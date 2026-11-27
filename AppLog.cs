using System.Text;

namespace HvBackup;

/// <summary>Журнал: файл + событие для UI.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string _file = string.Empty;
    private static bool _consoleMode;

    /// <summary>Строка журнала. Формат: "время | уровень | сообщение".</summary>
    public sealed record Entry(DateTime Time, string Level, string Message);

    public static event Action<Entry>? Logged;

    public static void Init(bool consoleMode)
    {
        _consoleMode = consoleMode;
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDir);
            _file = Path.Combine(SettingsStore.DataDir, $"log-{DateTime.Now:yyyy-MM}.txt");
            if (consoleMode)
            {
                ConsoleFile = Path.Combine(Path.GetTempPath(), "hvbackup-selftest.txt");
                try { File.WriteAllText(ConsoleFile, ""); } catch { }
            }
        }
        catch
        {
            _file = string.Empty;
        }
    }

    /// <summary>Файл-дублёр консольного вывода (для служебных режимов).</summary>
    public static string? ConsoleFile { get; private set; }

    /// <summary>Вывод в консоль и, при необходимости, в дублирующий файл.</summary>
    public static void Out(string text = "")
    {
        Console.WriteLine(text);
        try
        {
            if (!string.IsNullOrEmpty(ConsoleFile))
                File.AppendAllText(ConsoleFile!, text + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // игнорируем ошибки вывода
        }
    }

    /// <summary>
    /// Удаляет файлы журнала старше срока хранения (в днях). 0 — хранить всё.
    /// Возвращает количество удалённых файлов.
    /// </summary>
    public static int CleanupOldLogs(int days, string? directory = null)
    {
        if (days <= 0) return 0;

        var dir = directory ?? SettingsStore.DataDir;
        var cutoff = DateTime.Now.AddDays(-days);
        var removed = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var file in Directory.EnumerateFiles(dir, "log-*.txt"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch
                {
                    // занятый файл пропускаем
                }
            }
        }
        catch
        {
            // очистка журнала не должна мешать работе программы
        }
        return removed;
    }

    public static void Info(string message) => Write("ИНФО", message);
    public static void Warn(string message) => Write("ВНИМ ", message);
    public static void Error(string message) => Write("ОШИБ", message);

    public static void Write(string level, string message)
    {
        var entry = new Entry(DateTime.Now, level, message);
        try
        {
            lock (Gate)
            {
                if (_consoleMode)
                {
                    Console.WriteLine($"{entry.Time:HH:mm:ss} | {level} | {message}");
                }
                else if (!string.IsNullOrEmpty(_file))
                {
                    File.AppendAllText(_file, $"{entry.Time:yyyy-MM-dd HH:mm:ss} | {level} | {message}{Environment.NewLine}", Encoding.UTF8);
                }
            }
        }
        catch
        {
            // журнал не должен ронять операцию
        }
        Logged?.Invoke(entry);
    }
}
