using System.Runtime.InteropServices;

namespace HvBackup;

/// <summary>Работа с сетевыми папками Windows (UNC).</summary>
public static class NetworkShare
{
    private const int RESOURCE_TYPE_DISK = 1;
    private const int RESOURCE_CONNECT = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpLocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpRemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpComment;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetAddConnection2(ref NETRESOURCE lpNetResource, string? lpPassword, string? lpUserName, int dwFlags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetCancelConnection2(string lpName, int dwFlags, bool fForce);

    /// <summary>Подключает сетевую папку (при необходимости) под указанной учётной записью.</summary>
    public static (bool Ok, string Error) Connect(string uncPath, string user, string password)
    {
        try
        {
            var root = UncRoot(uncPath);
            if (string.IsNullOrEmpty(root)) return (false, "Некорректный сетевой путь: " + uncPath);

            var res = new NETRESOURCE
            {
                dwType = RESOURCE_TYPE_DISK,
                lpRemoteName = root,
                lpLocalName = "",
                lpProvider = ""
            };

            var code = WNetAddConnection2(ref res,
                string.IsNullOrEmpty(password) ? null : password,
                string.IsNullOrEmpty(user) ? null : user,
                RESOURCE_CONNECT);

            if (code == 0 || code == 85) return (true, "");
            return (false, DescribeError(code, root));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static void Disconnect(string uncPath)
    {
        try
        {
            var root = UncRoot(uncPath);
            if (!string.IsNullOrEmpty(root)) WNetCancelConnection2(root, RESOURCE_CONNECT, true);
        }
        catch { /* не критично */ }
    }

    /// <summary>Копирует содержимое каталога в сетевую папку с ограничением скорости.</summary>
    public static async Task<long> MirrorAsync(string sourceDir, string destinationDir, long limitBytesPerSec,
        IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destinationDir);
        return await ThrottledCopier.CopyDirectoryAsync(sourceDir, destinationDir, limitBytesPerSec, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Удаляет старые копии в сетевой папке (та же логика, что и локально).</summary>
    public static int Rotate(string remoteVmFolder, int keep)
    {
        if (!Directory.Exists(remoteVmFolder)) return 0;
        keep = Math.Max(1, keep);

        var dirs = Directory.GetDirectories(remoteVmFolder)
            .Select(d => new { Path = d, Name = Path.GetFileName(d) })
            .Where(x => x.Name.Length >= 19 &&
                        DateTime.TryParseExact(x.Name[..19], "yyyy-MM-dd_HH-mm-ss", null,
                            System.Globalization.DateTimeStyles.None, out _))
            .OrderByDescending(x => x.Name, StringComparer.Ordinal)
            .ToList();

        var removed = 0;
        foreach (var d in dirs.Skip(keep))
        {
            try { Directory.Delete(d.Path, true); removed++; }
            catch (Exception ex) { AppLog.Warn($"Не удалось удалить старую сетевую копию {d.Name}: {ex.Message}"); }
        }
        return removed;
    }

    /// <summary>Возвращает корень UNC-пути: \\server\share</summary>
    public static string UncRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var normalized = path.Trim().Replace('/', '\\');
        if (!normalized.StartsWith("\\\\", StringComparison.Ordinal)) return "";

        var parts = normalized.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "";
        return "\\\\" + parts[0] + "\\" + parts[1];
    }

    public static string DescribeError(int code, string path)
    {
        return code switch
        {
            53 => $"Сетевой путь не найден: {path}",
            67 => $"Не найдено сетевое имя: {path}",
            85 => $"Подключение уже установлено: {path}",
            1219 => $"Неверные учётные данные или нет доступа к {path}",
            1326 => $"Неверный логин или пароль для {path}",
            86 => $"Неверный пароль для {path}",
            2 => $"Папка не найдена: {path}",
            3 => $"Не удалось найти указанный путь: {path}",
            5 => "Отказано в доступе",
            _ => $"Ошибка подключения к сетевой папке (код {code})"
        };
    }
}
