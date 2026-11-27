using System.Diagnostics;
using System.Text.Json;

namespace HvBackup;

/// <summary>
/// Следит за ростом папки экспорта: показывает, сколько уже выгружено
/// и с какой скоростью идёт выгрузка.
/// </summary>
internal sealed class FolderProgressWatcher
{
    private readonly string _folder;
    private long _lastSize;
    private DateTime _lastTime = DateTime.UtcNow;
    private bool _hasBaseline;

    public FolderProgressWatcher(string folder) => _folder = folder;

    /// <summary>Текущий объём папки (байт) и скорость её роста (байт/с).</summary>
    public (long Size, double Speed) Sample()
    {
        var size = BackupEngine.DirectorySize(_folder);
        var now = DateTime.UtcNow;
        var dt = (now - _lastTime).TotalSeconds;

        // Скорость считаем только после первого замера: иначе получилась бы
        // мгновенная «скорость» с нуля, обманчиво большая.
        var speed = _hasBaseline && dt > 0.2 ? (size - _lastSize) / dt : 0;

        _lastSize = size;
        _lastTime = now;
        _hasBaseline = true;
        return (size, speed);
    }
}

public sealed class BackupProgress
{
    public string Phase { get; set; } = "";
    public string Detail { get; set; } = "";
    public long Done { get; set; }
    public long Total { get; set; }
    public double Speed { get; set; }
    public bool Indeterminate { get; set; }

    /// <summary>Общий объём известен лишь примерно (например, при горячем экспорте ВМ).</summary>
    public bool Estimated { get; set; }
}

public sealed class BackupResult
{
    public bool Ok { get; set; }
    public bool Cancelled { get; set; }
    public string Error { get; set; } = "";
    public string Folder { get; set; } = "";
    public long Bytes { get; set; }
    public TimeSpan Duration { get; set; }
    public int RemovedOld { get; set; }
    public string Consistency { get; set; } = "";
}

/// <summary>Горячее резервное копирование ВМ Hyper-V без остановки виртуальной машины.</summary>
public sealed class BackupEngine
{
    /// <summary>План копирования: какие файлы дисков и в каком порядке копировать.</summary>
    public sealed class PlanFile
    {
        public string Source = "";
        public string Target = "";
        public long Size;
        public string ParentSource = "";
        public bool IsTop;
        public int ControllerNumber;
        public int ControllerLocation;
        public string ControllerType = "SCSI";
    }

    /// <summary>
    /// Определяет, какие файлы дисков копировать.
    /// Для работающей ВМ активный диск пропускается: он меняется прямо сейчас,
    /// копируются «замороженные» предки — их содержимое соответствует моменту копии.
    /// </summary>
    public static List<PlanFile> BuildPlan(IReadOnlyList<DiskInfo> disks, bool running)
    {
        var plan = new List<PlanFile>();
        foreach (var disk in disks)
        {
            var chain = disk.Chain;
            if (chain == null || chain.Count == 0) continue;

            int start = running ? 1 : 0;
            for (int i = start; i < chain.Count; i++)
            {
                var item = chain[i];
                if (string.IsNullOrWhiteSpace(item.Path)) continue;
                plan.Add(new PlanFile
                {
                    Source = item.Path,
                    Size = item.Size,
                    ParentSource = item.ParentPath ?? "",
                    IsTop = i == start,
                    ControllerNumber = disk.ControllerNumber,
                    ControllerLocation = disk.ControllerLocation,
                    ControllerType = string.IsNullOrEmpty(disk.ControllerType) ? "SCSI" : disk.ControllerType
                });
            }
        }
        return plan;
    }

    private readonly AppSettings _settings;
    private readonly Action<string> _log;
    private readonly Action<BackupProgress> _progress;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    /// <summary>Эффективные настройки текущей ВМ (общие + индивидуальные).</summary>
    private AppSettings _eff;

    public BackupEngine(AppSettings settings, Action<string> log, Action<BackupProgress> progress)
    {
        _settings = settings;
        _eff = settings;
        _log = log;
        _progress = progress;
    }

    private long LimitBytes => (long)_eff.SpeedLimitMBps * 1024 * 1024;

    private void Phase(string phase, bool indeterminate = true)
        => _progress(new BackupProgress { Phase = phase, Indeterminate = indeterminate, Done = _progressDone, Total = _progressTotal });

    private long _progressDone;
    private long _progressTotal;

    public async Task<BackupResult> RunAsync(string vmName, CancellationToken ct)
    {
        var result = new BackupResult();
        var work = "";
        string? snapshotName = null;
        string? originalCheckpointType = null;
        var checkpointTypeChanged = false;

        // Индивидуальные настройки этой ВМ перекрывают общие.
        _eff = _settings.ForVm(vmName);

        try
        {
            Phase("Получение сведений о виртуальной машине");
            var (okVms, vms, errVms) = await Task.Run(() => HyperV.ListVms());
            if (!okVms) throw new InvalidOperationException(errVms);

            var vm = vms.FirstOrDefault(v => string.Equals(v.Name, vmName, StringComparison.OrdinalIgnoreCase));
            if (vm == null) throw new InvalidOperationException($"Виртуальная машина «{vmName}» не найдена.");

            var running = vm.State.Contains("Running", StringComparison.OrdinalIgnoreCase);
            _log($"ВМ «{vmName}»: состояние {vm.State}, дисков {vm.Disks.Count}, память {vm.MemoryText}.");
            if (_settings.PerVmSettings(vmName) != null)
                _log($"Применены индивидуальные настройки ВМ: скорость {_eff.SpeedLimitMBps} МБ/с, " +
                     $"папка «{_eff.BackupRoot}», копий {_eff.KeepCopies}, режим {_eff.Mode}.");

            var vmFolder = Path.Combine(_eff.BackupRoot, SafeName(vmName));
            Directory.CreateDirectory(vmFolder);
            work = Path.Combine(vmFolder, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(work);
            result.Folder = work;

            if (_eff.Mode == CopyMode.VmExport || _eff.Mode == CopyMode.HotExport)
                return await ExportModeAsync(vm, work, result, ct);

            var consistency = "снимок состояния дисков (контрольная точка)";

            if (running)
            {
                if (_eff.UseProductionCheckpoint &&
                    !string.Equals(vm.CheckpointType, "Production", StringComparison.OrdinalIgnoreCase))
                {
                    Phase("Временное переключение типа контрольной точки на Production (VSS)");
                    originalCheckpointType = vm.CheckpointType;
                    var (okType, newType, errType) = await Task.Run(() => HyperV.SetCheckpointType(vmName, "Production"));
                    if (okType)
                    {
                        checkpointTypeChanged = true;
                        _log($"Тип контрольной точки ВМ «{vmName}»: {vm.CheckpointType} → {newType} (вернём обратно после копии).");
                    }
                    else
                    {
                        _log("Не удалось переключить тип контрольной точки: " + errType);
                    }
                }

                Phase("Снятие контрольной точки (машина продолжает работать)");
                snapshotName = "HvBackup " + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var (okCp, cp, errCp) = await Task.Run(() => HyperV.CreateCheckpoint(vmName, snapshotName));
                if (!okCp) throw new InvalidOperationException("Не удалось снять контрольную точку: " + errCp);
                snapshotName = cp.Name;
                consistency = cp.Type.Contains("Production", StringComparison.OrdinalIgnoreCase)
                    ? "Production (VSS, согласованная на уровне приложений)"
                    : "Standard (согласованная на уровне дисков, crash-consistent)";
                _log($"Контрольная точка «{cp.Name}» создана, тип: {cp.Type}. ВМ продолжает работать.");
            }
            else
            {
                _log($"ВМ «{vmName}» не запущена — диски копируются напрямую, без контрольной точки.");
                consistency = "машина была выключена (полная копия)";
            }

            result.Consistency = consistency;

            Phase("Чтение состава виртуальных дисков");
            var (okDisks, disks, errDisks) = await Task.Run(() => HyperV.GetDiskChain(vmName));
            if (!okDisks) throw new InvalidOperationException(errDisks);
            if (disks.Count == 0) throw new InvalidOperationException("У виртуальной машины нет дисков для копирования.");

            var plan = BuildPlan(disks, running);
            plan = plan.Where(p => File.Exists(p.Source)).ToList();
            if (plan.Count == 0) throw new InvalidOperationException("Не найдено файлов дисков для копирования.");

            _progressTotal = plan.Sum(p => p.Size);
            _progressDone = 0;

            var disksDir = Path.Combine(work, "Disks");
            Directory.CreateDirectory(disksDir);

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var aggregate = new AggregateProgress(this, plan);

            foreach (var file in plan)
            {
                ct.ThrowIfCancellationRequested();
                var target = UniquePath(disksDir, Path.GetFileName(file.Source), used);
                file.Target = target;
                _currentFileName = Path.GetFileName(file.Source);

                Phase($"Копирование: {Path.GetFileName(file.Source)}", indeterminate: false);
                _log($"Копирование {file.Source} → {target} ({ThrottledCopier.FormatBytes(file.Size)}).");

                var done = await ThrottledCopier.CopyFileAsync(file.Source, target, LimitBytes, aggregate, ct);
                result.Bytes += done;
                _progressDone += done;
                aggregate.Base = _progressDone;

                var srcSize = new FileInfo(file.Source).Length;
                var dstSize = new FileInfo(target).Length;
                if (dstSize != srcSize)
                    _log($"ВНИМАНИЕ: размер копии {Path.GetFileName(target)} ({dstSize}) отличается от оригинала ({srcSize}).");
            }

            Phase("Связывание цепочки дисков в копии", indeterminate: false);
            var links = new List<(string Child, string Parent)>();
            foreach (var file in plan)
            {
                if (string.IsNullOrWhiteSpace(file.ParentSource)) continue;
                var parent = plan.FirstOrDefault(p => SamePath(p.Source, file.ParentSource));
                if (parent != null && !string.IsNullOrEmpty(parent.Target))
                    links.Add((file.Target, parent.Target));
            }
            var tops = plan.Where(p => p.IsTop).Select(p => p.Target).ToList();
            if (links.Count > 0 || tops.Count > 0)
            {
                var (okFix, report, errFix) = await Task.Run(() => HyperV.FixLocators(links, tops));
                if (!okFix) _log("Не удалось поправить связи дисков: " + errFix);
                else
                    foreach (var r in report)
                        if (r.Status != "ok")
                            _log($"Связи дисков ({r.Status}): {Path.GetFileName(r.Child)} — {r.Message}");
            }

            Phase("Сохранение метаданных копии", indeterminate: false);
            WriteMeta(vm, plan, work, result, consistency);

            // ── Дополнительные хранилища: сетевая папка и FTP ──
            var mirrored = await MirrorAsync(vm, work, ct);

            if (!_eff.KeepLocalCopy && mirrored > 0)
            {
                Phase("Удаление локальной копии", indeterminate: false);
                try
                {
                    Directory.Delete(work, true);
                    _log($"Локальная копия удалена (копия есть в удалённых хранилищах).");
                }
                catch (Exception ex)
                {
                    _log("Не удалось удалить локальную копию: " + ex.Message);
                }
            }

            result.Ok = true;
            _log($"Копия «{vmName}» готова: {work} ({ThrottledCopier.FormatBytes(result.Bytes)}).");
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            result.Error = "Копирование отменено пользователем.";
            _log("Копирование отменено. Завершаем контрольную точку: удаляем её и возвращаем исходный тип точки — это может занять до минуты.");
            TryDeleteFolder(work);
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _log("ОШИБКА копирования: " + ex.Message);
            TryDeleteFolder(work);
        }
        finally
        {
            if (snapshotName != null)
            {
                Phase("Удаление контрольной точки (слияние разностного диска)");
                _log("Удаляем контрольную точку — идёт слияние разностного диска с основным, это может занять время.");
                try
                {
                    // Слияние разностного диска идёт в фоновом потоке: окно остаётся живым,
                    // фаза видна, приложение не «зависает».
                    var (okR, errR) = await Task.Run(() => HyperV.RemoveCheckpoint(vmName, snapshotName));
                    if (!okR) _log("Не удалось удалить контрольную точку: " + errR);
                    else _log("Контрольная точка удалена, разностный диск слит с основным.");
                }
                catch (Exception ex)
                {
                    _log("Ошибка при удалении контрольной точки: " + ex.Message);
                }
            }
            if (checkpointTypeChanged && !string.IsNullOrEmpty(originalCheckpointType))
            {
                Phase("Возврат исходного типа контрольной точки (несколько секунд)");
                _log("Возвращаем исходный тип контрольной точки: " + originalCheckpointType + ".");
                try
                {
                    var (okT, _, errT) = await Task.Run(() => HyperV.SetCheckpointType(vmName, originalCheckpointType));
                    if (okT) _log($"Тип контрольной точки возвращён: {originalCheckpointType}.");
                    else _log("Не удалось вернуть тип контрольной точки: " + errT);
                }
                catch (Exception ex)
                {
                    _log("Ошибка при возврате типа контрольной точки: " + ex.Message);
                }
            }
            result.Duration = _sw.Elapsed;
        }

        if (result.Ok)
        {
            try
            {
                result.RemovedOld = Rotate(Path.GetDirectoryName(result.Folder) ?? "", _eff.KeepCopies);
                if (result.RemovedOld > 0) _log($"Удалено старых копий: {result.RemovedOld} (осталось {_eff.KeepCopies}).");
            }
            catch (Exception ex)
            {
                _log("Не удалось выполнить ротацию копий: " + ex.Message);
            }
        }

        return result;
    }

    /// <summary>Раскладывает готовую копию по дополнительным хранилищам. Возвращает число успешных.</summary>
    private async Task<int> MirrorAsync(VmInfo vm, string work, CancellationToken ct)
    {
        var success = 0;

        if (_eff.NetworkEnabled)
        {
            var vmRemote = Path.Combine(_eff.NetworkPath, SafeName(vm.Name));
            var dest = Path.Combine(vmRemote, Path.GetFileName(work));

            Phase("Копирование в сетевую папку", indeterminate: false);
            var (okConn, errConn) = NetworkShare.Connect(_eff.NetworkPath, _eff.NetworkUser, _eff.NetworkPassword);
            if (!okConn)
            {
                _log("Сетевая папка недоступна: " + errConn);
            }
            else
            {
                try
                {
                    var files = Directory.GetFiles(work, "*", SearchOption.AllDirectories);
                    var bytes = files.Sum(f => new FileInfo(f).Length);
                    _progressTotal = bytes;
                    _progressDone = 0;

                    await NetworkShare.MirrorAsync(work, dest, LimitBytes,
                        new MirrorProgress(this, "Копирование в сетевую папку", bytes), ct);
                    _log($"Скопировано в сетевую папку: {dest}");
                    success++;

                    var removed = NetworkShare.Rotate(vmRemote, _eff.KeepCopies);
                    if (removed > 0) _log($"В сетевой папке удалено старых копий: {removed}.");
                }
                catch (Exception ex)
                {
                    _log("Ошибка копирования в сетевую папку: " + ex.Message);
                }
                finally
                {
                    NetworkShare.Disconnect(_eff.NetworkPath);
                }
            }
        }

        if (_eff.FtpEnabled)
        {
            var remoteBase = CombineRemote(_eff.FtpRemoteDir, SafeName(vm.Name));
            var remoteCopy = CombineRemote(remoteBase, Path.GetFileName(work));

            Phase("Отправка на FTP-сервер", indeterminate: false);
            try
            {
                var files = Directory.GetFiles(work, "*", SearchOption.AllDirectories);
                var bytes = files.Sum(f => new FileInfo(f).Length);
                _progressTotal = bytes;
                _progressDone = 0;

                using var ftp = new FtpClient(
                    _eff.FtpHost, _eff.FtpPort, _eff.FtpUser, _eff.FtpPassword,
                    _eff.FtpTlsMode == 1, _eff.FtpTlsMode == 2)
                {
                    Passive = _eff.FtpPassive,
                    SkipCertificateValidation = _eff.FtpSkipCertificateValidation
                };

                await ftp.ConnectAsync();
                _log($"FTP: подключение к {_eff.FtpHost}:{_eff.FtpPort} установлено.");
                await ftp.EnsureDirectoryAsync(remoteCopy);

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(work, file).Replace('\\', '/');
                    var remoteFile = CombineRemote(remoteCopy, relative);
                    var remoteDir = remoteFile[..remoteFile.LastIndexOf('/')];
                    await ftp.EnsureDirectoryAsync(remoteDir);
                    await ftp.UploadFileAsync(file, remoteFile, LimitBytes,
                        new FtpProgress(this, "Отправка на FTP-сервер", bytes), ct);
                    _log($"FTP: загружен {relative} ({ThrottledCopier.FormatBytes(new FileInfo(file).Length)}).");
                }

                _log($"Копия загружена на FTP: {remoteCopy}");
                success++;

                // Ротация на FTP: удаляем самые старые копии этой ВМ.
                try
                {
                    var entries = await ftp.ListAsync(remoteBase);
                    var copies = entries
                        .Where(e => !e.IsDirectory && IsCopyFolderName(e.Name))
                        .OrderByDescending(e => e.Name, StringComparer.Ordinal)
                        .Skip(_eff.KeepCopies)
                        .ToList();
                    foreach (var old in copies)
                    {
                        try { await ftp.DeleteDirectoryAsync(CombineRemote(remoteBase, old.Name)); }
                        catch (Exception ex) { _log($"FTP: не удалось удалить старую копию {old.Name}: {ex.Message}"); }
                    }
                    if (copies.Count > 0) _log($"На FTP удалено старых копий: {copies.Count}.");
                }
                catch (Exception ex)
                {
                    _log("FTP: не удалось выполнить ротацию: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                _log("Ошибка отправки на FTP: " + ex.Message);
            }
        }

        return success;
    }

    private static string CombineRemote(string a, string b) =>
        (a ?? "").TrimEnd('/') + "/" + (b ?? "").TrimStart('/');

    /// <summary>Проверяет, что имя папки соответствует шаблону даты копии.</summary>
    public static bool IsCopyFolderName(string name) =>
        !string.IsNullOrEmpty(name) && name.Length >= 19 &&
        DateTime.TryParseExact(name[..19], "yyyy-MM-dd_HH-mm-ss", null,
            System.Globalization.DateTimeStyles.None, out _);

    private sealed class MirrorProgress : IProgress<CopyProgress>
    {
        private readonly BackupEngine _engine;
        private readonly string _phase;
        private readonly long _total;
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

        public MirrorProgress(BackupEngine engine, string phase, long total)
        {
            _engine = engine;
            _phase = phase;
            _total = total;
        }

        public void Report(CopyProgress value)
        {
            var speed = _sw.Elapsed.TotalSeconds > 0.3 ? value.BytesDone / _sw.Elapsed.TotalSeconds : 0;
            _engine._progress(new BackupProgress
            {
                Phase = _phase,
                Done = value.BytesDone,
                Total = _total > 0 ? _total : value.TotalBytes,
                Speed = speed,
                Indeterminate = false
            });
        }
    }

    private sealed class FtpProgress : IProgress<long>
    {
        private readonly BackupEngine _engine;
        private readonly string _phase;
        private readonly long _total;
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

        public FtpProgress(BackupEngine engine, string phase, long total)
        {
            _engine = engine;
            _phase = phase;
            _total = total;
        }

        public void Report(long sent)
        {
            var speed = _sw.Elapsed.TotalSeconds > 0.3 ? sent / _sw.Elapsed.TotalSeconds : 0;
            _engine._progress(new BackupProgress
            {
                Phase = _phase,
                Done = sent,
                Total = _total,
                Speed = speed,
                Indeterminate = false
            });
        }
    }

    /// <summary>
    /// Копирование через экспорт Hyper-V.
    /// <para>
    /// <see cref="CopyMode.HotExport"/>: работающая ВМ выгружается целиком (конфигурация + диски)
    /// во временную папку, затем копия переносится в папку копий <b>с ограничением скорости</b>.
    /// </para>
    /// <para>
    /// <see cref="CopyMode.VmExport"/>: экспорт сразу в папку копий — быстро, без ограничения скорости.
    /// </para>
    /// <para>В обоих случаях машина не останавливается: Hyper-V снимает согласованный Production-снимок.</para>
    /// </summary>
    private async Task<BackupResult> ExportModeAsync(VmInfo vm, string work, BackupResult result, CancellationToken ct)
    {
        var live = vm.State.Contains("Running", StringComparison.OrdinalIgnoreCase);
        var withLimit = _eff.Mode == CopyMode.HotExport && LimitBytes > 0;
        var tmp = "";

        try
        {
            if (withLimit)
            {
                tmp = PrepareTempExportFolder(vm.Name);
                WarnAboutFreeSpace(tmp, vm);

                Phase("Экспорт виртуальной машины средствами Hyper-V (машина продолжает работать)");
                _log($"Экспорт ВМ «{vm.Name}» во временную папку {tmp}. " +
                     "Hyper-V выгружает диски сам и без ограничения скорости — ограничение применяется при переносе копии.");
                var (ok, err) = await RunExportAsync(vm, tmp, live, ct);
                if (!ok) throw new InvalidOperationException("Ошибка экспорта: " + err);

                ct.ThrowIfCancellationRequested();

                Phase("Перенос копии в папку копий (с ограничением скорости)");
                var bytes = DirectorySize(tmp);
                _progressTotal = bytes;
                _progressDone = 0;
                _log($"Переносим экспорт в папку копий с ограничением {ThrottledCopier.FormatBytes(LimitBytes)}/с.");
                await ThrottledCopier.CopyDirectoryAsync(tmp, work, LimitBytes,
                    new MirrorProgress(this, "Перенос копии в папку копий (с ограничением скорости)", bytes), ct);
                result.Bytes = bytes;
            }
            else
            {
                Phase("Экспорт виртуальной машины средствами Hyper-V");
                _log($"Экспорт ВМ «{vm.Name}» в {work} (без ограничения скорости).");
                var (ok, err) = await RunExportAsync(vm, work, live, ct);
                if (!ok) throw new InvalidOperationException("Ошибка экспорта: " + err);
                result.Bytes = DirectorySize(work);
            }

            result.Consistency = live
                ? "экспорт Hyper-V работающей ВМ (согласованный Production-снимок VSS, машина не останавливалась)"
                : "экспорт Hyper-V (машина была выключена)";

            WriteExportMeta(vm, work, result, live);

            result.Ok = true;
            _log($"Экспорт ВМ «{vm.Name}» готов: {work} ({ThrottledCopier.FormatBytes(result.Bytes)}). " +
                 "Восстановление: Import-VM -Path <папка копии>.");
            return result;
        }
        finally
        {
            if (!string.IsNullOrEmpty(tmp) && Directory.Exists(tmp))
            {
                try
                {
                    Directory.Delete(tmp, true);
                    _log("Временная папка экспорта удалена.");
                }
                catch (Exception ex)
                {
                    _log("Не удалось удалить временную папку экспорта " + tmp + ": " + ex.Message);
                }
            }
        }
    }

    /// <summary>
    /// Экспорт ВМ средствами Hyper-V. Пока идёт выгрузка, следим за размером папки
    /// экспорта и показываем, сколько уже выгружено, с какой скоростью идёт выгрузка
    /// и сколько примерно осталось (общий объём известен лишь по объёму дисков ВМ).
    /// </summary>
    private async Task<(bool Ok, string Error)> RunExportAsync(VmInfo vm, string folder, bool live, CancellationToken ct)
    {
        var phase = live
            ? "Экспорт виртуальной машины средствами Hyper-V (машина продолжает работать)"
            : "Экспорт виртуальной машины средствами Hyper-V";

        using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchTask = WatchExportProgress(vm, folder, phase, watch.Token);
        try
        {
            return await Task.Run(() => HyperV.ExportVm(vm.Name, folder, live)).ConfigureAwait(false);
        }
        finally
        {
            watch.Cancel();
            try { await watchTask.ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>Следит за папкой экспорта и сообщает ход выгрузки.</summary>
    private async Task WatchExportProgress(VmInfo vm, string folder, string phase, CancellationToken ct)
    {
        var estimate = vm.DiskSizeBytes;      // объём дисков ВМ — грубая оценка общего объёма
        var watcher = new FolderProgressWatcher(folder);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);

                var (size, speed) = watcher.Sample();
                _progressDone = size;
                _progressTotal = estimate;

                _progress(new BackupProgress
                {
                    Phase = phase,
                    Done = size,
                    Total = estimate,
                    Speed = speed,
                    Indeterminate = estimate <= 0,
                    Estimated = estimate > 0
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось отследить ход экспорта: " + ex.Message);
        }
    }

    /// <summary>Готовит временную папку для экспорта (в неё Hyper-V выгружает работающую ВМ).</summary>
    private string PrepareTempExportFolder(string vmName)
    {
        var root = string.IsNullOrWhiteSpace(_eff.ExportTempRoot)
            ? Path.Combine(Path.GetTempPath(), "HvBackupExport")
            : _eff.ExportTempRoot.Trim();
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, $"{SafeName(vmName)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Предупреждает в журнал, если во временной папке может не хватить места под экспорт.</summary>
    private void WarnAboutFreeSpace(string tempFolder, VmInfo vm)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(tempFolder));
            if (string.IsNullOrEmpty(root)) return;
            var drive = new DriveInfo(root);
            var needed = vm.DiskSizeBytes > 0 ? vm.DiskSizeBytes : 0;
            if (needed <= 0) return;

            if (drive.AvailableFreeSpace < needed)
            {
                _log($"ВНИМАНИЕ: на диске {root} свободно {ThrottledCopier.FormatBytes(drive.AvailableFreeSpace)}, " +
                     $"а объём ВМ «{vm.Name}» — {ThrottledCopier.FormatBytes(needed)} (по виртуальному размеру дисков). " +
                     "Экспорт может не поместиться — укажите папку временного экспорта на диске с большим объёмом.");
            }
        }
        catch (Exception ex)
        {
            _log("Не удалось проверить свободное место: " + ex.Message);
        }
    }

    /// <summary>Суммарный размер файлов в каталоге.</summary>
    public static long DirectorySize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        try
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Справочные файлы рядом с экспортом: параметры ВМ и памятка по восстановлению.</summary>
    internal void WriteExportMeta(VmInfo vm, string work, BackupResult result, bool live)
    {
        var meta = new
        {
            vmName = vm.Name,
            vmId = vm.Id,
            createdLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            appVersion = AppVersion.Value,
            mode = _eff.Mode.ToString(),
            consistency = result.Consistency,
            generation = vm.Generation,
            memoryStartupBytes = vm.MemoryStartupBytes,
            processorCount = vm.ProcessorCount,
            switchName = vm.SwitchName,
            macAddress = vm.MacAddress,
            checkpointType = vm.CheckpointType,
            sourceDiskPaths = vm.Disks,
            restoreCommand = "Import-VM -Path \"" + work + "\""
        };
        try
        {
            File.WriteAllText(Path.Combine(work, "vm-info.json"),
                JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log("Не удалось записать vm-info.json: " + ex.Message);
        }

        var readme = $"""
            ГОРЯЧИЙ ЭКСПОРТ ВИРТУАЛЬНОЙ МАШИНЫ (Hyper-V Export)
            ====================================================
            Виртуальная машина : {vm.Name}
            Дата копии         : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
            Консистентность    : {result.Consistency}
            Исходная машина    : {vm.ConfigLocation}

            ЧТО ЭТО
              Папка — полноценный экспорт Hyper-V: конфигурация машины, виртуальные
              диски и контрольные точки. Машина во время копирования продолжала работать,
              поэтому состояние гостевой ОС согласовано на уровне приложений (VSS).

            КАК ВОССТАНОВИТЬ
              1. Откройте PowerShell от имени администратора.
              2. Import-VM -Path '<путь к этой папке>'
              3. Start-VM -Name '{vm.Name}'

              Важно: импорт невозможен, пока существует ВМ с тем же идентификатором.
              Если такая машина есть — переименуйте или удалите её заранее.

            ДОПОЛНИТЕЛЬНО
              vm-info.json — параметры исходной ВМ (память, ЦП, поколение, ключ и т.п.) для справки.
            """;
        try
        {
            File.WriteAllText(Path.Combine(work, "README.txt"), readme);
        }
        catch (Exception ex)
        {
            _log("Не удалось записать README.txt: " + ex.Message);
        }
    }

    private void WriteMeta(VmInfo vm, List<PlanFile> plan, string work, BackupResult result, string consistency)
    {
        var specs = new List<RestoreScript.DiskSpec>();
        foreach (var p in plan)
        {
            var parentTarget = plan.FirstOrDefault(x => SamePath(x.Source, p.ParentSource));
            specs.Add(new RestoreScript.DiskSpec
            {
                File = Path.GetFileName(p.Target),
                Source = p.Source,
                ParentFile = parentTarget != null ? Path.GetFileName(parentTarget.Target) : "",
                SizeBytes = p.Size,
                ControllerNumber = p.ControllerNumber,
                ControllerLocation = p.ControllerLocation,
                ControllerType = p.ControllerType,
                IsTop = p.IsTop
            });
        }
        var top = plan.FirstOrDefault(p => p.IsTop);
        var topFile = top != null ? Path.GetFileName(top.Target) : (plan.Count > 0 ? Path.GetFileName(plan[0].Target) : "");

        var meta = new
        {
            vmName = vm.Name,
            vmId = vm.Id,
            createdLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            createdUtc = DateTime.UtcNow.ToString("o"),
            appVersion = "1.0.0",
            mode = _eff.Mode.ToString(),
            consistency,
            generation = vm.Generation,
            memoryStartupBytes = vm.MemoryStartupBytes,
            processorCount = vm.ProcessorCount,
            switchName = vm.SwitchName,
            macAddress = vm.MacAddress,
            checkpointType = vm.CheckpointType,
            sourceDiskPaths = vm.Disks,
            topDiskFile = topFile,
            disks = plan.Select((p, i) => new
            {
                index = i,
                file = Path.GetFileName(p.Target),
                source = p.Source,
                parentFile = specs[i].ParentFile,
                sizeBytes = p.Size,
                controllerType = p.ControllerType,
                controllerNumber = p.ControllerNumber,
                controllerLocation = p.ControllerLocation,
                isTop = p.IsTop
            })
        };

        File.WriteAllText(Path.Combine(work, "vm-info.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

        File.WriteAllText(Path.Combine(work, "restore.ps1"),
            RestoreScript.Build(vm.Name, vm.Generation, vm.MemoryStartupBytes, vm.ProcessorCount,
                vm.SwitchName, vm.MacAddress, specs, topFile,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), consistency));

        File.WriteAllText(Path.Combine(work, "README.txt"),
            RestoreScript.BuildReadme(vm.Name, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), consistency, specs, topFile));
    }

    /// <summary>Удаляет старые копии, оставляя keep последних.</summary>
    public static int Rotate(string vmFolder, int keep)
    {
        if (!Directory.Exists(vmFolder)) return 0;
        keep = Math.Max(1, keep);

        var dirs = Directory.GetDirectories(vmFolder)
            .Select(d => new { Path = d, Name = Path.GetFileName(d) })
            .Where(x => IsCopyFolderName(x.Name))
            .OrderByDescending(x => x.Name, StringComparer.Ordinal)
            .ToList();

        var removed = 0;
        foreach (var d in dirs.Skip(keep))
        {
            try
            {
                Directory.Delete(d.Path, true);
                removed++;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Не удалось удалить старую копию {d.Name}: {ex.Message}");
            }
        }
        return removed;
    }

    private sealed class AggregateProgress : IProgress<CopyProgress>
    {
        private readonly BackupEngine _engine;
        private readonly List<PlanFile> _plan;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public long Base;

        public AggregateProgress(BackupEngine engine, List<PlanFile> plan)
        {
            _engine = engine;
            _plan = plan;
        }

        public void Report(CopyProgress value)
        {
            var done = Base + value.BytesDone;
            var speed = _sw.Elapsed.TotalSeconds > 0.3 ? done / _sw.Elapsed.TotalSeconds : 0;
            _engine._progress(new BackupProgress
            {
                Phase = _engine._currentFileName,
                Detail = "",
                Done = done,
                Total = _engine._progressTotal,
                Speed = speed,
                Indeterminate = false
            });
        }
    }

    private string _currentFileName = "";

    public static bool SamePath(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString().Trim();
    }

    private static string UniquePath(string dir, string fileName, HashSet<string> used)
    {
        var name = fileName;
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var candidate = Path.Combine(dir, name);
        int i = 1;
        while (used.Contains(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            i++;
        }
        used.Add(candidate);
        return candidate;
    }

    private static void TryDeleteFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        try { Directory.Delete(folder, true); }
        catch (Exception ex) { AppLog.Warn($"Не удалось удалить незавершённую копию {folder}: {ex.Message}"); }
    }
}
