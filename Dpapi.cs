using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace HvBackup;

/// <summary>
/// Шифрование паролей через DPAPI (машинный уровень, CRYPTPROTECT_LOCAL_MACHINE),
/// чтобы пароль не лежал в настройках открытым текстом и был доступен
/// и пользователю, и системной службе Планировщика задач.
/// </summary>
public static class Dpapi
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private const int CRYPTPROTECT_LOCAL_MACHINE = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDescription,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr pzpszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Шифрует строку, возвращает Base64. При ошибке — пустая строка.</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var blobIn = new DATA_BLOB { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
            Marshal.Copy(bytes, 0, blobIn.pbData, bytes.Length);
            try
            {
                if (!CryptProtectData(ref blobIn, "HvBackup", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE, out var blobOut))
                    return "";
                try
                {
                    var result = new byte[blobOut.cbData];
                    Marshal.Copy(blobOut.pbData, result, 0, blobOut.cbData);
                    return Convert.ToBase64String(result);
                }
                finally { LocalFree(blobOut.pbData); }
            }
            finally { Marshal.FreeHGlobal(blobIn.pbData); }
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Расшифровывает строку, полученную из Protect. При ошибке — пустая строка.</summary>
    public static string Unprotect(string encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return "";
        try
        {
            var data = Convert.FromBase64String(encoded);
            var blobIn = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
            Marshal.Copy(data, 0, blobIn.pbData, data.Length);
            try
            {
                if (!CryptUnprotectData(ref blobIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE, out var blobOut))
                    return "";
                try
                {
                    var result = new byte[blobOut.cbData];
                    Marshal.Copy(blobOut.pbData, result, 0, blobOut.cbData);
                    return Encoding.UTF8.GetString(result);
                }
                finally { LocalFree(blobOut.pbData); }
            }
            finally { Marshal.FreeHGlobal(blobIn.pbData); }
        }
        catch
        {
            return "";
        }
    }
}
