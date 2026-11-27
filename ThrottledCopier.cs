using System.Diagnostics;

namespace HvBackup;

public sealed record CopyProgress(long BytesDone, long TotalBytes, double BytesPerSecond)
{
    public double Percent => TotalBytes <= 0 ? 0 : Math.Min(100.0, BytesDone * 100.0 / TotalBytes);
    public TimeSpan Remaining
    {
        get
        {
            if (BytesPerSecond <= 1 || TotalBytes <= BytesDone) return TimeSpan.Zero;
            var seconds = (TotalBytes - BytesDone) / BytesPerSecond;
            return seconds > TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
        }
    }
}

/// <summary>
/// Отчёт о прогрессе, который вызывается сразу, в том же потоке.
/// Нужен там, где обычный <see cref="Progress{T}"/> переключил бы поток и потерял порядок сообщений.
/// </summary>
public sealed class ActionProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;

    public ActionProgress(Action<T> action) => _action = action;

    public void Report(T value) => _action(value);
}

/// <summary>Копирование файла с ограничением средней скорости (МБ/с).</summary>
public static class ThrottledCopier
{
    private const int BufferSize = 4 * 1024 * 1024;

    /// <summary>Минимальный интервал между сообщениями о прогрессе (мс).</summary>
    private const long ReportIntervalMs = 250;

    /// <summary>Копирует файл, не превышая заданную среднюю скорость. limitBytesPerSec = 0 — без ограничения.</summary>
    public static async Task<long> CopyFileAsync(
        string source,
        string target,
        long limitBytesPerSec,
        IProgress<CopyProgress>? progress,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long done = 0;

        var fi = new FileInfo(source);
        long total = fi.Exists ? fi.Length : 0;

        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var output = new FileStream(
            target, FileMode.Create, FileAccess.Write,
            FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[BufferSize];
        var lastReport = 0L;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false);
            if (read <= 0) break;

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;

            if (limitBytesPerSec > 0)
            {
                // Держим среднюю скорость: ждём, пока фактически прошедшее время догонит ожидаемое.
                var expected = (double)done / limitBytesPerSec;
                var delayMs = (expected - sw.Elapsed.TotalSeconds) * 1000.0;
                if (delayMs > 1)
                    await Task.Delay((int)Math.Min(delayMs, 2000), ct).ConfigureAwait(false);
            }

            // Отчёты — не чаще 4 раз в секунду: иначе окно забивается сообщениями.
            var now = sw.ElapsedMilliseconds;
            if (progress != null && now - lastReport >= ReportIntervalMs)
            {
                lastReport = now;
                var speed = sw.Elapsed.TotalSeconds > 0.2 ? done / sw.Elapsed.TotalSeconds : 0;
                progress.Report(new CopyProgress(done, total, speed));
            }
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
        await output.DisposeAsync().ConfigureAwait(false);

        // Итоговый отчёт — всегда, даже если файл маленький.
        if (progress != null)
        {
            var speed = sw.Elapsed.TotalSeconds > 0.2 ? done / sw.Elapsed.TotalSeconds : 0;
            progress.Report(new CopyProgress(done, total, speed));
        }
        return done;
    }

    /// <summary>
    /// Копирует дерево каталогов с ограничением средней скорости, сохраняя структуру папок.
    /// limitBytesPerSec = 0 — без ограничения.
    /// </summary>
    public static async Task<long> CopyDirectoryAsync(
        string sourceDir,
        string targetDir,
        long limitBytesPerSec,
        IProgress<CopyProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);

        var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        var total = files.Sum(f => new FileInfo(f).Length);
        long done = 0;
        var sw = Stopwatch.StartNew();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(targetDir, relative);
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Отчёт по файлу складываем с уже скопированными байтами каталога,
            // иначе прогресс по большому VHDX не виден до конца файла.
            var inner = new DirectoryProgress(progress, done, total, sw);
            var copied = await CopyFileAsync(file, target, limitBytesPerSec, inner, ct).ConfigureAwait(false);
            done += copied;
        }

        if (progress != null)
        {
            var speed = sw.Elapsed.TotalSeconds > 0.3 ? done / sw.Elapsed.TotalSeconds : 0;
            progress.Report(new CopyProgress(done, total, speed));
        }
        return done;
    }

    /// <summary>
    /// Складывает прогресс отдельного файла с уже скопированными байтами каталога
    /// и считает общую скорость копирования.
    /// </summary>
    private sealed class DirectoryProgress : IProgress<CopyProgress>
    {
        private readonly IProgress<CopyProgress>? _target;
        private readonly long _base;
        private readonly long _total;
        private readonly Stopwatch _sw;
        private long _lastReport;

        public DirectoryProgress(IProgress<CopyProgress>? target, long baseBytes, long total, Stopwatch sw)
        {
            _target = target;
            _base = baseBytes;
            _total = total;
            _sw = sw;
        }

        public void Report(CopyProgress value)
        {
            if (_target == null) return;

            // Отчёт по завершении файла пропускаем только если совсем недавно был другой.
            var finished = value.TotalBytes > 0 && value.BytesDone >= value.TotalBytes;
            var now = _sw.ElapsedMilliseconds;
            if (!finished && now - _lastReport < ReportIntervalMs) return;
            _lastReport = now;

            var done = _base + value.BytesDone;
            var speed = _sw.Elapsed.TotalSeconds > 0.3 ? done / _sw.Elapsed.TotalSeconds : 0;
            _target.Report(new CopyProgress(done, _total, speed));
        }
    }

    public static string FormatBytes(double bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{v:0} {units[u]}" : $"{v:0.##} {units[u]}";
    }
}
