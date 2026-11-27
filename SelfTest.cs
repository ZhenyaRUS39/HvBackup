using System.Security.Cryptography;
using HvBackup.Mcp;
using HvBackup.Ui;

namespace HvBackup;

/// <summary>Самопроверка: троттлинг, ротация, расписание, синтаксис сценариев, генерация restore.ps1.</summary>
public static class SelfTest
{
    private static int _failed;
    private static int _passed;

    public static int Run()
    {
        _failed = 0;
        _passed = 0;

        TestThrottledCopy();
        TestUnlimitedCopy();
        TestRotation();
        TestScheduler();
        TestSettingsJson();
        TestPsScriptSyntax();
        TestRestoreScript();
        TestPathHelpers();
        TestMockedHyperV();
        TestPlanBuilder();
        TestDpapi();
        TestFtpParsing();
        TestFtpLoopback();
        TestNetworkPaths();
        TestScheduleText();
        TestVmSizeText();
        TestLogCleanup();
        TestBadgeUpdate();
        TestProgressGate();
        TestPerVmSettings();
        TestPerVmSchedule();
        TestSchedulerFires();
        TestCopyProgress();
        TestExportProgress();
        TestProgressText();
        TestHotExport();
        TestRestoreScan();
        TestRestoreScripts();
        TestRestoreScriptNameParameter();
        TestRestoreHelpers();
        TestRestoreDestination();
        TestDeleteCopy();
        TestSettingsReload();
        TestHyperVAccess();
        TestMcp();

        AppLog.Out();
        AppLog.Out($"ИТОГО: успешно {_passed}, с ошибками {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _passed++; AppLog.Out($"  [OK]   {name}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _failed++; AppLog.Out($"  [FAIL] {name}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    private static void TestThrottledCopy()
    {
        AppLog.Out("Ограничение скорости копирования");
        var dir = Path.Combine(Path.GetTempPath(), "hvselftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "src.bin");
            var dst = Path.Combine(dir, "dst.bin");
            const int sizeMb = 32;
            var bytes = new byte[sizeMb * 1024 * 1024];
            Random.Shared.NextBytes(bytes);
            File.WriteAllBytes(src, bytes);

            const long limit = 8L * 1024 * 1024; // 8 МБ/с
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var done = ThrottledCopier.CopyFileAsync(src, dst, limit, null, CancellationToken.None).GetAwaiter().GetResult();
            sw.Stop();

            var expected = (double)sizeMb * 1024 * 1024 / limit;
            var actual = sw.Elapsed.TotalSeconds;
            var avg = done / Math.Max(0.001, sw.Elapsed.TotalSeconds);

            Check("файл скопирован целиком", done == bytes.Length, $"{done} байт");
            Check("средняя скорость не выше лимита", avg <= limit * 1.05, $"{ThrottledCopier.FormatBytes(avg)}/с при лимите {ThrottledCopier.FormatBytes(limit)}/с");
            Check("копирование действительно растянуто во времени", actual >= expected * 0.85,
                $"{actual:0.0} с при ожидаемых {expected:0.0} с");
            Check("содержимое идентично", Sha(src) == Sha(dst));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void TestUnlimitedCopy()
    {
        AppLog.Out("Копирование без ограничения скорости");
        var dir = Path.Combine(Path.GetTempPath(), "hvselftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "a.bin");
            var dst = Path.Combine(dir, "b.bin");
            var bytes = new byte[8 * 1024 * 1024];
            Random.Shared.NextBytes(bytes);
            File.WriteAllBytes(src, bytes);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ThrottledCopier.CopyFileAsync(src, dst, 0, null, CancellationToken.None).GetAwaiter().GetResult();
            sw.Stop();
            Check("быстрое копирование без задержек", sw.Elapsed.TotalSeconds < 10, $"{sw.Elapsed.TotalSeconds:0.0} с");
            Check("содержимое идентично", Sha(src) == Sha(dst));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void TestRotation()
    {
        AppLog.Out("Ротация копий (удаление самых старых)");
        var dir = Path.Combine(Path.GetTempPath(), "hvselftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < 5; i++)
                Directory.CreateDirectory(Path.Combine(dir, DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd_HH-mm-ss")));
            Directory.CreateDirectory(Path.Combine(dir, "не-копия"));

            var removed = BackupEngine.Rotate(dir, 3);
            var copiesLeft = Directory.GetDirectories(dir)
                .Select(d => Path.GetFileName(d)!)
                .Count(n => n.Length >= 19 &&
                            DateTime.TryParseExact(n[..19], "yyyy-MM-dd_HH-mm-ss", null, System.Globalization.DateTimeStyles.None, out _));
            Check("удалено лишних копий", removed == 2, $"удалено {removed}");
            Check("осталось нужное количество копий", copiesLeft == 3, $"осталось {copiesLeft}");
            Check("посторонние папки не тронуты", Directory.Exists(Path.Combine(dir, "не-копия")));
            var newest = Directory.GetDirectories(dir)
                .Select(d => Path.GetFileName(d)!)
                .Where(n => n.Length >= 19)
                .OrderByDescending(n => n, StringComparer.Ordinal)
                .First();
            Check("сохранены самые новые", newest.StartsWith(DateTime.Now.ToString("yyyy-MM-dd")), newest);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void TestScheduler()
    {
        AppLog.Out("Расписание");
        var now = new DateTime(2026, 11, 25, 12, 30, 0, DateTimeKind.Local);

        var s1 = new AppSettings { Schedule = ScheduleKind.Disabled };
        Check("выключено — нет следующего запуска", new BackupScheduler(s1).ComputeNext(now) == null);

        var s2 = new AppSettings { Schedule = ScheduleKind.EveryMinutes, EveryMinutes = 60 };
        var n2 = new BackupScheduler(s2).ComputeNext(now);
        Check("каждые 60 минут", n2 != null && Math.Abs((n2!.Value - now).TotalMinutes - 60) < 1, n2?.ToString() ?? "");

        var s3 = new AppSettings { Schedule = ScheduleKind.Daily, Hour = 3, Minute = 0 };
        var n3 = new BackupScheduler(s3).ComputeNext(now);
        Check("ежедневно в 03:00 → следующий день", n3 != null && n3!.Value.Date == now.Date.AddDays(1).Date && n3.Value.Hour == 3,
            n3?.ToString() ?? "");

        var s4 = new AppSettings { Schedule = ScheduleKind.Weekly, WeeklyDay = (int)DayOfWeek.Wednesday, Hour = 4, Minute = 30 };
        var n4 = new BackupScheduler(s4).ComputeNext(now);
        Check("еженедельно в среду 04:30", n4 != null && n4!.Value.DayOfWeek == DayOfWeek.Wednesday && n4.Value.Hour == 4,
            n4?.ToString() ?? "");

        var s5 = new AppSettings { Schedule = ScheduleKind.Daily, Hour = 23, Minute = 0 };
        var n5 = new BackupScheduler(s5).ComputeNext(now);
        Check("ежедневно в 23:00 → сегодня", n5 != null && n5!.Value.Date == now.Date, n5?.ToString() ?? "");
    }

    private static void TestSettingsJson()
    {
        AppLog.Out("Сохранение настроек");
        var s = new AppSettings
        {
            BackupRoot = @"D:\Копии\ВМ",
            KeepCopies = 7,
            SpeedLimitMBps = 55,
            Mode = CopyMode.CheckpointThrottled,
            Schedule = ScheduleKind.Weekly,
            WeeklyDay = 3,
            SelectedVms = new List<string> { "Тест-ВМ", "Вторая" }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Check("настройки переживают сериализацию",
            back != null && back.KeepCopies == 7 && back.SpeedLimitMBps == 55 &&
            back.SelectedVms.Count == 2 && back.WeeklyDay == 3);
    }

    private static void TestPsScriptSyntax()
    {
        AppLog.Out("Синтаксис сценариев PowerShell");
        var fields = typeof(PsScripts).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToList();
        foreach (var f in fields)
        {
            var body = (string)f.GetRawConstantValue()!;
            var (ok, err) = Ps.CheckSyntax(body);
            Check("скрипт " + f.Name, ok, err);
        }
    }

    private static void TestRestoreScript()
    {
        AppLog.Out("Генерация restore.ps1");
        var specs = new List<RestoreScript.DiskSpec>
        {
            new() { File = "Win10.vhdx", Source = @"D:\VM\Win10.vhdx", ParentFile = "", SizeBytes = 40L << 30 },
            new() { File = "Win10.avhdx", Source = @"D:\VM\Win10_1.avhdx", ParentFile = "Win10.vhdx", SizeBytes = 1L << 30 }
        };
        var script = RestoreScript.Build("Тест-ВМ", 2, 8L << 30, 4, "Default Switch", "00-15-5D-01-02-03",
            specs, "Win10.avhdx", "25.11.2026 21:00", "Production (VSS)");
        var (ok, err) = Ps.CheckSyntax(script);
        Check("restore.ps1 синтаксически корректен", ok, err);
        Check("restore.ps1 содержит New-VM", script.Contains("New-VM"));
        Check("restore.ps1 содержит Set-VHD", script.Contains("Set-VHD"));
        Check("имя ВМ подставлено", script.Contains("Тест-ВМ"));

        // ВМ с двумя виртуальными дисками: дополнительный диск должен подключаться отдельной командой.
        var specs2 = new List<RestoreScript.DiskSpec>
        {
            new() { File = "Win10.vhdx", Source = @"D:\VM\Win10.vhdx", SizeBytes = 40L << 30, ControllerNumber = 0, ControllerLocation = 0, IsTop = true },
            new() { File = "Data.vhdx", Source = @"D:\VM\Data.vhdx", SizeBytes = 10L << 30, ControllerNumber = 0, ControllerLocation = 1, IsTop = true }
        };
        var script2 = RestoreScript.Build("ДвеДиска", 2, 4L << 30, 2, "Default Switch", "",
            specs2, "Win10.vhdx", "25.11.2026 21:00", "Production");
        var (ok2, err2) = Ps.CheckSyntax(script2);
        Check("restore.ps1 для двух дисков синтаксически корректен", ok2, err2);
        Check("дополнительный диск подключается через Add-VMHardDiskDrive",
            script2.Contains("Add-VMHardDiskDrive") && script2.Contains("Data.vhdx"));

        var readme = RestoreScript.BuildReadme("Тест-ВМ", "25.11.2026 21:00", "Production", specs, "Win10.avhdx");
        Check("README содержит инструкцию", readme.Contains("restore.ps1"));
    }

    private static void TestPathHelpers()
    {
        AppLog.Out("Работа с путями");
        Check("недопустимые символы заменяются", !BackupEngine.SafeName("VM: test/1").Contains(':'));
        Check("обычное имя не меняется", BackupEngine.SafeName("Win10-Test") == "Win10-Test");
        Check("сравнение путей", BackupEngine.SamePath(@"C:\a\b.vhdx", @"C:\A\B.VHDX"));
        Check("разные пути не равны", !BackupEngine.SamePath(@"C:\a\b.vhdx", @"C:\a\c.vhdx"));
    }

    private static void TestScheduleText()
    {
        AppLog.Out("Описание цикла расписания");

        var off = new AppSettings { Schedule = ScheduleKind.Disabled };
        Check("выключенное расписание не активно", !ScheduleText.IsActive(off));
        Check("у выключенного расписания пустой бейдж", ScheduleText.Badge(off) == "");
        Check("выключенное расписание описывается словом «выключено»", ScheduleText.Describe(off) == "выключено");

        var every = new AppSettings { Schedule = ScheduleKind.EveryMinutes, EveryMinutes = 60 };
        Check("описание интервала в минутах", ScheduleText.Describe(every).Contains("60 мин"), ScheduleText.Describe(every));
        Check("бейдж интервала короткий", ScheduleText.Badge(every) == "60 мин", ScheduleText.Badge(every));

        var daily = new AppSettings { Schedule = ScheduleKind.Daily, Hour = 3, Minute = 5 };
        Check("описание ежедневного запуска со временем", ScheduleText.Describe(daily).Contains("03:05"), ScheduleText.Describe(daily));
        Check("бейдж ежедневного запуска", ScheduleText.Badge(daily) == "ежедн. 03:05", ScheduleText.Badge(daily));

        var weekly = new AppSettings { Schedule = ScheduleKind.Weekly, WeeklyDay = 3, Hour = 4, Minute = 30 };
        Check("описание недельного запуска с днём и временем",
            ScheduleText.Describe(weekly).Contains("среду") && ScheduleText.Describe(weekly).Contains("04:30"),
            ScheduleText.Describe(weekly));
        Check("бейдж недельного запуска короткий", ScheduleText.Badge(weekly) == "ср 04:30", ScheduleText.Badge(weekly));
        Check("активное расписание отмечается", ScheduleText.IsActive(weekly));
    }

    private static void TestVmSizeText()
    {
        AppLog.Out("Размер виртуальной машины");
        Check("без размера — прочерк", new VmInfo().DiskSizeText == "—");
        Check("размер показывается в гигабайтах", new VmInfo { DiskSizeBytes = 42L << 30 }.DiskSizeText.Contains("ГБ"),
            new VmInfo { DiskSizeBytes = 42L << 30 }.DiskSizeText);
        Check("маленький размер показывается в мегабайтах", new VmInfo { DiskSizeBytes = 512L << 20 }.DiskSizeText.Contains("МБ"),
            new VmInfo { DiskSizeBytes = 512L << 20 }.DiskSizeText);
    }

    private static void TestLogCleanup()
    {
        AppLog.Out("Срок хранения журнала");
        var dir = Path.Combine(Path.GetTempPath(), "hvlog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var old = Path.Combine(dir, "log-2020-01.txt");
            var fresh = Path.Combine(dir, "log-2026-11.txt");
            var other = Path.Combine(dir, "settings.json");
            File.WriteAllText(old, "old");
            File.WriteAllText(fresh, "fresh");
            File.WriteAllText(other, "not a log");
            File.SetLastWriteTime(old, DateTime.Now.AddDays(-100));

            var removed = AppLog.CleanupOldLogs(30, dir);
            Check("старый файл журнала удалён", !File.Exists(old), $"удалено {removed}");
            Check("свежий файл журнала сохранён", File.Exists(fresh));
            Check("чужие файлы не тронуты", File.Exists(other));

            var kept = AppLog.CleanupOldLogs(0, dir);
            Check("срок 0 — ничего не удаляется", kept == 0 && File.Exists(fresh));

            var removed2 = AppLog.CleanupOldLogs(30, dir);
            Check("повторная очистка ничего не удаляет", removed2 == 0, $"удалено {removed2}");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void TestBadgeUpdate()
    {
        AppLog.Out("Значки расписания в списке машин");
        using var list = new ListView { View = View.Details, CheckBoxes = true };
        list.Columns.Add("Машина", 100);
        list.Columns.Add("⚙", 30);
        list.Columns.Add("Расписание", 80);

        var settings = new AppSettings { Schedule = ScheduleKind.Daily, Hour = 3, Minute = 0 };

        MainForm.ApplyScheduleBadges(list, settings);
        Check("пустой список не вызывает ошибок", list.Items.Count == 0);

        var item = new ListViewItem("VM-1") { Checked = true, Tag = "VM-1" };
        list.Items.Add(item);
        MainForm.ApplyScheduleBadges(list, settings);
        Check("строка без колонки расписания не ломает список", item.SubItems.Count == 1);

        item.SubItems.Add("⚙");
        item.SubItems.Add("↺");
        item.SubItems.Add("");
        MainForm.ApplyScheduleBadges(list, settings);
        Check("у отмеченной машины появляется цикличность", item.SubItems[3].Text.Contains("03:00"),
            item.SubItems[3].Text);

        item.Checked = false;
        MainForm.ApplyScheduleBadges(list, settings);
        Check("у машины без галочки надпись очищается", item.SubItems[3].Text == "");

        settings.Schedule = ScheduleKind.Disabled;
        item.Checked = true;
        MainForm.ApplyScheduleBadges(list, settings);
        Check("при выключенном расписании значок снимается", item.SubItems[3].Text == "" && item.ImageIndex == -1);

        // У машины со своим графиком показывается именно он.
        settings.Schedule = ScheduleKind.Daily;
        settings.SetPerVm("VM-1", new VmSettings
        {
            UseOwnSchedule = true,
            Schedule = ScheduleKind.Weekly,
            Hour = 4,
            Minute = 30,
            WeeklyDay = 3
        });
        MainForm.ApplyScheduleBadges(list, settings);
        Check("у машины свой график — в бейдже он", item.SubItems[3].Text.Contains("04:30"),
            item.SubItems[3].Text);
    }

    private static void TestProgressGate()
    {
        AppLog.Out("Отбрасывание поздних обновлений прогресса");

        var gate = new ProgressGate();
        Check("до запуска обновления не принимаются", !gate.IsCurrent(1) && gate.ActiveId == -1);

        var run1 = gate.Begin();
        Check("обновление текущего запуска принимается", gate.IsCurrent(run1));
        Check("идентификатор запуска отражается в воротах", gate.ActiveId == run1);

        gate.End();
        Check("после завершения запуска поздняя фаза отбрасывается", !gate.IsCurrent(run1));
        Check("после завершения запуска активного запуска нет", gate.ActiveId == -1);

        var run2 = gate.Begin();
        Check("следующий запуск получает новый идентификатор", run2 != run1);
        Check("старое обновление не попадает в новый запуск", !gate.IsCurrent(run1));
        Check("новое обновление принимается", gate.IsCurrent(run2));
    }

    private static void TestHotExport()
    {
        AppLog.Out("Горячий экспорт Hyper-V");

        // 1. Сценарий экспорта: работающая ВМ выгружается без остановки, с согласованным состоянием.
        var script = PsScripts.ExportVm;
        Check("экспорт не требует остановки ВМ", script.Contains("CaptureLiveState CaptureDataConsistentState"));
        Check("экспорт учитывает состояние машины", script.Contains("__LIVE__") && script.Contains("$vm.State -eq 'Running'"));
        Check("экспорт выгружает через Export-VM", script.Contains("Export-VM -VM $vm -Path $dest"));

        // 2. Перенос папки экспорта с ограничением скорости: структура сохраняется.
        var dir = Path.Combine(Path.GetTempPath(), "hvselftest-" + Guid.NewGuid().ToString("N")[..8]);
        var src = Path.Combine(dir, "export");
        var dst = Path.Combine(dir, "copy");
        try
        {
            Directory.CreateDirectory(Path.Combine(src, "Virtual Hard Disks"));
            Directory.CreateDirectory(Path.Combine(src, "Virtual Machines", "{GUID-1}"));
            var vhdx = Path.Combine(src, "Virtual Hard Disks", "Win10.vhdx");
            var payload = new byte[8 * 1024 * 1024];
            Random.Shared.NextBytes(payload);
            File.WriteAllBytes(vhdx, payload);
            File.WriteAllText(Path.Combine(src, "Virtual Machines", "{GUID-1}", "config.bin"), "cfg");

            var size = BackupEngine.DirectorySize(src);
            Check("размер папки экспорта посчитан", size == payload.Length + 3, size + " байт");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var copied = ThrottledCopier.CopyDirectoryAsync(src, dst, 8L * 1024 * 1024, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            sw.Stop();

            Check("перенос скопировал все байты", copied == payload.Length + 3, copied + " байт");
            Check("структура экспорта сохранена",
                File.Exists(Path.Combine(dst, "Virtual Hard Disks", "Win10.vhdx")) &&
                File.Exists(Path.Combine(dst, "Virtual Machines", "{GUID-1}", "config.bin")));
            Check("содержимое диска не изменилось",
                new FileInfo(Path.Combine(dst, "Virtual Hard Disks", "Win10.vhdx")).Length == payload.Length);
            Check("ограничение скорости соблюдается", sw.Elapsed.TotalSeconds >= 0.6,
                sw.Elapsed.TotalSeconds.ToString("0.00") + " с на 8 МБ при лимите 8 МБ/с");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        // 3. Справочные файлы экспорта: параметры ВМ и команда восстановления.
        var dir2 = Path.Combine(Path.GetTempPath(), "hvselftest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir2);
            var settings = new AppSettings { Mode = CopyMode.HotExport, SpeedLimitMBps = 50 };
            var engine = new BackupEngine(settings, _ => { }, _ => { });
            var vm = new VmInfo
            {
                Name = "Win10-Test",
                Generation = 2,
                MemoryStartupBytes = 8L << 30,
                ProcessorCount = 4,
                SwitchName = "Default Switch",
                MacAddress = "00-15-5D-01-02-03",
                CheckpointType = "Production"
            };
            var res = new BackupResult { Consistency = "экспорт Hyper-V работающей ВМ" };
            engine.WriteExportMeta(vm, dir2, res, true);

            var info = Path.Combine(dir2, "vm-info.json");
            var readme = Path.Combine(dir2, "README.txt");
            Check("в экспорте есть vm-info.json", File.Exists(info));
            Check("в экспорте есть памятка", File.Exists(readme));
            if (File.Exists(info))
            {
                var text = File.ReadAllText(info);
                Check("в параметрах сохранён режим", text.Contains("HotExport"));
                Check("в параметрах сохранена команда Import-VM", text.Contains("Import-VM"));
            }
            if (File.Exists(readme))
            {
                var text = File.ReadAllText(readme);
                Check("памятка объясняет восстановление", text.Contains("Import-VM -Path"));
                Check("памятка предупреждает о конфликте имён", text.Contains("идентификатор"));
            }
        }
        finally
        {
            try { Directory.Delete(dir2, true); } catch { }
        }

        // 4. Настройки: новый режим и папка временного экспорта сохраняются.
        var s = new AppSettings { Mode = CopyMode.HotExport, ExportTempRoot = @"E:\Tmp" };
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Check("режим «горячий экспорт» сохраняется", back != null && back.Mode == CopyMode.HotExport);
        Check("папка временного экспорта сохраняется", back != null && back.ExportTempRoot == @"E:\Tmp");

        // 5. Переход режима для настроек, сохранённых до 1.8.
        var old = SettingsStore.ApplyModeMigration("{\"Mode\":\"CheckpointThrottled\",\"KeepCopies\":3}",
            new AppSettings { Mode = CopyMode.CheckpointThrottled });
        Check("старые настройки переходят на горячий экспорт", old.Mode == CopyMode.HotExport);

        var fresh = SettingsStore.ApplyModeMigration("{\"Mode\":\"CheckpointThrottled\",\"ExportTempRoot\":\"\"}",
            new AppSettings { Mode = CopyMode.CheckpointThrottled });
        Check("новые настройки не переключаются принудительно", fresh.Mode == CopyMode.CheckpointThrottled);

        // 6. Выбор машин: галочка — автобэкап, выделение строки — ручная копия.
        using var list = new ListView { View = View.Details, CheckBoxes = true, MultiSelect = true };
        list.Items.Add(new ListViewItem("Автобэкап") { Tag = "VM-A", Checked = true });
        list.Items.Add(new ListViewItem("Только вручную") { Tag = "VM-B", Checked = false });
        list.CreateControl();   // без созданного handle ListView не хранит выделение

        var checkedNames = MainForm.CheckedVmNames(list.CheckedItems.Cast<ListViewItem>());
        Check("галочка выбирает машины автобэкапа", checkedNames.Count == 1 && checkedNames[0] == "VM-A",
            string.Join(", ", checkedNames));

        list.Items[1].Selected = true;
        var selectedNames = MainForm.SelectedVmNames(list.SelectedItems.Cast<ListViewItem>());
        Check("выделение строки выбирает машину для ручной копии",
            selectedNames.Count == 1 && selectedNames[0] == "VM-B",
            string.Join(", ", selectedNames));
        Check("ручной выбор не зависит от галочек",
            !selectedNames.Contains("VM-A") && selectedNames.Contains("VM-B"));
    }

    private static void TestPerVmSettings()
    {
        AppLog.Out("Индивидуальные настройки виртуальных машин");

        var s = new AppSettings
        {
            SpeedLimitMBps = 100,
            KeepCopies = 3,
            BackupRoot = @"D:\Backups\Hyper-V",
            Schedule = ScheduleKind.Daily,
            Hour = 3,
            Minute = 0
        };

        var plain = s.ForVm("VM-A");
        Check("без индивидуальных настроек действуют общие",
            plain.SpeedLimitMBps == 100 && plain.KeepCopies == 3 && plain.BackupRoot == @"D:\Backups\Hyper-V");

        s.SetPerVm("VM-A", new VmSettings
        {
            SpeedLimitMBps = 25,
            KeepCopies = 7,
            BackupRoot = @"E:\VM\A",
            Mode = CopyMode.CheckpointThrottled,
            UseProductionCheckpoint = false,
            KeepLocalCopy = false,
            NetworkPath = @"\\nas\vma",
            FtpRemoteDir = "/vma",
            UseOwnSchedule = true,
            Schedule = ScheduleKind.Weekly,
            Hour = 4,
            Minute = 30,
            WeeklyDay = 3
        });

        var eff = s.ForVm("VM-A");
        Check("скорость перекрыта", eff.SpeedLimitMBps == 25);
        Check("число копий перекрыто", eff.KeepCopies == 7);
        Check("папка перекрыта", eff.BackupRoot == @"E:\VM\A");
        Check("режим перекрыт", eff.Mode == CopyMode.CheckpointThrottled);
        Check("Production-точка перекрыта", eff.UseProductionCheckpoint == false);
        Check("локальная копия перекрыта", eff.KeepLocalCopy == false);
        Check("сетевая папка перекрыта", eff.NetworkPath == @"\\nas\vma");
        Check("каталог FTP перекрыт", eff.FtpRemoteDir == "/vma");
        Check("свой график применён",
            eff.Schedule == ScheduleKind.Weekly && eff.Hour == 4 && eff.Minute == 30 && eff.WeeklyDay == 3);

        var other = s.ForVm("VM-B");
        Check("другие машины не затронуты",
            other.SpeedLimitMBps == 100 && other.KeepCopies == 3 && other.Schedule == ScheduleKind.Daily);

        s.SetPerVm("VM-C", new VmSettings());
        var c = s.ForVm("VM-C");
        Check("пустые значения не перекрывают общие",
            c.SpeedLimitMBps == 100 && c.BackupRoot == @"D:\Backups\Hyper-V" && c.Schedule == ScheduleKind.Daily);

        s.SetPerVm("VM-A", null);
        Check("индивидуальные настройки удаляются", s.PerVmSettings("VM-A") == null);

        // Настройки переживают сериализацию (в settings.json).
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Check("индивидуальные настройки сохраняются в файле",
            back != null && back.PerVm.ContainsKey("VM-C") && back.PerVmSettings("VM-C") != null);
    }

    private static void TestPerVmSchedule()
    {
        AppLog.Out("Расписание отдельных виртуальных машин");

        var s = new AppSettings
        {
            Schedule = ScheduleKind.Daily,
            Hour = 3,
            Minute = 0,
            SelectedVms = new List<string> { "VM-A", "VM-B" }
        };
        s.SetPerVm("VM-B", new VmSettings
        {
            UseOwnSchedule = true,
            Schedule = ScheduleKind.Weekly,
            Hour = 4,
            Minute = 30,
            WeeklyDay = (int)DayOfWeek.Wednesday
        });

        var scheduler = new BackupScheduler(s);
        var now = new DateTime(2026, 11, 25, 10, 0, 0);   // среда

        var a = scheduler.ComputeNextFor(now, "VM-A");
        Check("у машины без своих настроек — общий график",
            a != null && a.Value == new DateTime(2026, 11, 26, 3, 0, 0), a?.ToString() ?? "");

        var b = scheduler.ComputeNextFor(now, "VM-B");
        Check("у машины со своим графиком — свой график",
            b != null && b.Value == new DateTime(2026, 12, 2, 4, 30, 0), b?.ToString() ?? "");

        s.Schedule = ScheduleKind.Disabled;
        var b2 = scheduler.ComputeNextFor(now, "VM-B");
        Check("свой график работает при выключенном общем",
            b2 != null && b2.Value == new DateTime(2026, 12, 2, 4, 30, 0), b2?.ToString() ?? "");

        var a2 = scheduler.ComputeNextFor(now, "VM-A");
        Check("без своего графика машина выключена", a2 == null);
    }

    /// <summary>
    /// Проверка того, что копирование по расписанию действительно запускается.
    /// Раньше время следующего запуска пересчитывалось на каждом тике, а расчёт всегда
    /// возвращал будущий момент — условие срабатывания не выполнялось никогда.
    /// </summary>
    private static void TestSchedulerFires()
    {
        AppLog.Out("Запуск копирования по расписанию");

        var start = new DateTime(2026, 11, 27, 2, 0, 0);   // пятница

        var s = new AppSettings
        {
            Schedule = ScheduleKind.Daily,
            Hour = 3,
            Minute = 0,
            SelectedVms = new List<string> { "VM-A" }
        };
        s.SetPerVm("VM-B", new VmSettings
        {
            UseOwnSchedule = true,
            Schedule = ScheduleKind.Daily,
            Hour = 5,
            Minute = 0
        });
        s.SelectedVms.Add("VM-B");

        var scheduler = new BackupScheduler(s);
        scheduler.Recalculate(start);

        Check("время запуска посчитано",
            scheduler.NextRunFor("VM-A") == new DateTime(2026, 11, 27, 3, 0, 0),
            scheduler.NextRunFor("VM-A")?.ToString() ?? "—");
        Check("у машины со своим графиком своё время",
            scheduler.NextRunFor("VM-B") == new DateTime(2026, 11, 27, 5, 0, 0),
            scheduler.NextRunFor("VM-B")?.ToString() ?? "—");
        Check("ближайший запуск — самый ранний",
            scheduler.NextRun == new DateTime(2026, 11, 27, 3, 0, 0),
            scheduler.NextRun?.ToString() ?? "—");

        Check("до назначенного времени ничего не запускается",
            scheduler.CollectDue(new DateTime(2026, 11, 27, 2, 59, 59)).Count == 0);

        var due = scheduler.CollectDue(new DateTime(2026, 11, 27, 3, 0, 0));
        Check("в назначенное время запускается нужная машина",
            due.Count == 1 && due[0] == "VM-A", string.Join(", ", due));

        // После срабатывания время запуска пересчитывается — повторного запуска быть не должно.
        scheduler.Recalculate(new DateTime(2026, 11, 27, 3, 0, 1));
        Check("после запуска повторного срабатывания нет",
            scheduler.CollectDue(new DateTime(2026, 11, 27, 3, 0, 2)).Count == 0);
        Check("следующий ежедневный запуск — на следующий день",
            scheduler.NextRunFor("VM-A") == new DateTime(2026, 11, 28, 3, 0, 0),
            scheduler.NextRunFor("VM-A")?.ToString() ?? "—");

        var dueOwn = scheduler.CollectDue(new DateTime(2026, 11, 27, 5, 0, 0));
        Check("машина со своим графиком запускается в своё время",
            dueOwn.Count == 1 && dueOwn[0] == "VM-B", string.Join(", ", dueOwn));

        // Если машина пропустила своё время (программа была занята или компьютер спал),
        // запуск должен «догнать» её, а не ждать следующего дня.
        var late = new BackupScheduler(s);
        late.Recalculate(new DateTime(2026, 11, 27, 2, 0, 0));
        Check("пропущенное время догоняется",
            late.CollectDue(new DateTime(2026, 11, 27, 9, 0, 0)).Contains("VM-A"));

        // Интервал «каждые N минут».
        var s2 = new AppSettings
        {
            Schedule = ScheduleKind.EveryMinutes,
            EveryMinutes = 30,
            SelectedVms = new List<string> { "VM-C" }
        };
        var scheduler2 = new BackupScheduler(s2);
        scheduler2.Recalculate(new DateTime(2026, 11, 27, 10, 0, 0));
        Check("интервал: первый запуск через 30 минут",
            scheduler2.NextRunFor("VM-C") == new DateTime(2026, 11, 27, 10, 30, 0),
            scheduler2.NextRunFor("VM-C")?.ToString() ?? "—");
        Check("интервал: раньше времени не срабатывает",
            scheduler2.CollectDue(new DateTime(2026, 11, 27, 10, 29, 0)).Count == 0);
        Check("интервал: вовремя срабатывает",
            scheduler2.CollectDue(new DateTime(2026, 11, 27, 10, 30, 0)).Count == 1);

        // Выключенное расписание не запускается никогда.
        s.Schedule = ScheduleKind.Disabled;
        scheduler.Recalculate(new DateTime(2026, 11, 27, 2, 0, 0));
        Check("выключенное расписание не срабатывает",
            scheduler.CollectDue(new DateTime(2026, 11, 27, 3, 0, 0)).Count == 0);
    }

    /// <summary>
    /// Ход копирования должен быть виден в окне во время переноса, а не только
    /// после того, как скопирован очередной файл (раньше большой VHDX «молчал»).
    /// </summary>
    private static void TestCopyProgress()
    {
        AppLog.Out("Ход копирования (прогресс)");

        var dir = Path.Combine(Path.GetTempPath(), "hvprog-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);

        try
        {
            for (int i = 0; i < 3; i++)
                File.WriteAllBytes(Path.Combine(src, $"f{i}.bin"), new byte[4 * 1024 * 1024]);

            var reports = new List<CopyProgress>();
            var copied = ThrottledCopier.CopyDirectoryAsync(src, dst, 8L * 1024 * 1024,
                    new CollectingProgress(reports), CancellationToken.None)
                .GetAwaiter().GetResult();

            Check("скопированы все байты", copied == 12L * 1024 * 1024, copied + " байт");
            Check("прогресс сообщается во время копирования", reports.Count >= 3, $"{reports.Count} сообщений");
            Check("виден промежуточный объём",
                reports.Any(r => r.BytesDone > 0 && r.BytesDone < r.TotalBytes));
            Check("итоговый прогресс — 100 %",
                reports.Count > 0 && reports[^1].BytesDone == reports[^1].TotalBytes && reports[^1].TotalBytes == copied);
            Check("скорость в прогрессе не нулевая", reports.Any(r => r.BytesPerSecond > 0));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Во время горячего экспорта Hyper-V пишет диски в папку — по росту этой папки
    /// программа показывает, сколько уже выгружено и с какой скоростью.
    /// </summary>
    private static void TestExportProgress()
    {
        AppLog.Out("Ход горячего экспорта (прогресс)");

        var dir = Path.Combine(Path.GetTempPath(), "hvexport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var watcher = new FolderProgressWatcher(dir);

            var (empty, emptySpeed) = watcher.Sample();
            Check("пустая папка — нулевой объём", empty == 0, empty + " байт");
            Check("первый замер не даёт мгновенной скорости", emptySpeed == 0, emptySpeed + " байт/с");

            File.WriteAllBytes(Path.Combine(dir, "Win10.vhdx"), new byte[8 * 1024 * 1024]);
            Thread.Sleep(300);

            var (size, speed) = watcher.Sample();
            Check("объём выгруженного виден", size == 8L * 1024 * 1024, size + " байт");
            Check("скорость выгрузки считается", speed > 0, ThrottledCopier.FormatBytes(speed) + "/с");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>Проверка того, что именно видно в карточке состояния копии.</summary>
    private static void TestProgressText()
    {
        AppLog.Out("Показатели в карточке копии");

        var slow = new BackupProgress { Done = 0, Total = 0, Speed = 0 };
        Check("без измерений скорость не выдумывается", ProgressText.Speed(slow) == "—", ProgressText.Speed(slow));
        Check("без измерений время не выдумывается", ProgressText.Eta(slow) == "—", ProgressText.Eta(slow));

        const long mb = 1024 * 1024;
        var p = new BackupProgress { Done = 18 * mb, Total = 42 * mb, Speed = 1 * mb };
        Check("скорость показана в МБ/с", ProgressText.Speed(p) == "1 МБ/с", ProgressText.Speed(p));
        Check("оставшееся время посчитано", ProgressText.Eta(p) == "00:00:24", ProgressText.Eta(p));
        Check("объём показан с процентами",
            ProgressText.Volume(p) == "18 МБ из 42 МБ (43%)", ProgressText.Volume(p));

        var estimated = new BackupProgress { Done = 18 * mb, Total = 42 * mb, Speed = 1 * mb, Estimated = true };
        Check("оценочный объём помечен знаком ≈", ProgressText.Volume(estimated).Contains("≈"), ProgressText.Volume(estimated));
        Check("оценочное время помечено знаком ≈", ProgressText.Eta(estimated).StartsWith("≈"), ProgressText.Eta(estimated));

        var over = new BackupProgress { Done = 50 * mb, Total = 42 * mb, Speed = 1 * mb };
        Check("процент не больше 100", ProgressText.Volume(over).Contains("(100%)"), ProgressText.Volume(over));
    }

    /// <summary>Синхронный сборщик сообщений о прогрессе (для проверок).</summary>
    private sealed class CollectingProgress : IProgress<CopyProgress>
    {
        private readonly List<CopyProgress> _items;
        public CollectingProgress(List<CopyProgress> items) => _items = items;
        public void Report(CopyProgress value) { lock (_items) _items.Add(value); }
    }

    /// <summary>Поиск копий, пригодных для восстановления.</summary>
    private static void TestRestoreScan()
    {
        AppLog.Out("Поиск копий для восстановления");

        var root = Path.Combine(Path.GetTempPath(), "hvrestore-" + Guid.NewGuid().ToString("N")[..8]);

        // Экспорт Hyper-V: конфигурация и диски.
        var export = Path.Combine(root, "Win10-Test", "2026-11-27_22-14-03");
        Directory.CreateDirectory(Path.Combine(export, "Virtual Machines"));
        Directory.CreateDirectory(Path.Combine(export, "Virtual Hard Disks"));
        File.WriteAllText(Path.Combine(export, "Virtual Machines", "1234-5678.vmcx"), "config");
        File.WriteAllBytes(Path.Combine(export, "Virtual Hard Disks", "Win10.vhdx"), new byte[4096]);
        File.WriteAllText(Path.Combine(export, "vm-info.json"),
            "{\"vmName\":\"Win10-Test\",\"consistency\":\"экспорт Hyper-V работающей ВМ\"}");

        // Копия режима «контрольная точка» — её машины уже нет в Hyper-V.
        var cp = Path.Combine(root, "Ubuntu-Dev", "2026-11-26_03-00-00");
        Directory.CreateDirectory(Path.Combine(cp, "Disks"));
        File.WriteAllBytes(Path.Combine(cp, "Disks", "Ubuntu.avhdx"), new byte[2048]);
        File.WriteAllText(Path.Combine(cp, "restore.ps1"), "param([string]$Destination)");
        File.WriteAllText(Path.Combine(cp, "vm-info.json"), "{\"vmName\":\"Ubuntu-Dev\"}");

        // Посторонняя папка — копией не считается.
        Directory.CreateDirectory(Path.Combine(root, "Не-копия", "что-то"));

        try
        {
            var settings = new AppSettings { BackupRoot = root };
            var copies = RestoreEngine.Scan(settings);

            Check("найдены обе копии", copies.Count == 2, copies.Count.ToString());

            var exp = copies.FirstOrDefault(c => c.VmName == "Win10-Test");
            Check("экспорт найден", exp != null);
            Check("тип экспорта определён", exp != null && exp.IsExport, exp?.KindText ?? "");
            Check("дата копии прочитана",
                exp != null && exp.Created == new DateTime(2026, 11, 27, 22, 14, 3), exp?.CreatedText ?? "");
            Check("объём копии посчитан", exp != null && exp.SizeBytes > 0, exp?.SizeText ?? "");
            Check("консистентность прочитана из vm-info.json",
                exp != null && exp.Consistency.Contains("экспорт Hyper-V"), exp?.Consistency ?? "");

            var point = copies.FirstOrDefault(c => c.VmName == "Ubuntu-Dev");
            Check("копия контрольной точки найдена", point != null);
            Check("тип контрольной точки определён",
                point != null && !point.IsExport && point.Kind == "checkpoint", point?.KindText ?? "");
            Check("копия удалённой машины видна", point != null);

            Check("посторонние папки проигнорированы", copies.All(c => c.VmName != "Не-копия"));
            Check("папка не по шаблону даты — не копия",
                RestoreEngine.Describe(Path.Combine(root, "Не-копия", "что-то")) == null);

            // Индивидуальная папка копий другой машины тоже просматривается.
            var other = Path.Combine(root, "other-root", "Server", "2026-11-25_03-00-00");
            Directory.CreateDirectory(Path.Combine(other, "Virtual Hard Disks"));
            File.WriteAllBytes(Path.Combine(other, "Virtual Hard Disks", "Server.vhdx"), new byte[512]);
            settings.SetPerVm("Server", new VmSettings { BackupRoot = Path.Combine(root, "other-root") });

            var copies2 = RestoreEngine.Scan(settings);
            Check("индивидуальная папка копий учтена", copies2.Any(c => c.VmName == "Server"), copies2.Count.ToString());
            Check("копии отсортированы — свежие сверху",
                copies2.Count == 0 || copies2[0].Created >= copies2[^1].Created);

            // Экспорт, вложенный в подпапку с именем машины (так делает сам Hyper-V при Export-VM).
            var nestedCopy = Path.Combine(root, "Nested-VM", "2026-11-27_10-00-00");
            var nested = Path.Combine(nestedCopy, "Nested-VM");
            Directory.CreateDirectory(Path.Combine(nested, "Virtual Machines"));
            Directory.CreateDirectory(Path.Combine(nested, "Virtual Hard Disks"));
            File.WriteAllText(Path.Combine(nested, "Virtual Machines", "abcd-5678.vmcx"), "config");
            File.WriteAllBytes(Path.Combine(nested, "Virtual Hard Disks", "Nested.vhdx"), new byte[1024]);

            var nestedInfo = RestoreEngine.Describe(nestedCopy);
            Check("вложенный экспорт найден", nestedInfo != null);
            Check("вложенный экспорт определён как экспорт", nestedInfo != null && nestedInfo.IsExport);
            Check("путь экспорта указывает на вложенную папку",
                nestedInfo != null && nestedInfo.ExportRoot == nested, nestedInfo?.ExportRoot ?? "");
            Check("вложенный экспорт попал в общий список",
                RestoreEngine.Scan(settings).Any(c => c.VmName == "Nested-VM"));

            // Фильтр по выбранным машинам: пустой выбор — показываем все копии.
            var all = RestoreEngine.Scan(settings);
            Check("без выбора показываются все копии",
                RestoreForm.FilterCopies(all, new string[0]).Count == all.Count);
            Check("с выбором показываются копии этой машины",
                RestoreForm.FilterCopies(all, new[] { "Win10-Test" }).All(c => c.VmName == "Win10-Test")
                && RestoreForm.FilterCopies(all, new[] { "Win10-Test" }).Count == 1);
            Check("выбор машины без копий даёт пустой список",
                RestoreForm.FilterCopies(all, new[] { "Нет-Такой-ВМ" }).Count == 0);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>Сценарии восстановления: синтаксис и нужные команды Hyper-V.</summary>
    private static void TestRestoreScripts()
    {
        AppLog.Out("Сценарии восстановления Hyper-V");

        var (okImport, errImport) = Ps.CheckSyntax(PsScripts.ImportVm);
        Check("сценарий импорта синтаксически корректен", okImport, errImport);
        Check("импорт копирует файлы и берёт новый идентификатор",
            PsScripts.ImportVm.Contains("Copy = $true") && PsScripts.ImportVm.Contains("GenerateNewId"));
        Check("импорт ищет файл конфигурации .vmcx", PsScripts.ImportVm.Contains(".vmcx"));
        Check("импорт задаёт целевые папки для дисков и конфигурации",
            PsScripts.ImportVm.Contains("VhdDestinationPath") && PsScripts.ImportVm.Contains("VirtualMachinePath"));
        Check("импорт переименовывает машину при необходимости", PsScripts.ImportVm.Contains("Rename-VM"));

        var (okRun, errRun) = Ps.CheckSyntax(PsScripts.RunRestoreScript);
        Check("сценарий запуска restore.ps1 синтаксически корректен", okRun, errRun);
        Check("проверяется, занято ли имя машины", PsScripts.RunRestoreScript.Contains("уже существует"));
        Check("созданная машина находится среди ВМ", PsScripts.RunRestoreScript.Contains("$before -notcontains"));
        Check("созданная машина переименовывается", PsScripts.RunRestoreScript.Contains("Rename-VM"));
        Check("учитывается ключ -VmName", PsScripts.RunRestoreScript.Contains("VmName"));

        var (okPaths, errPaths) = Ps.CheckSyntax(PsScripts.GetHostPaths);
        Check("сценарий получения путей хоста синтаксически корректен", okPaths, errPaths);
        Check("запрашиваются пути машин и дисков",
            PsScripts.GetHostPaths.Contains("VirtualMachinePath") && PsScripts.GetHostPaths.Contains("VirtualHardDiskPath"));
    }

    /// <summary>Готовый restore.ps1 должен принимать имя восстанавливаемой машины.</summary>
    private static void TestRestoreScriptNameParameter()
    {
        AppLog.Out("Скрипт восстановления: имя машины");

        var script = RestoreScript.Build("Тест-ВМ", 2, 4L << 30, 4, "Default Switch", "00-11-22-33-44-55",
            new List<RestoreScript.DiskSpec> { new() { File = "Тест-ВМ.vhdx", IsTop = true } },
            "Тест-ВМ.vhdx", "2026-11-27 22:14:03", "контрольная точка");

        Check("скрипт принимает ключ -VmName", script.Contains("[string]$VmName = ''"));
        Check("имя из ключа подставляется", script.Contains("if ($VmName) { $vmName = $VmName }"));

        var (ok, err) = Ps.CheckSyntax(script);
        Check("скрипт с ключом -VmName синтаксически корректен", ok, err);
    }

    /// <summary>Вспомогательные проверки восстановления.</summary>
    private static void TestRestoreHelpers()
    {
        AppLog.Out("Вспомогательные проверки восстановления");

        var dir = Path.Combine(Path.GetTempPath(), "hvr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            Check("пустая папка подходит для восстановления", RestoreEngine.UniqueFolder(dir) == dir);

            File.WriteAllText(Path.Combine(dir, "файл.vhdx"), "данные");
            var second = RestoreEngine.UniqueFolder(dir);
            Check("занятая папка заменяется свободной", second != dir && !Directory.Exists(second), second);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Check("занятое имя переводится на понятный язык",
            RestoreEngine.FriendlyError("The virtual machine name 'Win10' already exists.").Contains("уже существует"));
        Check("отказ в правах переводится на понятный язык",
            RestoreEngine.FriendlyError("Access is denied.").Contains("администратора"));
        Check("занятый идентификатор переводится на понятный язык",
            RestoreEngine.FriendlyError("The operation failed because a virtual machine with the same identifier already exists.")
                .Contains("идентификатор"));
        Check("неизвестная ошибка не искажается",
            RestoreEngine.FriendlyError("Что-то пошло не так") == "Что-то пошло не так");

        var options = new RestoreOptions();
        Check("по умолчанию машина создаётся с новым идентификатором", options.GenerateNewId);
        Check("по умолчанию машина не запускается", !options.StartAfter);
    }

    /// <summary>Одна целевая папка и счётчик копий у машины.</summary>
    private static void TestRestoreDestination()
    {
        AppLog.Out("Целевая папка восстановления и счётчик копий");

        var (vhd, vm) = RestoreEngine.SplitDestination(@"D:\Restore\Win10", isExport: true);
        Check("экспорт: диски — в подпапке «Virtual Hard Disks»",
            vhd == @"D:\Restore\Win10\Virtual Hard Disks", vhd);
        Check("экспорт: конфигурация — в подпапке «Virtual Machines»",
            vm == @"D:\Restore\Win10\Virtual Machines", vm);

        var (vhdCp, vmCp) = RestoreEngine.SplitDestination(@"D:\Restore\Win10", isExport: false);
        Check("контрольная точка: диски и машина — в одной папке",
            vhdCp == "" && vmCp == @"D:\Restore\Win10", $"{vhdCp} | {vmCp}");

        var (vhdEmpty, vmEmpty) = RestoreEngine.SplitDestination("", isExport: true);
        Check("пустая папка — пустые пути", vhdEmpty == "" && vmEmpty == "", $"{vhdEmpty} | {vmEmpty}");

        // Счётчик копий у машины — те же папки, что и в окне восстановления.
        var root = Path.Combine(Path.GetTempPath(), "hvcount-" + Guid.NewGuid().ToString("N")[..8]);
        var vmFolder = Path.Combine(root, "Счётчик-ВМ");
        Directory.CreateDirectory(Path.Combine(vmFolder, "2026-11-27_10-00-00"));
        Directory.CreateDirectory(Path.Combine(vmFolder, "2026-11-26_10-00-00"));
        Directory.CreateDirectory(Path.Combine(vmFolder, "не-копия"));

        try
        {
            var settings = new AppSettings { BackupRoot = root };
            var (copies, last) = MainForm.BackupStats(settings, "Счётчик-ВМ");
            Check("копии посчитаны по папкам копий", copies == 2, copies.ToString());
            Check("самая свежая копия определена", last == "27.11.2026 10:00", last);

            var (none, dash) = MainForm.BackupStats(settings, "Нет-Такой-ВМ");
            Check("у машины без копий — ноль", none == 0 && dash == "—", $"{none} / {dash}");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>Удаление копий.</summary>
    private static void TestDeleteCopy()
    {
        AppLog.Out("Удаление копий");

        var root = Path.Combine(Path.GetTempPath(), "hvdel-" + Guid.NewGuid().ToString("N")[..8]);
        var vmFolder = Path.Combine(root, "Удаляемая-ВМ");
        var copy = Path.Combine(vmFolder, "2026-11-27_10-00-00");
        Directory.CreateDirectory(Path.Combine(copy, "Virtual Hard Disks"));
        File.WriteAllBytes(Path.Combine(copy, "Virtual Hard Disks", "disk.vhdx"), new byte[1024]);

        try
        {
            var settings = new AppSettings { BackupRoot = root };
            Check("копия видна до удаления",
                RestoreEngine.Scan(settings).Any(c => c.VmName == "Удаляемая-ВМ"));

            Check("копия удалена", RestoreEngine.DeleteCopy(copy));
            Check("папки копии больше нет", !Directory.Exists(copy));
            Check("в списке копий её больше нет",
                !RestoreEngine.Scan(settings).Any(c => c.VmName == "Удаляемая-ВМ"));
            Check("удаление уже удалённой копии не считается ошибкой",
                RestoreEngine.DeleteCopy(copy));

            // Папка, которую удаляют, не должна считаться копией.
            var trash = Path.Combine(vmFolder, "удаляется-20261127-101010-2026-11");
            Directory.CreateDirectory(Path.Combine(trash, "Virtual Hard Disks"));
            Check("папка «в удалении» не считается копией",
                !RestoreEngine.Scan(settings).Any(c => c.Folder == trash));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void TestSettingsReload()
    {
        AppLog.Out("Подхват настроек, изменённых извне");

        var first = new AppSettings
        {
            BackupRoot = @"D:\Копии",
            KeepCopies = 5,
            SpeedLimitMBps = 40,
            Schedule = ScheduleKind.Daily,
            Hour = 2,
            Minute = 30,
            SelectedVms = { "VM-1" }
        };
        first.SetPerVm("VM-1", new VmSettings
        {
            UseOwnSchedule = true,
            Schedule = ScheduleKind.Weekly,
            Hour = 4,
            WeeklyDay = 3
        });

        var second = new AppSettings
        {
            BackupRoot = @"E:\Другие копии",
            KeepCopies = 9,
            SpeedLimitMBps = 0,
            Schedule = ScheduleKind.EveryMinutes,
            EveryMinutes = 30,
            SelectedVms = { "VM-2", "VM-3" }
        };
        second.SetPerVm("VM-2", new VmSettings { SpeedLimitMBps = 15 });

        first.CopyFrom(second);

        Check("папка копий обновлена", first.BackupRoot == @"E:\Другие копии");
        Check("число копий обновлено", first.KeepCopies == 9);
        Check("лимит скорости обновлён", first.SpeedLimitMBps == 0);
        Check("расписание обновлено",
            first.Schedule == ScheduleKind.EveryMinutes && first.EveryMinutes == 30);
        Check("список машин обновлён",
            first.SelectedVms.Count == 2 && first.SelectedVms.Contains("VM-2"));
        Check("индивидуальные настройки заменены",
            first.PerVmSettings("VM-1") == null && first.PerVmSettings("VM-2")?.SpeedLimitMBps == 15);
        Check("индивидуальные настройки не делятся с исходным объектом",
            second.PerVmSettings("VM-2") != first.PerVmSettings("VM-2"));
        Check("эффективные настройки учитывают индивидуальные",
            first.ForVm("VM-2").SpeedLimitMBps == 15);
        Check("общие настройки не задеты индивидуальными",
            first.ForVm("VM-3").SpeedLimitMBps == 0);
    }

    private static void TestHyperVAccess()
    {
        AppLog.Out("Доступ к Hyper-V (справочно)");
        var (ok, err) = HyperV.CheckAccess();
        AppLog.Out(ok ? "  Hyper-V доступен." : "  Hyper-V недоступен: " + err);
    }

    /// <summary>Проверки встроенного MCP-сервера: реестр инструментов, протокол, HTTP-эндпоинты.</summary>
    private static void TestMcp()
    {
        AppLog.Out("Встроенный MCP-сервер");
        McpSelfTest.Run(Check);
    }

    private static void TestDpapi()
    {
        AppLog.Out("Шифрование паролей (DPAPI)");
        var encoded = Dpapi.Protect("секрет-пароль-123");
        Check("пароль зашифрован", encoded.Length > 0 && !encoded.Contains("секрет"));
        Check("пароль расшифрован без потерь", Dpapi.Unprotect(encoded) == "секрет-пароль-123");
        Check("пустой пароль безопасен", Dpapi.Protect("") == "" && Dpapi.Unprotect("") == "");
        Check("испорченный шифр не ломает программу", Dpapi.Unprotect("не-base64!!") == "");
    }

    private static void TestFtpParsing()
    {
        AppLog.Out("Разбор ответов FTP");
        var (ip, port) = FtpClient.ParsePassive("227 Entering Passive Mode (192,168,1,10,195,80)");
        Check("PASV разобран", ip.ToString() == "192.168.1.10" && port == 195 * 256 + 80, $"{ip}:{port}");

        var epsv = FtpClient.ParseExtendedPassive("229 Entering Extended Passive Mode (|||54321|)");
        Check("EPSV разобран", epsv == 54321, epsv.ToString());

        var mlsd = FtpClient.ParseListing(
            "type=dir;size=0;modify=20260101000000; 2026-01-01_00-00-00\n" +
            "type=file;size=100;modify=20260101000000; disk.vhdx");
        Check("MLSD разобран", mlsd.Count == 2 && mlsd[0].IsDirectory && mlsd[1].Name == "disk.vhdx" && mlsd[1].Size == 100);

        var list = FtpClient.ParseListing(
            "-rw-r--r-- 1 owner group 2048 Jan  1 00:00 disk.vhdx\n" +
            "drwxr-xr-x 2 owner group 4096 Jan  1 00:00 sub");
        Check("LIST разобран", list.Count == 2 && list[1].IsDirectory && list[0].Size == 2048);
    }

    private static void TestFtpLoopback()
    {
        AppLog.Out("FTP-клиент на локальном тестовом сервере");
        var dir = Path.Combine(Path.GetTempPath(), "hvftp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        MiniFtpServer? server = null;
        try
        {
            server = new MiniFtpServer(dir);
            using var ftp = new FtpClient("127.0.0.1", server.Port, "user", "pass", false, false) { Passive = true };
            ftp.ConnectAsync().GetAwaiter().GetResult();
            Check("подключение к FTP-серверу", true, $"порт {server.Port}");

            ftp.EnsureDirectoryAsync("/backup/VM").GetAwaiter().GetResult();
            Check("удалённый каталог создан", Directory.Exists(Path.Combine(dir, "backup", "VM")));

            var src = Path.Combine(dir, "src.vhdx");
            const string payload = "содержимое тестового виртуального диска";
            File.WriteAllText(src, payload);

            ftp.UploadFileAsync(src, "/backup/VM/2026-01-01_00-00-00/Disks/src.vhdx", 0, null, CancellationToken.None)
                .GetAwaiter().GetResult();

            var uploaded = Path.Combine(dir, "backup", "VM", "2026-01-01_00-00-00", "Disks", "src.vhdx");
            Check("файл загружен на сервер", File.Exists(uploaded));
            Check("содержимое файла не изменилось",
                File.Exists(uploaded) && File.ReadAllText(uploaded) == payload);

            var size = ftp.GetSizeAsync("/backup/VM/2026-01-01_00-00-00/Disks/src.vhdx").GetAwaiter().GetResult();
            Check("SIZE возвращает размер", size == new FileInfo(src).Length, size.ToString());

            var entries = ftp.ListAsync("/backup/VM").GetAwaiter().GetResult();
            Check("листинг содержит папку копии",
                entries.Any(e => e.Name == "2026-01-01_00-00-00" && e.IsDirectory),
                string.Join(", ", entries.Select(e => e.Name)));

            ftp.DeleteDirectoryAsync("/backup/VM/2026-01-01_00-00-00").GetAwaiter().GetResult();
            Check("удалённая копия удалена",
                !Directory.Exists(Path.Combine(dir, "backup", "VM", "2026-01-01_00-00-00")));
        }
        catch (Exception ex)
        {
            Check("тест FTP-клиента", false, ex.Message);
        }
        finally
        {
            server?.DisposeAsync().AsTask().Wait(5000);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void TestNetworkPaths()
    {
        AppLog.Out("Сетевые пути");
        Check("корень UNC выделен", NetworkShare.UncRoot(@"\\nas\backups\Hyper-V") == @"\\nas\backups");
        Check("некорректный путь отклонён", NetworkShare.UncRoot(@"C:\temp") == "");
        Check("ошибка входа описана понятно", NetworkShare.DescribeError(1326, @"\\nas\x").Contains("пароль"));
        Check("шаблон имени копии распознаётся", BackupEngine.IsCopyFolderName("2026-01-01_00-00-00"));
        Check("посторонняя папка не считается копией", !BackupEngine.IsCopyFolderName("Диски"));
    }

    // ── Моки командлетов Hyper-V: проверяем логику сценариев без прав администратора ──
    private const string MockCmdlets = """
function Get-VM {
  param($Name, $VM, $ErrorAction)
  @([PSCustomObject]@{
    Name='MockVM'; Id=[guid]'11111111-1111-1111-1111-111111111111'; State='Running'; Generation=2
    MemoryStartup=8589934592; ProcessorCount=4; CheckpointType='Standard'
    ConfigurationLocation='C:\ProgramData\Microsoft\Hyper-V\Virtual Machine Configuration'
    SnapshotFileLocation='C:\ProgramData\Microsoft\Hyper-V\Virtual Hard Disks'
  })
}
function Get-VMHardDiskDrive {
  param($VM, $VMName, $ErrorAction)
  @([PSCustomObject]@{ Path='C:\VM\MockVM.vhdx'; ControllerType='SCSI'; ControllerNumber=0; ControllerLocation=0 })
}
function Get-VMNetworkAdapter {
  param($VM, $VMName, $ErrorAction)
  @([PSCustomObject]@{ SwitchName='Default Switch'; MacAddress='00-15-5D-01-02-03' })
}
function Get-VHD {
  param($Path, $ErrorAction)
  if ($Path -eq 'C:\VM\MockVM.vhdx') {
    return [PSCustomObject]@{ Path=$Path; Size=107374182400; ParentPath='C:\VM\Base.vhdx'; ChildPath='C:\VM\MockVM_1.avhdx' }
  }
  if ($Path -eq 'C:\VM\Base.vhdx') {
    return [PSCustomObject]@{ Path=$Path; Size=107374182400; ParentPath=$null; ChildPath='C:\VM\MockVM.vhdx' }
  }
  return $null
}
function Checkpoint-VM {
  param($VM, $Name, $SnapshotName, $Passthru, $ErrorAction)
  $script:MockCheckpointCreated = $SnapshotName
  return @([PSCustomObject]@{ Id=[guid]'22222222-2222-2222-2222-222222222222'; Name=$SnapshotName; SnapshotType='Standard' })
}
function Get-VMSnapshot {
  param($VM, $VMName, $Name, $Id, $ErrorAction)
  @([PSCustomObject]@{ Id=[guid]'22222222-2222-2222-2222-222222222222'; Name=$Name; SnapshotType='Standard' })
}
function Remove-VMSnapshot {
  [CmdletBinding(SupportsShouldProcess=$true)]
  param($VM, $VMName, $Name, $VMSnapshot, $IncludeAllChildSnapshots, $AsJob, $Passthru)
  $script:MockSnapshotRemoved = $Name
  return $true
}
""";

    private static void TestMockedHyperV()
    {
        AppLog.Out("Сценарии Hyper-V на подставных командлетах");

        var (ok1, vms, err1) = Ps.RunJson<List<VmInfo>>(MockCmdlets + "\n" + PsScripts.ListVms, 60);
        Check("список ВМ: JSON разбирается", ok1 && vms != null && vms.Count == 1, err1);
        if (vms != null && vms.Count == 1)
        {
            var vm = vms[0];
            Check("имя и состояние ВМ", vm.Name == "MockVM" && vm.State == "Running");
            Check("память в байтах", vm.MemoryStartupBytes == 8589934592, vm.MemoryStartupBytes.ToString());
            Check("число процессоров", vm.ProcessorCount == 4);
            Check("список дисков", vm.Disks.Count == 1 && vm.Disks[0] == @"C:\VM\MockVM.vhdx");
            Check("коммутатор и MAC", vm.SwitchName == "Default Switch" && vm.MacAddress == "00-15-5D-01-02-03");
            Check("тип контрольной точки", vm.CheckpointType == "Standard");
        }

        var chainBody = PsScripts.GetDiskChain.Replace("__VMNAME__", "MockVM");
        var (ok2, disks, err2) = Ps.RunJson<List<DiskInfo>>(MockCmdlets + "\n" + chainBody, 60);
        Check("цепочка дисков: JSON разбирается", ok2 && disks != null && disks.Count == 1, err2);
        if (disks != null && disks.Count == 1)
        {
            var chain = disks[0].Chain;
            Check("в цепочке два элемента", chain.Count == 2, chain.Count.ToString());
            Check("первый — активный диск", chain.Count > 0 && chain[0].Path == @"C:\VM\MockVM.vhdx" && chain[0].IsActive);
            Check("второй — родитель", chain.Count > 1 && chain[1].Path == @"C:\VM\Base.vhdx" && chain[1].ParentPath == "");
            Check("размер диска", chain[0].Size == 107374182400);
        }

        var cpBody = PsScripts.CreateCheckpoint.Replace("__VMNAME__", "MockVM").Replace("__SNAPNAME__", "HvBackup test");
        var (ok3, cp, err3) = Ps.RunJson<CheckpointInfo>(MockCmdlets + "\n" + cpBody, 60);
        Check("контрольная точка: JSON разбирается", ok3 && cp != null && cp.Type == "Standard", err3);

        var rmBody = PsScripts.RemoveCheckpoint.Replace("__VMNAME__", "MockVM").Replace("__SNAPNAME__", "HvBackup test");
        var (ok4, _, err4) = Ps.RunJson<bool>(MockCmdlets + "\n" + rmBody, 60);
        Check("удаление контрольной точки выполняется", ok4, err4);
    }

    private static void TestPlanBuilder()
    {
        AppLog.Out("План копирования дисков");
        var disks = new List<DiskInfo>
        {
            new()
            {
                ControllerType = "SCSI", ControllerNumber = 0, ControllerLocation = 0,
                ActivePath = @"C:\VM\MockVM.vhdx",
                Chain = new List<ChainItem>
                {
                    new() { Path = @"C:\VM\MockVM.vhdx", Size = 100, IsActive = true, ParentPath = @"C:\VM\Base.vhdx" },
                    new() { Path = @"C:\VM\Base.vhdx", Size = 100, ParentPath = "" }
                }
            }
        };

        var running = BackupEngine.BuildPlan(disks, running: true);
        Check("у работающей ВМ активный диск не копируется", running.Count == 1 && running[0].Source == @"C:\VM\Base.vhdx");
        Check("верхний диск помечен", running.Count == 1 && running[0].IsTop);

        var stopped = BackupEngine.BuildPlan(disks, running: false);
        Check("у выключенной ВМ копируется вся цепочка", stopped.Count == 2);
        Check("верхним становится активный диск", stopped.Count == 2 && stopped[0].IsTop && stopped[0].Source == @"C:\VM\MockVM.vhdx");
    }

    private static string Sha(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}
