namespace HvBackup.Ui;

/// <summary>
/// Тексты для карточки состояния копии: скорость, оставшееся время и объём.
/// Вынесены отдельно, чтобы их можно было проверить в самопроверке.
/// </summary>
internal static class ProgressText
{
    /// <summary>Скорость: «28,4 МБ/с» или «—», если измерять нечего.</summary>
    public static string Speed(BackupProgress p) =>
        p.Speed > 1 ? ThrottledCopier.FormatBytes(p.Speed) + "/с" : "—";

    /// <summary>Оставшееся время: «00:12:37». При оценочном объёме добавляется «≈».</summary>
    public static string Eta(BackupProgress p)
    {
        if (p.Speed <= 1 || p.Total <= p.Done) return "—";
        var seconds = (p.Total - p.Done) / p.Speed;
        if (seconds > TimeSpan.MaxValue.TotalSeconds) seconds = TimeSpan.MaxValue.TotalSeconds;
        return (p.Estimated ? "≈" : "") + TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");
    }

    /// <summary>Объём: «18,6 ГБ из ≈42 ГБ (44 %)».</summary>
    public static string Volume(BackupProgress p)
    {
        var pct = p.Total > 0 ? Math.Min(100.0, p.Done * 100.0 / p.Total) : 0;
        var approx = p.Estimated ? "≈" : "";
        return $"{ThrottledCopier.FormatBytes(p.Done)} из {approx}{ThrottledCopier.FormatBytes(p.Total)} ({pct:0}%)";
    }
}
