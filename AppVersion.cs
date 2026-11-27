namespace HvBackup;

/// <summary>
/// Версия программы — берётся из свойств сборки, чтобы не расходиться с csproj.
/// </summary>
internal static class AppVersion
{
    public static string Value
    {
        get
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var attrs = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            var info = (attrs.FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }
            return asm.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }
}
