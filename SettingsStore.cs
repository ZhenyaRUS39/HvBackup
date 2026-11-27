using System.Text.Json;

namespace HvBackup;

/// <summary>
/// Хранилище настроек в общесистемной папке %ProgramData%\HvBackup\settings.json.
/// Общесистемная папка нужна потому, что задача Планировщика запускает программу от SYSTEM:
/// пользовательские %APPDATA% у SYSTEM другие, и настройки были бы не видны.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HvBackup");

    public static string SettingsPath { get; } = Path.Combine(DataDir, "settings.json");

    private static string LegacySettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HvBackup", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            MigrateLegacy();
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (s != null) return Normalize(ApplyModeMigration(json, s));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать настройки: " + ex.Message);
        }
        return Normalize(new AppSettings());
    }

    /// <summary>
    /// Переход на «горячий экспорт» для настроек, сохранённых до версии 1.8:
    /// там режима экспорта с ограничением скорости ещё не было, а копирование
    /// по умолчанию шло через контрольную точку.
    /// </summary>
    internal static AppSettings ApplyModeMigration(string json, AppSettings s)
    {
        try
        {
            // В файле до 1.8 нет поля ExportTempRoot — значит, настройки старые.
            var isOld = !json.Contains("ExportTempRoot", StringComparison.OrdinalIgnoreCase);
            if (isOld && s.Mode == CopyMode.CheckpointThrottled)
            {
                s.Mode = CopyMode.HotExport;
                AppLog.Info("Режим копирования переключён на «горячий экспорт Hyper-V» (настройки обновлены с версии 1.7).");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось применить переход режима копирования: " + ex.Message);
        }
        return s;
    }

    /// <summary>Блокировка записи: настройки пишутся и из окна, и из потока планировщика.</summary>
    private static readonly object SaveLock = new();

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);

            lock (SaveLock)
            {
                // уникальное имя временного файла: два процесса не мешают друг другу
                var tmp = SettingsPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                try
                {
                    File.WriteAllText(tmp, json);
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            File.Move(tmp, SettingsPath, overwrite: true);
                            break;
                        }
                        catch (IOException) when (attempt < 4)
                        {
                            // файл настроек мог быть занят другой программой — пробуем ещё раз
                            Thread.Sleep(80);
                            if (!File.Exists(tmp)) break;
                        }
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                    catch
                    {
                        // временный файл не удалён — не критично
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось сохранить настройки: " + ex.Message);
        }
    }

    /// <summary>Переносит настройки из прежней пользовательской папки, если они там есть.</summary>
    private static void MigrateLegacy()
    {
        try
        {
            if (File.Exists(SettingsPath)) return;
            if (!File.Exists(LegacySettingsPath)) return;
            Directory.CreateDirectory(DataDir);
            File.Copy(LegacySettingsPath, SettingsPath, overwrite: true);
            AppLog.Info("Настройки перенесены из " + LegacySettingsPath);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось перенести настройки: " + ex.Message);
        }
    }

    private static AppSettings Normalize(AppSettings s)
    {
        s.KeepCopies = Math.Clamp(s.KeepCopies, 1, 999);
        s.SpeedLimitMBps = Math.Clamp(s.SpeedLimitMBps, 0, 100000);
        s.EveryMinutes = Math.Clamp(s.EveryMinutes, 1, 60 * 24 * 30);
        s.Hour = Math.Clamp(s.Hour, 0, 23);
        s.Minute = Math.Clamp(s.Minute, 0, 59);
        s.WeeklyDay = Math.Clamp(s.WeeklyDay, 0, 6);
        s.LogRetentionDays = Math.Clamp(s.LogRetentionDays, 0, 3650);
        if (string.IsNullOrWhiteSpace(s.BackupRoot)) s.BackupRoot = @"D:\Backups\Hyper-V";
        s.SelectedVms ??= new List<string>();

        s.FtpPort = Math.Clamp(s.FtpPort, 1, 65535);
        s.FtpTlsMode = Math.Clamp(s.FtpTlsMode, 0, 2);
        s.McpPort = Math.Clamp(s.McpPort, 1024, 65535);
        if (s.FtpEnabled && string.IsNullOrWhiteSpace(s.FtpHost)) s.FtpEnabled = false;
        if (s.NetworkEnabled && string.IsNullOrWhiteSpace(s.NetworkPath)) s.NetworkEnabled = false;

        // Индивидуальные настройки ВМ: сравнение без учёта регистра и приведение к допустимым значениям.
        var perVm = new Dictionary<string, VmSettings>(StringComparer.OrdinalIgnoreCase);
        if (s.PerVm != null)
        {
            foreach (var pair in s.PerVm)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null) continue;
                var v = pair.Value;
                v.EveryMinutes = Math.Clamp(v.EveryMinutes, 1, 60 * 24 * 30);
                v.Hour = Math.Clamp(v.Hour, 0, 23);
                v.Minute = Math.Clamp(v.Minute, 0, 59);
                v.WeeklyDay = Math.Clamp(v.WeeklyDay, 0, 6);
                if (v.KeepCopies.HasValue) v.KeepCopies = Math.Clamp(v.KeepCopies.Value, 1, 999);
                if (v.SpeedLimitMBps.HasValue) v.SpeedLimitMBps = Math.Clamp(v.SpeedLimitMBps.Value, 0, 100000);
                perVm[pair.Key] = v;
            }
        }
        s.PerVm = perVm;
        return s;
    }
}
