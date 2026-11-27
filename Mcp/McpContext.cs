namespace HvBackup.Mcp;

/// <summary>
/// Контекст вызовов инструментов MCP.
/// <para>
/// Настройки читаются и записываются через файл настроек программы —
/// так изменения, сделанные через MCP, сразу видны в окне настроек
/// и в планировщике (окно программы подхватывает файл автоматически).
/// </para>
/// </summary>
public static class McpContext
{
    /// <summary>Создать контекст для вызовов инструментов.</summary>
    /// <param name="live">
    /// Объект настроек программы (в окне — тот же, что использует планировщик).
    /// Если не задан — настройки читаются из файла.
    /// </param>
    public static ToolContext Create(AppSettings? live = null)
    {
        var settings = live ?? SettingsStore.Load();

        return new ToolContext
        {
            Settings = settings,
            ReloadSettings = () => SettingsStore.Load(),
            SaveSettings = s =>
            {
                // live-объект обновляем сразу — окно программы увидит правки без перезапуска
                if (!ReferenceEquals(s, settings)) settings.CopyFrom(s);
                SettingsStore.Save(s);
            },
            Jobs = new JobRunner(),
            Log = message => AppLog.Info(message)
        };
    }
}
