using System.Text.Json.Nodes;

namespace HvBackup.Mcp;

/// <summary>
/// Набор инструментов сервера: чтение состояния, копирование, удаление копий,
/// работа с расписанием и восстановление.
/// <para>
/// Один и тот же набор доступен и в MCP-режиме (stdio), и в REST-режиме
/// с документацией OpenAPI/Swagger.
/// </para>
/// </summary>
public static class ToolRegistry
{
    /// <summary>Все инструменты сервера.</summary>
    public static IReadOnlyList<ToolDef> All { get; } = Build();

    public static ToolDef? Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<ToolDef> Build()
    {
        return new List<ToolDef>
        {
            ListVms(),
            ListBackups(),
            CreateBackup(),
            DeleteBackup(),
            DeleteAllBackups(),
            GetSchedule(),
            SetSchedule(),
            RestoreBackup(),
            GetSettings(),
            UpdateSettings(),
            GetStatus(),
            GetLogs(),
            GetJob(),
            ListJobs()
        };
    }

    // ─────────────────────────────── Чтение ───────────────────────────────

    private static ToolDef ListVms() => new()
    {
        Name = "list_vms",
        Title = "Список виртуальных машин",
        Description =
            "Возвращает список виртуальных машин Hyper-V на этом компьютере: имя, состояние, " +
            "число процессоров, память, диски, тип контрольной точки и имя сетевого коммутатора. " +
            "Нужны права администратора: без них Hyper-V недоступен и приходит понятная ошибка.",
        Method = "GET",
        Path = "/api/vms",
        Tag = "Виртуальные машины",
        Schema = Schema.Object(),
        Handler = (_, _, _) =>
        {
            var (ok, vms, err) = HyperV.ListVms();
            if (!ok) return Task.FromResult(ToolResult.Fail(HyperVFriendly(err)));

            var items = new JsonArray();
            foreach (var vm in vms)
            {
                items.Add(new JsonObject
                {
                    ["name"] = vm.Name,
                    ["id"] = vm.Id,
                    ["state"] = vm.State,
                    ["generation"] = vm.Generation,
                    ["processorCount"] = vm.ProcessorCount,
                    ["memoryBytes"] = vm.MemoryStartupBytes,
                    ["memoryText"] = vm.MemoryText,
                    ["checkpointType"] = vm.CheckpointType,
                    ["switchName"] = vm.SwitchName,
                    ["macAddress"] = vm.MacAddress,
                    ["diskSizeBytes"] = vm.DiskSizeBytes,
                    ["diskSizeText"] = vm.DiskSizeText,
                    ["disks"] = new JsonArray(Array.ConvertAll(vm.Disks.ToArray(), d => (JsonNode?)d))
                });
            }

            return Task.FromResult(ToolResult.Success(new JsonObject
            {
                ["count"] = vms.Count,
                ["vms"] = items
            }));
        }
    };

    private static ToolDef ListBackups() => new()
    {
        Name = "list_backups",
        Title = "Список резервных копий",
        Description =
            "Ищет копии во всех папках копий (общей и индивидуальных для отдельных ВМ) и возвращает " +
            "список: имя машины, дату, тип (экспорт Hyper-V или контрольная точка), объём, папку и " +
            "описание консистентности. Необязательный параметр vm ограничивает список копиями одной машины. " +
            "Находятся и копии машин, которых больше нет в Hyper-V.",
        Method = "GET",
        Path = "/api/backups",
        Tag = "Копии",
        Query = new[] { "vm" },
        Schema = Schema.Object(
            ("vm", Schema.Str("Имя виртуальной машины — показывать только её копии (необязательно)."), false)),
        Handler = (ctx, args, _) =>
        {
            var settings = ctx.ReloadSettings();
            var copies = RestoreEngine.Scan(settings);
            var vm = Str(args, "vm");

            if (vm.Length > 0)
                copies = copies.Where(c => string.Equals(c.VmName, vm, StringComparison.OrdinalIgnoreCase)).ToList();

            return Task.FromResult(ToolResult.Success(new JsonObject
            {
                ["count"] = copies.Count,
                ["backups"] = new JsonArray(copies.Select(c => (JsonNode)CopyToJson(c)).ToArray())
            }));
        }
    };

    private static ToolDef CreateBackup() => new()
    {
        Name = "create_backup",
        Title = "Сделать резервную копию",
        Description =
            "Делает горячую резервную копию виртуальной машины — машина продолжает работать, " +
            "остановки нет. По умолчанию копирование идёт в фоне: возвращается идентификатор задания, " +
            "состояние которого смотрит инструмент get_job. Если задать wait=true, вызов дождётся " +
            "окончания копии (это может занять часы при большой машине). Нужны права администратора.",
        Method = "POST",
        Path = "/api/backups",
        Tag = "Копии",
        Schema = Schema.Object(
            ("vm", Schema.Str("Имя виртуальной машины для копирования."), true),
            ("wait", Schema.Bool("Ждать окончания копии (false — копировать в фоне)."), false)),
        Handler = async (ctx, args, ct) =>
        {
            var vm = Str(args, "vm");
            if (vm.Length == 0) return ToolResult.Fail("Не указано имя виртуальной машины (vm).");

            var wait = Bool(args, "wait");
            var settings = ctx.ReloadSettings();

            var job = ctx.Jobs.StartBackup(vm, settings);
            if (!wait)
            {
                return ToolResult.Success(new JsonObject
                {
                    ["jobId"] = job.Id,
                    ["status"] = job.StatusText,
                    ["vm"] = vm,
                    ["message"] = "Копирование запущено в фоне. Состояние — get_job."
                });
            }

            var result = await job.WaitAsync(ct).ConfigureAwait(false);
            return result.Ok
                ? ToolResult.Success(new JsonObject
                {
                    ["jobId"] = job.Id,
                    ["status"] = job.StatusText,
                    ["vm"] = vm,
                    ["folder"] = result.Folder,
                    ["bytes"] = result.Bytes,
                    ["sizeText"] = ThrottledCopier.FormatBytes(result.Bytes),
                    ["durationSeconds"] = Math.Round(result.Duration.TotalSeconds),
                    ["consistency"] = result.Consistency,
                    ["removedOldCopies"] = result.RemovedOld
                })
                : ToolResult.Fail(result.Error.Length > 0 ? result.Error : "Копирование не удалось.");
        }
    };

    private static ToolDef DeleteBackup() => new()
    {
        Name = "delete_backup",
        Title = "Удалить резервную копию",
        Description =
            "Удаляет конкретную резервную копию. Копию можно указать папкой (folder — точный путь, " +
            "его возвращает list_backups) либо парой «имя машины + дата копии» (vm и date в формате " +
            "ГГГГ-ММ-ДД_ЧЧ-ММ-СС). Действие необратимо.",
        Method = "DELETE",
        Path = "/api/backups",
        Tag = "Копии",
        Schema = Schema.Object(
            ("folder", Schema.Str("Папка копии (точный путь)."), false),
            ("vm", Schema.Str("Имя машины — вместе с параметром date."), false),
            ("date", Schema.Str("Дата копии в формате ГГГГ-ММ-ДД_ЧЧ-ММ-СС."), false)),
        Handler = (ctx, args, _) =>
        {
            var settings = ctx.ReloadSettings();
            var folder = Str(args, "folder");
            var vm = Str(args, "vm");
            var date = Str(args, "date");

            if (folder.Length == 0 && (vm.Length == 0 || date.Length == 0))
                return Task.FromResult(ToolResult.Fail(
                    "Укажите папку копии (folder) либо имя машины (vm) и дату копии (date)."));

            if (folder.Length == 0)
            {
                var copies = RestoreEngine.Scan(settings)
                    .Where(c => string.Equals(c.VmName, vm, StringComparison.OrdinalIgnoreCase))
                    .Where(c => c.Created.ToString("yyyy-MM-dd_HH-mm-ss").Equals(date, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (copies.Count == 0)
                    return Task.FromResult(ToolResult.Fail($"Копия не найдена: {vm}, {date}."));
                folder = copies[0].Folder;
            }

            var removed = RestoreEngine.DeleteCopy(folder, msg => ctx.Log(msg));
            return Task.FromResult(removed
                ? ToolResult.Success(new JsonObject { ["deleted"] = true, ["folder"] = folder })
                : ToolResult.Fail("Не удалось удалить копию: " + folder));
        }
    };

    private static ToolDef DeleteAllBackups() => new()
    {
        Name = "delete_all_backups",
        Title = "Удалить все копии виртуальной машины",
        Description =
            "Удаляет все найденные копии указанной виртуальной машины (либо все копии, если vm не задан — " +
            "тогда нужно указать all=true). Действие необратимо.",
        Method = "DELETE",
        Path = "/api/backups/all",
        Tag = "Копии",
        Schema = Schema.Object(
            ("vm", Schema.Str("Имя виртуальной машины, чьи копии удалить."), false),
            ("all", Schema.Bool("Удалить вообще все копии, независимо от машины."), false)),
        Handler = (ctx, args, _) =>
        {
            var settings = ctx.ReloadSettings();
            var vm = Str(args, "vm");
            var all = Bool(args, "all");

            if (vm.Length == 0 && !all)
                return Task.FromResult(ToolResult.Fail("Укажите имя машины (vm) или all=true."));

            var copies = RestoreEngine.Scan(settings);
            if (vm.Length > 0)
                copies = copies.Where(c => string.Equals(c.VmName, vm, StringComparison.OrdinalIgnoreCase)).ToList();

            var deleted = 0;
            var failed = new JsonArray();
            foreach (var copy in copies)
                if (RestoreEngine.DeleteCopy(copy.Folder, msg => ctx.Log(msg))) deleted++;
                else failed.Add(copy.Folder);

            return Task.FromResult(ToolResult.Success(new JsonObject
            {
                ["deleted"] = deleted,
                ["failed"] = failed.Count,
                ["failedFolders"] = failed
            }));
        }
    };

    // ─────────────────────────────── Расписание ───────────────────────────────

    private static ToolDef GetSchedule() => new()
    {
        Name = "get_schedule",
        Title = "Показать расписание копирования",
        Description =
            "Возвращает текущее расписание копирования: общее для всех машин и, если указана машина (vm), " +
            "собственное расписание этой ВМ. Режимы: disabled (выключено), daily (ежедневно), " +
            "every_minutes (каждые N минут), weekly (еженедельно в заданный день).",
        Method = "GET",
        Path = "/api/schedule",
        Tag = "Расписание",
        Query = new[] { "vm" },
        Schema = Schema.Object(
            ("vm", Schema.Str("Имя виртуальной машины — показать её собственное расписание (необязательно)."), false)),
        Handler = (ctx, args, _) =>
        {
            var settings = ctx.ReloadSettings();
            var vm = Str(args, "vm");

            var result = new JsonObject
            {
                ["global"] = ScheduleToJson(settings, null)
            };

            if (vm.Length > 0)
            {
                var perVm = settings.PerVmSettings(vm);
                result["perVm"] = perVm == null
                    ? null
                    : ScheduleToJson(settings, perVm);
                result["hasOwnSchedule"] = perVm?.UseOwnSchedule == true;
            }

            var selected = new JsonArray();
            foreach (var name in settings.SelectedVms) selected.Add(name);
            result["selectedVms"] = selected;

            return Task.FromResult(ToolResult.Success(result));
        }
    };

    private static ToolDef SetSchedule() => new()
    {
        Name = "set_schedule",
        Title = "Изменить расписание копирования",
        Description =
            "Меняет время или периодичность копирования. Без параметра vm меняется общее расписание " +
            "для всех машин; с параметром vm — собственное расписание этой машины (остальные общие " +
            "настройки при этом не меняются). Виды расписания: disabled, daily (час и минута), " +
            "every_minutes (интервал в минутах), weekly (день недели 0-6, час и минута). " +
            "Изменения сразу применяются работающей программой.",
        Method = "PUT",
        Path = "/api/schedule",
        Tag = "Расписание",
        Schema = Schema.Object(
            ("vm", Schema.Str("Имя виртуальной машины (необязательно) — задать расписание только ей."), false),
            ("kind", Schema.Enum("Вид расписания.", new[] { "disabled", "daily", "every_minutes", "weekly" }), true),
            ("hour", Schema.Int("Час запуска (0-23).", null, 0, 23), false),
            ("minute", Schema.Int("Минута запуска (0-59).", null, 0, 59), false),
            ("everyMinutes", Schema.Int("Интервал в минутах для вида every_minutes.", null, 1, 43200), false),
            ("weeklyDay", Schema.Int("День недели для вида weekly: 0 — воскресенье, 6 — суббота.", null, 0, 6), false)),
        Handler = (ctx, args, _) =>
        {
            var settings = ctx.ReloadSettings();
            var vm = Str(args, "vm");
            var kindText = Str(args, "kind").ToLowerInvariant();

            if (!TryParseKind(kindText, out var kind))
                return Task.FromResult(ToolResult.Fail(
                    "Неизвестный вид расписания: " + kindText + ". Допустимо: disabled, daily, every_minutes, weekly."));

            var hour = Int(args, "hour", settings.Hour);
            var minute = Int(args, "minute", settings.Minute);
            var every = Int(args, "everyMinutes", settings.EveryMinutes);
            var day = Int(args, "weeklyDay", settings.WeeklyDay);

            if (vm.Length > 0)
            {
                var perVm = settings.PerVmSettings(vm) ?? new VmSettings();
                perVm.UseOwnSchedule = true;
                perVm.Schedule = kind;
                perVm.Hour = Math.Clamp(hour, 0, 23);
                perVm.Minute = Math.Clamp(minute, 0, 59);
                perVm.EveryMinutes = Math.Clamp(every, 1, 43200);
                perVm.WeeklyDay = Math.Clamp(day, 0, 6);
                settings.SetPerVm(vm, perVm);
            }
            else
            {
                settings.Schedule = kind;
                settings.Hour = Math.Clamp(hour, 0, 23);
                settings.Minute = Math.Clamp(minute, 0, 59);
                settings.EveryMinutes = Math.Clamp(every, 1, 43200);
                settings.WeeklyDay = Math.Clamp(day, 0, 6);
            }

            ctx.SaveSettings(settings);

            var effective = vm.Length > 0 ? settings.ForVm(vm) : settings;
            return Task.FromResult(ToolResult.Success(new JsonObject
            {
                ["scope"] = vm.Length > 0 ? vm : "все машины",
                ["schedule"] = ScheduleToJson(effective, vm.Length > 0 ? settings.PerVmSettings(vm) : null),
                ["description"] = ScheduleText.Describe(effective)
            }));
        }
    };

    // ─────────────────────────────── Восстановление ───────────────────────────────

    private static ToolDef RestoreBackup() => new()
    {
        Name = "restore_backup",
        Title = "Восстановить виртуальную машину из копии",
        Description =
            "Восстанавливает виртуальную машину из найденной копии средствами Hyper-V. Копию задают " +
            "папкой (folder, её возвращает list_backups). Имя новой машины — vmName (если не указать, " +
            "возьмётся имя из копии). destination — папка, куда развернуть машину; внутри неё программа " +
            "сама разложит диски и конфигурацию. generateNewId создаёт машину с новым идентификатором " +
            "(исходная ВМ может остаться на месте). Нужны права администратора.",
        Method = "POST",
        Path = "/api/restore",
        Tag = "Восстановление",
        Schema = Schema.Object(
            ("folder", Schema.Str("Папка копии, из которой восстанавливаем."), true),
            ("vmName", Schema.Str("Имя восстанавливаемой машины (необязательно)."), false),
            ("destination", Schema.Str("Папка для восстановления (необязательно)."), false),
            ("generateNewId", Schema.Bool("Создать машину с новым идентификатором."), false),
            ("start", Schema.Bool("Запустить машину после восстановления."), false)),
        Handler = async (ctx, args, ct) =>
        {
            var folder = Str(args, "folder");
            if (folder.Length == 0) return ToolResult.Fail("Не указана папка копии (folder).");

            var settings = ctx.ReloadSettings();
            var copies = RestoreEngine.Scan(settings);
            var copy = copies.FirstOrDefault(c => string.Equals(c.Folder, folder, StringComparison.OrdinalIgnoreCase));
            if (copy == null)
                return ToolResult.Fail("Копия не найдена: " + folder + ". Посмотрите список list_backups.");

            var name = Str(args, "vmName");
            if (name.Length == 0) name = copy.VmName;

            var destination = Str(args, "destination");
            if (destination.Length == 0)
            {
                var (okPaths, _, hostVhdPath, _) = await Task.Run(() => HyperV.GetHostPaths()).ConfigureAwait(false);
                if (!okPaths || hostVhdPath.Length == 0)
                    return ToolResult.Fail(
                        "Не удалось узнать папку дисков Hyper-V. Укажите destination вручную " +
                        "и запустите сервер от имени администратора.");

                destination = Path.Combine(hostVhdPath, "HvRestore", BackupEngine.SafeName(name));
            }

            destination = RestoreEngine.UniqueFolder(destination);
            var (vhdPath, vmPath) = RestoreEngine.SplitDestination(destination, copy.IsExport);

            var options = new RestoreOptions
            {
                VmName = name,
                VhdPath = vhdPath,
                VmPath = vmPath,
                GenerateNewId = Bool(args, "generateNewId", true),
                StartAfter = Bool(args, "start")
            };

            var result = await RestoreEngine
                .RestoreAsync(copy, options, msg => ctx.Log(msg), null, ct)
                .ConfigureAwait(false);

            return result.Ok
                ? ToolResult.Success(new JsonObject
                {
                    ["vmName"] = result.VmName,
                    ["vmId"] = result.VmId,
                    ["destination"] = destination,
                    ["kind"] = copy.KindText
                })
                : ToolResult.Fail(result.Error);
        }
    };

    // ─────────────────────────────── Настройки и состояние ───────────────────────────────

    private static ToolDef GetSettings() => new()
    {
        Name = "get_settings",
        Title = "Показать настройки программы",
        Description =
            "Возвращает текущие настройки программы: папка копий, сколько копий хранить, " +
            "ограничение скорости, режим копирования, папка временного экспорта, расписание, " +
            "сетевая папка и FTP (без паролей), список машин в автобэкапе и индивидуальные настройки ВМ.",
        Method = "GET",
        Path = "/api/settings",
        Tag = "Настройки",
        Schema = Schema.Object(),
        Handler = (ctx, _, _) =>
        {
            var settings = ctx.ReloadSettings();
            return Task.FromResult(ToolResult.Success(SettingsToJson(settings)));
        }
    };

    private static ToolDef UpdateSettings() => new()
    {
        Name = "update_settings",
        Title = "Изменить настройки программы",
        Description =
            "Меняет общие настройки программы. Менять можно только перечисленные поля — остальные " +
            "останутся как были. Папка копий (backupRoot), число хранимых копий (keepCopies), " +
            "ограничение скорости в МБ/с (speedLimitMBps, 0 — без ограничения), режим копирования " +
            "(mode: checkpoint_throttled, vm_export, hot_export), папка временного экспорта " +
            "(exportTempRoot), хранение локальной копии после отправки в сеть (keepLocalCopy), " +
            "срок хранения журнала в днях (logRetentionDays), включение автобэкапа для машин (selectedVms).",
        Method = "PUT",
        Path = "/api/settings",
        Tag = "Настройки",
        Schema = Schema.Object(
            ("backupRoot", Schema.Str("Папка для копий."), false),
            ("keepCopies", Schema.Int("Сколько копий хранить на каждую машину.", null, 1, 999), false),
            ("speedLimitMBps", Schema.Int("Ограничение скорости копирования, МБ/с (0 — без ограничения).", null, 0, 100000), false),
            ("mode", Schema.Enum("Режим копирования.", new[] { "checkpoint_throttled", "vm_export", "hot_export" }), false),
            ("exportTempRoot", Schema.Str("Папка для временного экспорта Hyper-V."), false),
            ("keepLocalCopy", Schema.Bool("Хранить локальную копию после отправки в сеть/FTP."), false),
            ("logRetentionDays", Schema.Int("Срок хранения файлов журнала в днях (0 — хранить всё).", null, 0, 3650), false),
            ("selectedVms", Schema.Str("Машины для автобэкапа через запятую."), false)),
        Handler = (ctx, args, _) =>
        {
            var settings = ctx.ReloadSettings();
            var changed = new JsonArray();

            if (Has(args, "backupRoot"))
            {
                settings.BackupRoot = Str(args, "backupRoot").Trim();
                changed.Add("backupRoot");
            }
            if (Has(args, "keepCopies"))
            {
                settings.KeepCopies = Math.Clamp(Int(args, "keepCopies", settings.KeepCopies), 1, 999);
                changed.Add("keepCopies");
            }
            if (Has(args, "speedLimitMBps"))
            {
                settings.SpeedLimitMBps = Math.Clamp(Int(args, "speedLimitMBps", settings.SpeedLimitMBps), 0, 100000);
                changed.Add("speedLimitMBps");
            }
            if (Has(args, "mode"))
            {
                var mode = Str(args, "mode").ToLowerInvariant();
                if (!TryParseMode(mode, out var parsed))
                    return Task.FromResult(ToolResult.Fail(
                        "Неизвестный режим копирования: " + mode +
                        ". Допустимо: checkpoint_throttled, vm_export, hot_export."));
                settings.Mode = parsed;
                changed.Add("mode");
            }
            if (Has(args, "exportTempRoot"))
            {
                settings.ExportTempRoot = Str(args, "exportTempRoot").Trim();
                changed.Add("exportTempRoot");
            }
            if (Has(args, "keepLocalCopy"))
            {
                settings.KeepLocalCopy = Bool(args, "keepLocalCopy", settings.KeepLocalCopy);
                changed.Add("keepLocalCopy");
            }
            if (Has(args, "logRetentionDays"))
            {
                settings.LogRetentionDays = Math.Clamp(Int(args, "logRetentionDays", settings.LogRetentionDays), 0, 3650);
                changed.Add("logRetentionDays");
            }
            if (Has(args, "selectedVms"))
            {
                var list = Str(args, "selectedVms")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                settings.SelectedVms = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                changed.Add("selectedVms");
            }

            if (changed.Count == 0)
                return Task.FromResult(ToolResult.Fail("Не указано ни одно поле для изменения."));

            ctx.SaveSettings(settings);
            return Task.FromResult(ToolResult.Success(new JsonObject
            {
                ["changed"] = changed,
                ["settings"] = SettingsToJson(settings)
            }));
        }
    };

    private static ToolDef GetStatus() => new()
    {
        Name = "get_status",
        Title = "Состояние программы и доступ к Hyper-V",
        Description =
            "Показывает, запущена ли программа, есть ли доступ к Hyper-V (без прав администратора " +
            "операции с Hyper-V работать не будут), где лежат настройки и копии, сколько копий найдено " +
            "и какие фоновые задания выполняются.",
        Method = "GET",
        Path = "/api/status",
        Tag = "Состояние",
        Schema = Schema.Object(),
        Handler = (ctx, _, _) =>
        {
            var settings = ctx.ReloadSettings();
            var (okAccess, accessError) = HyperV.CheckAccess();
            var copies = RestoreEngine.Scan(settings);

            return Task.FromResult(ToolResult.Success(new JsonObject
            {
                ["version"] = McpVersion.Value,
                ["elevated"] = IsElevated(),
                ["hyperVAvailable"] = okAccess,
                ["hyperVError"] = okAccess ? "" : HyperVFriendly(accessError),
                ["settingsPath"] = SettingsStore.SettingsPath,
                ["backupRoot"] = settings.BackupRoot,
                ["copiesFound"] = copies.Count,
                ["runningJobs"] = ctx.Jobs.Count,
                ["note"] = okAccess
                    ? "Доступ к Hyper-V есть."
                    : "Операции с Hyper-V требуют запуска сервера от имени администратора."
            }));
        }
    };

    private static ToolDef GetLogs() => new()
    {
        Name = "get_logs",
        Title = "Показать журнал работы",
        Description =
            "Возвращает последние строки журнала программы (файл %ProgramData%\\HvBackup\\log-ГГГГ-ММ.txt). " +
            "Строки отдаются в порядке возрастания времени; по умолчанию — последние 100.",
        Method = "GET",
        Path = "/api/logs",
        Tag = "Журнал",
        Query = new[] { "lines" },
        Schema = Schema.Object(
            ("lines", Schema.Int("Сколько последних строк вернуть.", 100, 1, 5000), false)),
        Handler = (ctx, args, _) =>
        {
            var lines = Math.Clamp(Int(args, "lines", 100), 1, 5000);
            var file = Path.Combine(SettingsStore.DataDir, $"log-{DateTime.Now:yyyy-MM}.txt");

            var all = new List<string>();
            try
            {
                if (File.Exists(file))
                    all.AddRange(File.ReadAllLines(file));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail("Не удалось прочитать журнал: " + ex.Message));
            }

            var tail = all.Skip(Math.Max(0, all.Count - lines)).ToList();
            return Task.FromResult(ToolResult.Success(new JsonObject
            {
                ["file"] = file,
                ["totalLines"] = all.Count,
                ["lines"] = new JsonArray(tail.Select(l => (JsonNode?)l).ToArray())
            }));
        }
    };

    private static ToolDef GetJob() => new()
    {
        Name = "get_job",
        Title = "Состояние фоновой операции",
        Description =
            "Возвращает состояние фонового задания (например, запущенной копии): текущая фаза, " +
            "сколько байтов скопировано, скорость, признак успеха и текст ошибки, если что-то пошло не так.",
        Method = "GET",
        Path = "/api/jobs/{jobId}",
        Tag = "Копии",
        Schema = Schema.Object(
            ("jobId", Schema.Str("Идентификатор задания (его вернул create_backup)."), true)),
        Handler = (ctx, args, _) =>
        {
            var id = Str(args, "jobId");
            var job = ctx.Jobs.Find(id);
            return Task.FromResult(job == null
                ? ToolResult.Fail("Задание не найдено: " + id)
                : ToolResult.Success(job.ToJson()));
        }
    };

    private static ToolDef ListJobs() => new()
    {
        Name = "list_jobs",
        Title = "Список фоновых операций",
        Description = "Возвращает список фоновых заданий сервера (запущенных копий) и их состояние.",
        Method = "GET",
        Path = "/api/jobs",
        Tag = "Копии",
        Schema = Schema.Object(),
        Handler = (ctx, _, _) =>
        {
            var items = new JsonArray();
            foreach (var job in ctx.Jobs.All) items.Add(job.ToJson());
            return Task.FromResult(ToolResult.Success(new JsonObject
            {
                ["count"] = items.Count,
                ["jobs"] = items
            }));
        }
    };

    // ─────────────────────────────── Помощники ───────────────────────────────

    public static bool TryParseKind(string text, out ScheduleKind kind)
    {
        switch (text.ToLowerInvariant())
        {
            case "disabled": kind = ScheduleKind.Disabled; return true;
            case "daily": kind = ScheduleKind.Daily; return true;
            case "every_minutes": kind = ScheduleKind.EveryMinutes; return true;
            case "weekly": kind = ScheduleKind.Weekly; return true;
            default: kind = ScheduleKind.Daily; return false;
        }
    }

    public static bool TryParseMode(string text, out CopyMode mode)
    {
        switch (text.ToLowerInvariant())
        {
            case "checkpoint_throttled": mode = CopyMode.CheckpointThrottled; return true;
            case "vm_export": mode = CopyMode.VmExport; return true;
            case "hot_export": mode = CopyMode.HotExport; return true;
            default: mode = CopyMode.HotExport; return false;
        }
    }

    private static JsonObject CopyToJson(BackupCopyInfo c) => new()
    {
        ["vmName"] = c.VmName,
        ["folder"] = c.Folder,
        ["created"] = c.Created == default ? "" : c.Created.ToString("yyyy-MM-dd HH:mm:ss"),
        ["createdStamp"] = c.Created == default ? "" : c.Created.ToString("yyyy-MM-dd_HH-mm-ss"),
        ["sizeBytes"] = c.SizeBytes,
        ["sizeText"] = c.SizeText,
        ["kind"] = c.Kind,
        ["kindText"] = c.KindText,
        ["consistency"] = c.Consistency
    };

    private static JsonObject ScheduleToJson(AppSettings s, VmSettings? perVm)
    {
        var kind = perVm?.UseOwnSchedule == true ? perVm!.Schedule : s.Schedule;
        var hour = perVm?.UseOwnSchedule == true ? perVm!.Hour : s.Hour;
        var minute = perVm?.UseOwnSchedule == true ? perVm!.Minute : s.Minute;
        var every = perVm?.UseOwnSchedule == true ? perVm!.EveryMinutes : s.EveryMinutes;
        var day = perVm?.UseOwnSchedule == true ? perVm!.WeeklyDay : s.WeeklyDay;

        return new JsonObject
        {
            ["kind"] = KindName(kind),
            ["hour"] = hour,
            ["minute"] = minute,
            ["everyMinutes"] = every,
            ["weeklyDay"] = day,
            ["weekDayName"] = ScheduleText.DayFull(day),
            ["description"] = ScheduleText.Describe(kind, every, hour, minute, day)
        };
    }

    public static string KindName(ScheduleKind kind) => kind switch
    {
        ScheduleKind.Disabled => "disabled",
        ScheduleKind.Daily => "daily",
        ScheduleKind.EveryMinutes => "every_minutes",
        ScheduleKind.Weekly => "weekly",
        _ => "disabled"
    };

    public static string ModeName(CopyMode mode) => mode switch
    {
        CopyMode.CheckpointThrottled => "checkpoint_throttled",
        CopyMode.VmExport => "vm_export",
        CopyMode.HotExport => "hot_export",
        _ => "hot_export"
    };

    private static JsonObject SettingsToJson(AppSettings s)
    {
        var perVm = new JsonArray();
        foreach (var pair in s.PerVm)
        {
            perVm.Add(new JsonObject
            {
                ["vmName"] = pair.Key,
                ["useOwnSchedule"] = pair.Value.UseOwnSchedule,
                ["schedule"] = KindName(pair.Value.Schedule),
                ["hour"] = pair.Value.Hour,
                ["minute"] = pair.Value.Minute,
                ["everyMinutes"] = pair.Value.EveryMinutes,
                ["weeklyDay"] = pair.Value.WeeklyDay,
                ["speedLimitMBps"] = pair.Value.SpeedLimitMBps,
                ["keepCopies"] = pair.Value.KeepCopies,
                ["mode"] = pair.Value.Mode.HasValue ? ModeName(pair.Value.Mode.Value) : null,
                ["backupRoot"] = pair.Value.BackupRoot,
                ["keepLocalCopy"] = pair.Value.KeepLocalCopy
            });
        }

        var selected = new JsonArray();
        foreach (var name in s.SelectedVms) selected.Add(name);

        return new JsonObject
        {
            ["backupRoot"] = s.BackupRoot,
            ["keepCopies"] = s.KeepCopies,
            ["speedLimitMBps"] = s.SpeedLimitMBps,
            ["mode"] = ModeName(s.Mode),
            ["exportTempRoot"] = s.ExportTempRoot,
            ["keepLocalCopy"] = s.KeepLocalCopy,
            ["logRetentionDays"] = s.LogRetentionDays,
            ["minimizeToTray"] = s.MinimizeToTray,
            ["runOnStart"] = s.RunOnStart,
            ["useProductionCheckpoint"] = s.UseProductionCheckpoint,
            ["networkEnabled"] = s.NetworkEnabled,
            ["networkPath"] = s.NetworkPath,
            ["networkUser"] = s.NetworkUser,
            ["ftpEnabled"] = s.FtpEnabled,
            ["ftpHost"] = s.FtpHost,
            ["ftpPort"] = s.FtpPort,
            ["ftpUser"] = s.FtpUser,
            ["ftpRemoteDir"] = s.FtpRemoteDir,
            ["schedule"] = ScheduleToJson(s, null),
            ["selectedVms"] = selected,
            ["perVm"] = perVm,
            ["settingsPath"] = SettingsStore.SettingsPath
        };
    }

    private static string Str(JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node == null) return "";
        return node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => node.GetValue<string>(),
            System.Text.Json.JsonValueKind.Number => node.ToJsonString(),
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            _ => ""
        };
    }

    private static int Int(JsonObject args, string name, int fallback)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node == null) return fallback;
        try
        {
            if (node.GetValueKind() == System.Text.Json.JsonValueKind.Number) return node.GetValue<int>();
            if (int.TryParse(node.ToString(), out var parsed)) return parsed;
        }
        catch
        {
            // не число — берём значение по умолчанию
        }
        return fallback;
    }

    private static bool Bool(JsonObject args, string name, bool fallback = false)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node == null) return fallback;
        try
        {
            return node.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.String => bool.TryParse(node.GetValue<string>(), out var parsed) && parsed,
                System.Text.Json.JsonValueKind.Number => node.GetValue<int>() != 0,
                _ => fallback
            };
        }
        catch
        {
            return fallback;
        }
    }

    private static bool Has(JsonObject args, string name) =>
        args.TryGetPropertyValue(name, out var node) && node != null;

    /// <summary>Понятный текст ошибки Hyper-V (в том числе про права администратора).</summary>
    internal static string HyperVFriendly(string error)
    {
        var text = (error ?? "").Trim();
        var low = text.ToLowerInvariant();

        if (low.Contains("access is denied") || low.Contains("нет разрешения") || low.Contains("не разрешено") ||
            low.Contains("accessdenied") || low.Contains("отказано в доступе"))
            return "Нет прав доступа к Hyper-V. Запустите сервер от имени администратора: " + Short(text);

        if (low.Contains("no hyper-v") || low.Contains("hyper-v не установлен"))
            return "Hyper-V не установлен на этом компьютере: " + Short(text);

        return Short(text);
    }

    private static string Short(string text) =>
        text.Length > 400 ? text[..400] + "…" : text;

    /// <summary>Запущена ли программа с правами администратора.</summary>
    internal static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
