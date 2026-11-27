namespace HvBackup;

/// <summary>
/// Текстовое описание цикла резервного копирования по расписанию.
/// Используется в строке статуса, в меню трея и в зелёном бейдже рядом с виртуальной машиной.
/// </summary>
public static class ScheduleText
{
    private static readonly string[] DaysShort = { "вс", "пн", "вт", "ср", "чт", "пт", "сб" };
    private static readonly string[] DaysFull =
    {
        "воскресенье", "понедельник", "вторник", "среда", "четверг", "пятница", "суббота"
    };
    private static readonly string[] DaysIn =
    {
        "воскресенье", "понедельник", "вторник", "среду", "четверг", "пятницу", "субботу"
    };

    /// <summary>Активно ли расписание.</summary>
    public static bool IsActive(AppSettings settings) => settings.Schedule != ScheduleKind.Disabled;

    /// <summary>Полное описание цикла: «каждые 60 мин», «ежедневно в 03:00», «еженедельно в среду в 04:30».</summary>
    public static string Describe(AppSettings settings) =>
        Describe(settings.Schedule, settings.EveryMinutes, settings.Hour, settings.Minute, settings.WeeklyDay);

    public static string Describe(ScheduleKind kind, int everyMinutes, int hour, int minute, int weeklyDay) => kind switch
    {
        ScheduleKind.EveryMinutes => $"каждые {everyMinutes} мин",
        ScheduleKind.Daily => $"ежедневно в {hour:00}:{minute:00}",
        ScheduleKind.Weekly => $"еженедельно в {DayIn(weeklyDay)} в {hour:00}:{minute:00}",
        _ => "выключено"
    };

    /// <summary>Короткая надпись для зелёного бейджа в списке виртуальных машин.</summary>
    public static string Badge(AppSettings settings) => settings.Schedule switch
    {
        ScheduleKind.EveryMinutes => $"{settings.EveryMinutes} мин",
        ScheduleKind.Daily => $"ежедн. {settings.Hour:00}:{settings.Minute:00}",
        ScheduleKind.Weekly => $"{DayShort(settings.WeeklyDay)} {settings.Hour:00}:{settings.Minute:00}",
        _ => ""
    };

    public static string DayShort(int day) => DaysShort[Math.Clamp(day, 0, 6)];

    public static string DayFull(int day) => DaysFull[Math.Clamp(day, 0, 6)];

    /// <summary>День недели в форме «в среду» (для текста описания).</summary>
    public static string DayIn(int day) => DaysIn[Math.Clamp(day, 0, 6)];
}
