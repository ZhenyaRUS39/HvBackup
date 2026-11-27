using System.Globalization;
using System.Text.Json;

namespace HvBackup;

/// <summary>Найденная копия виртуальной машины, пригодная для восстановления.</summary>
public sealed class BackupCopyInfo
{
    /// <summary>Имя исходной виртуальной машины.</summary>
    public string VmName { get; set; } = "";

    /// <summary>Папка копии.</summary>
    public string Folder { get; set; } = "";

    /// <summary>Момент создания копии.</summary>
    public DateTime Created { get; set; }

    /// <summary>Объём копии в байтах.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Тип копии: export (экспорт Hyper-V) или checkpoint (контрольная точка).</summary>
    public string Kind { get; set; } = "";

    /// <summary>Консистентность копии (из vm-info.json).</summary>
    public string Consistency { get; set; } = "";

    /// <summary>
    /// Папка, в которой лежит сам экспорт Hyper-V. Обычно это сама папка копии,
    /// но Hyper-V при экспорте кладёт «Virtual Machines» и «Virtual Hard Disks»
    /// в подпапку с именем машины — тогда указывается она.
    /// </summary>
    public string ExportRoot { get; set; } = "";

    public bool IsExport => Kind == "export";

    public string KindText => IsExport ? "экспорт Hyper-V" : "контрольная точка";

    public string CreatedText => Created == default ? "—" : Created.ToString("dd.MM.yyyy HH:mm");

    public string SizeText => ThrottledCopier.FormatBytes(SizeBytes);
}

/// <summary>Параметры восстановления, заданные в диалоге.</summary>
public sealed class RestoreOptions
{
    /// <summary>Имя восстанавливаемой машины.</summary>
    public string VmName { get; set; } = "";

    /// <summary>Папка, куда Hyper-V скопирует виртуальные диски (пусто — путь хоста по умолчанию).</summary>
    public string VhdPath { get; set; } = "";

    /// <summary>Папка для файлов конфигурации машины (пусто — путь хоста по умолчанию).</summary>
    public string VmPath { get; set; } = "";

    /// <summary>Создать машину с новым идентификатором (тогда исходная ВМ может остаться).</summary>
    public bool GenerateNewId { get; set; } = true;

    /// <summary>Запустить машину после восстановления.</summary>
    public bool StartAfter { get; set; }
}

/// <summary>Результат восстановления.</summary>
public sealed class RestoreResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string VmName { get; set; } = "";
    public string VmId { get; set; } = "";
}

/// <summary>
/// Поиск копий и восстановление виртуальных машин из них.
/// </summary>
public static class RestoreEngine
{
    /// <summary>
    /// Ищет копии во всех папках копий: общей и индивидуальных для отдельных ВМ.
    /// Находятся и копии машин, которых больше нет в Hyper-V, — их как раз и нужно восстанавливать.
    /// </summary>
    public static List<BackupCopyInfo> Scan(AppSettings settings)
    {
        var roots = new List<string>();

        void AddRoot(string? root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            var trimmed = root.Trim();
            if (!roots.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) roots.Add(trimmed);
        }

        AddRoot(settings.BackupRoot);
        foreach (var v in settings.PerVm.Values) AddRoot(v.BackupRoot);

        var result = new List<BackupCopyInfo>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            foreach (var vmDir in SafeGetDirectories(root))
            {
                foreach (var copyDir in SafeGetDirectories(vmDir))
                {
                    var info = Describe(copyDir);
                    if (info != null) result.Add(info);
                }
            }
        }

        return result
            .OrderByDescending(x => x.Created)
            .ThenBy(x => x.VmName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SafeGetDirectories(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Разносит одну целевую папку по назначению: для экспорта Hyper-V — диски
    /// и конфигурация в своих подпапках, для копии-контрольной точки — сама папка
    /// (готовый скрипт сам создаёт внутри подпапку Disks).
    /// </summary>
    public static (string VhdPath, string VmPath) SplitDestination(string dest, bool isExport)
    {
        var folder = (dest ?? "").Trim();
        if (folder.Length == 0) return ("", "");
        if (!isExport) return ("", folder);

        return (Path.Combine(folder, "Virtual Hard Disks"), Path.Combine(folder, "Virtual Machines"));
    }

    /// <summary>
    /// Удаляет папку копии. Сначала переименовывает её — чтобы папка перестала считаться
    /// копией и её нельзя было случайно выбрать, — затем удаляет содержимое.
    /// </summary>
    public static bool DeleteCopy(string folder, Action<string>? log = null)
    {
        try
        {
            if (!Directory.Exists(folder)) return true;

            var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar);
            var parent = Path.GetDirectoryName(trimmed) ?? "";
            var name = Path.GetFileName(trimmed);
            // Имя не должно начинаться с даты — иначе папка продолжит считаться копией.
            var trash = Path.Combine(parent,
                "удаляется-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + name.Substring(0, Math.Min(8, name.Length)));

            Directory.Move(trimmed, trash);
            try
            {
                Directory.Delete(trash, true);
            }
            catch (Exception ex)
            {
                var message = "Не удалось удалить содержимое " + trash + ": " + ex.Message +
                              ". Папку можно удалить вручную.";
                log?.Invoke(message);
                AppLog.Warn(message);
                return false;
            }

            log?.Invoke("Копия удалена: " + trimmed);
            return true;
        }
        catch (Exception ex)
        {
            var message = "Не удалось удалить копию " + folder + ": " + ex.Message;
            log?.Invoke(message);
            AppLog.Warn(message);
            return false;
        }
    }

    /// <summary>
    /// Возвращает папку, пригодную для восстановления: если указанная папка уже
    /// не пуста, подбирается свободная рядом (Hyper-V не копирует диски поверх
    /// существующих файлов — импорт падает с ошибкой «файл уже существует»).
    /// </summary>
    public static string UniqueFolder(string basePath)
    {
        var path = (basePath ?? "").Trim();
        if (path.Length == 0) return path;

        try
        {
            if (!Directory.Exists(path)) return path;
            if (!Directory.EnumerateFileSystemEntries(path).Any()) return path;

            for (int i = 2; i < 100; i++)
            {
                var candidate = path + "_" + i;
                if (!Directory.Exists(candidate)) return candidate;
                if (!Directory.EnumerateFileSystemEntries(candidate).Any()) return candidate;
            }

            return path + "_" + Guid.NewGuid().ToString("N")[..6];
        }
        catch
        {
            return path;
        }
    }

    /// <summary>Описывает папку копии (null — это не копия).</summary>
    internal static BackupCopyInfo? Describe(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        if (!BackupEngine.IsCopyFolderName(name)) return null;

        var created = DateTime.TryParseExact(name[..19], "yyyy-MM-dd_HH-mm-ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.MinValue;

        // Экспорт Hyper-V может лежать прямо в папке копии, а может — в подпапке
        // с именем машины (так делает сам Hyper-V при Export-VM).
        var exportRoot = FindExportRoot(folder);
        var isCheckpoint = Directory.Exists(Path.Combine(folder, "Disks"));
        if (exportRoot == null && !isCheckpoint) return null;

        var parent = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar));
        var vmName = parent == null ? name : Path.GetFileName(parent);
        var consistency = "";

        try
        {
            var infoPath = Path.Combine(folder, "vm-info.json");
            if (File.Exists(infoPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(infoPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("vmName", out var n) && n.ValueKind == JsonValueKind.String)
                    vmName = n.GetString() ?? vmName;
                if (root.TryGetProperty("consistency", out var c) && c.ValueKind == JsonValueKind.String)
                    consistency = c.GetString() ?? "";
            }
        }
        catch
        {
            // повреждённый vm-info.json не мешает восстановлению
        }

        return new BackupCopyInfo
        {
            VmName = vmName,
            Folder = folder,
            Created = created,
            SizeBytes = BackupEngine.DirectorySize(folder),
            Kind = exportRoot != null ? "export" : "checkpoint",
            Consistency = consistency,
            ExportRoot = exportRoot ?? ""
        };
    }

    /// <summary>Есть ли в папке признаки экспорта Hyper-V.</summary>
    private static bool HasExportLayout(string dir) =>
        Directory.Exists(Path.Combine(dir, "Virtual Machines"))
        || Directory.Exists(Path.Combine(dir, "Virtual Hard Disks"));

    /// <summary>
    /// Ищет папку с экспортом Hyper-V: сначала сама папка, затем вложенные
    /// (Hyper-V при экспорте создаёт подпапку с именем машины).
    /// </summary>
    private static string? FindExportRoot(string folder, int depth = 0)
    {
        if (depth > 2) return null;
        if (HasExportLayout(folder)) return folder;

        foreach (var sub in SafeGetDirectories(folder))
        {
            var found = FindExportRoot(sub, depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Восстанавливает виртуальную машину из копии.
    /// </summary>
    public static async Task<RestoreResult> RestoreAsync(
        BackupCopyInfo copy,
        RestoreOptions options,
        Action<string>? log,
        Action<BackupProgress>? progress,
        CancellationToken ct)
    {
        var result = new RestoreResult();

        try
        {
            if (!Directory.Exists(copy.Folder))
            {
                result.Error = "Папка копии не найдена: " + copy.Folder;
                return result;
            }

            if (string.IsNullOrWhiteSpace(options.VmName))
            {
                result.Error = "Не задано имя восстанавливаемой машины.";
                return result;
            }

            if (copy.IsExport)
            {
                progress?.Invoke(new BackupProgress
                {
                    Phase = "Восстановление: импорт виртуальной машины средствами Hyper-V",
                    Indeterminate = true
                });
                log?.Invoke($"Импорт копии «{copy.VmName}» из {copy.Folder}.");
                log?.Invoke(options.GenerateNewId
                    ? "Машина будет создана с новым идентификатором — исходная ВМ может остаться на месте."
                    : "Машина будет создана с исходным идентификатором — исходная ВМ должна быть предварительно удалена.");

                var vhdFolder = options.VhdPath.Trim();
                if (vhdFolder.Length > 0)
                {
                    Directory.CreateDirectory(vhdFolder);
                    WarnAboutFreeSpace(vhdFolder, copy, log);
                }

                // Экспорт может лежать во вложенной папке — импортируем именно её.
                var exportRoot = string.IsNullOrWhiteSpace(copy.ExportRoot) ? copy.Folder : copy.ExportRoot;
                var total = BackupEngine.DirectorySize(Path.Combine(exportRoot, "Virtual Hard Disks"));

                using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var watchTask = WatchRestoreProgress(vhdFolder, total, progress, watch.Token);
                try
                {
                    var (ok, name, id, err) = await Task.Run(() => HyperV.ImportVm(
                        exportRoot, options.VmName, options.VhdPath, options.VmPath,
                        options.GenerateNewId, options.StartAfter)).ConfigureAwait(false);

                    if (!ok)
                    {
                        result.Error = FriendlyError(err);
                        log?.Invoke("Ошибка импорта: " + result.Error);
                        return result;
                    }

                    result.VmName = name;
                    result.VmId = id;
                }
                finally
                {
                    watch.Cancel();
                    try { await watchTask.ConfigureAwait(false); } catch { }
                }

                log?.Invoke($"Машина «{result.VmName}» восстановлена (идентификатор {result.VmId}).");
            }
            else
            {
                progress?.Invoke(new BackupProgress
                {
                    Phase = "Восстановление: запуск скрипта из копии",
                    Indeterminate = true
                });

                var script = Path.Combine(copy.Folder, "restore.ps1");
                var dest = options.VmPath.Trim();
                if (string.IsNullOrWhiteSpace(dest))
                {
                    result.Error = "Для копии режима «контрольная точка» нужно указать папку для восстановления.";
                    return result;
                }

                if (!File.Exists(script))
                {
                    result.Error = "В папке копии нет скрипта restore.ps1 — восстановить её автоматически нельзя.";
                    return result;
                }

                log?.Invoke($"Восстановление копии «{copy.VmName}» скриптом restore.ps1 в {dest}.");
                var (ok, name, id, err) = await Task.Run(() => HyperV.RunRestoreScript(
                    script, dest, options.VmName, options.StartAfter)).ConfigureAwait(false);
                if (!ok)
                {
                    result.Error = FriendlyError(err);
                    log?.Invoke("Ошибка восстановления: " + result.Error);
                    return result;
                }
                result.VmName = string.IsNullOrWhiteSpace(name) ? options.VmName : name;
                result.VmId = id;
                log?.Invoke($"Машина «{result.VmName}» восстановлена.");
            }

            progress?.Invoke(new BackupProgress
            {
                Phase = "Восстановление завершено",
                Done = 1,
                Total = 1,
                Speed = 0,
                Indeterminate = false
            });

            result.Ok = true;
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Восстановление отменено.";
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            log?.Invoke("Ошибка восстановления: " + ex.Message);
            return result;
        }
    }

    /// <summary>Следит за папкой дисков: Hyper-V копирует туда виртуальные диски при импорте.</summary>
    private static async Task WatchRestoreProgress(string folder, long total, Action<BackupProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder) || progress == null) return;

        var watcher = new FolderProgressWatcher(folder);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                var (size, speed) = watcher.Sample();
                progress(new BackupProgress
                {
                    Phase = "Восстановление: копирование виртуальных дисков",
                    Done = size,
                    Total = total,
                    Speed = speed,
                    Indeterminate = total <= 0
                });
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Переводит сообщение PowerShell на понятный пользователю язык.</summary>
    internal static string FriendlyError(string error)
    {
        var text = (error ?? "").Trim();
        if (text.Length == 0) return "Восстановление не удалось.";

        var low = text.ToLowerInvariant();

        if (low.Contains("already exists") && low.Contains("identifier"))
            return "Машина с таким же идентификатором уже есть на этом хосте. " +
                   "Восстановите с новым идентификатором (это можно выбрать в диалоге) или удалите прежнюю машину.";

        if (low.Contains("already exists"))
            return "Машина с таким именем уже существует. Выберите другое имя для восстановления.";

        if (low.Contains("access is denied") || low.Contains("не удается получить доступ") || low.Contains("accessdenied"))
            return "Нет прав на операцию с Hyper-V. Запустите программу от имени администратора.";

        if (low.Contains("no hyper-v") || low.Contains("not supported") || low.Contains("hyper-v не установлен"))
            return "Hyper-V недоступен на этом компьютере.";

        if (low.Contains("not enough space") || low.Contains("недостаточно места"))
            return "Не хватает места на диске для восстановления. Укажите другую папку.";

        return text.Length > 400 ? text[..400] + "…" : text;
    }

    /// <summary>Предупреждает, если в целевой папке может не хватить места под копию дисков.</summary>
    private static void WarnAboutFreeSpace(string folder, BackupCopyInfo copy, Action<string>? log)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root)) return;

            var drive = new DriveInfo(root);
            var exportRoot = string.IsNullOrWhiteSpace(copy.ExportRoot) ? copy.Folder : copy.ExportRoot;
            var needed = BackupEngine.DirectorySize(Path.Combine(exportRoot, "Virtual Hard Disks"));
            if (needed <= 0) needed = copy.SizeBytes;
            if (needed <= 0) return;

            if (drive.AvailableFreeSpace < needed)
            {
                log?.Invoke($"ВНИМАНИЕ: на диске {root} свободно {ThrottledCopier.FormatBytes(drive.AvailableFreeSpace)}, " +
                           $"а копия занимает {ThrottledCopier.FormatBytes(needed)}. Восстановление может не поместиться.");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("Не удалось проверить свободное место: " + ex.Message);
        }
    }
}
