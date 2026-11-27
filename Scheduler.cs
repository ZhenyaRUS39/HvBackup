namespace HvBackup;

/// <summary>
/// Планировщик периодических копий.
/// <para>
/// Расписание может быть общим для всех машин и/или собственным у отдельных ВМ
/// (настраивается шестерёнкой в списке машин). Планировщик сам вычисляет, какие
/// машины «должны» запуститься, и передаёт их список в событие <see cref="Due"/>.
/// </para>
/// <para>
/// Время следующего запуска по каждой ВМ считается один раз и хранится.
/// Проверка «пора ли» сравнивает текущее время с <b>сохранённым</b> временем запуска,
/// а не с новым расчётом: новый расчёт всегда возвращает будущий момент
/// (иначе он «прыгал» бы назад), поэтому сравнение с ним никогда не срабатывало бы
/// и копирование по расписанию не запускалось.
/// </para>
/// </summary>
public sealed class BackupScheduler : IDisposable
{
    private readonly AppSettings _settings;
    private System.Threading.Timer? _timer;

    /// <summary>Время следующего запуска по каждой ВМ (null — запусков не будет).</summary>
    private readonly Dictionary<string, DateTime?> _nextByVm = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Время последнего фактического запуска по каждой ВМ — якорь для «каждые N минут».</summary>
    private readonly Dictionary<string, DateTime> _lastRunByVm = new(StringComparer.OrdinalIgnoreCase);

    private bool _firing;

    /// <summary>Машины, которым пора копироваться.</summary>
    public event Action<IReadOnlyList<string>>? Due;

    public BackupScheduler(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Ближайший запуск по любой из машин (null — запусков не будет).</summary>
    public DateTime? NextRun
    {
        get
        {
            DateTime? next = null;
            foreach (var value in _nextByVm.Values)
            {
                if (value is not { } v) continue;
                if (next is null || v < next) next = v;
            }
            return next;
        }
    }

    /// <summary>Время следующего запуска по конкретной ВМ (null — у этой ВМ автобэкапа нет).</summary>
    public DateTime? NextRunFor(string vmName) =>
        _nextByVm.TryGetValue(vmName, out var value) ? value : null;

    public void Start()
    {
        Recalculate();
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => Tick(), null, 5000, 5000);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Пересчитать время следующих запусков (после изменения настроек или выполнения копии).</summary>
    public void Recalculate() => Recalculate(DateTime.Now);

    /// <summary>Пересчитать время запуска от указанного момента (для проверок и тестов).</summary>
    internal void Recalculate(DateTime now)
    {
        var names = _settings.SelectedVms.ToList();
        foreach (var stale in _nextByVm.Keys.Where(k => !names.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            _nextByVm.Remove(stale);

        foreach (var vm in names)
            _nextByVm[vm] = ComputeNextFor(now, vm);
    }

    private void Tick()
    {
        if (_firing) return;

        var due = CollectDue(DateTime.Now);
        if (due.Count == 0) return;

        _firing = true;
        try
        {
            foreach (var vm in due) MarkRun(vm);
            _settings.LastRunUtc = DateTime.UtcNow;
            SettingsStore.Save(_settings);
            Due?.Invoke(due);
        }
        finally
        {
            _firing = false;
            Recalculate();
        }
    }

    /// <summary>
    /// Машины, которым пора копироваться на момент <paramref name="now"/>.
    /// Сравнение идёт с сохранённым временем запуска — так условие срабатывает,
    /// когда подошёл час, и не срабатывает задним числом при каждом тике.
    /// </summary>
    internal List<string> CollectDue(DateTime now)
    {
        var due = new List<string>();

        foreach (var vm in _settings.SelectedVms.ToList())
        {
            if (!_nextByVm.TryGetValue(vm, out var stored))
            {
                // Время ещё не посчитано (например, машину только отметили галочкой).
                stored = ComputeNextFor(now, vm);
                _nextByVm[vm] = stored;
            }

            if (stored is not { } next) continue;   // расписание выключено
            if (now < next) continue;              // ещё рано
            due.Add(vm);
        }

        return due;
    }

    /// <summary>Отмечает, что ВМ только что копировалась — это якорь для «каждые N минут».</summary>
    private void MarkRun(string vmName)
    {
        var now = DateTime.UtcNow;
        _lastRunByVm[vmName] = now;

        // Якорь сохраняем и в настройках ВМ, чтобы «каждые N минут» не сбивалось
        // после перезапуска программы. Запись создаётся только если она уже есть:
        // пустая запись выглядела бы в диалоге как «включены отдельные настройки».
        var v = _settings.PerVmSettings(vmName);
        if (v != null) v.LastRunUtc = now;
    }

    /// <summary>Расчёт времени следующего запуска по общим настройкам.</summary>
    public DateTime? ComputeNext(DateTime now) => ComputeNext(now, _settings, null);

    /// <summary>Расчёт времени следующего запуска по конкретной ВМ (с учётом её настроек).</summary>
    public DateTime? ComputeNextFor(DateTime now, string vmName)
    {
        var eff = _settings.ForVm(vmName);
        return ComputeNext(now, eff, vmName);
    }

    private DateTime? ComputeNext(DateTime now, AppSettings s, string? vmName)
    {
        switch (s.Schedule)
        {
            case ScheduleKind.Disabled:
                return null;

            case ScheduleKind.EveryMinutes:
            {
                var anchor = LastRun(vmName) ?? now.AddMinutes(-s.EveryMinutes);
                var next = anchor.AddMinutes(s.EveryMinutes);
                if (next <= now) next = now.AddMinutes(s.EveryMinutes);
                return next;
            }

            case ScheduleKind.Daily:
            {
                var t = now.Date.AddHours(s.Hour).AddMinutes(s.Minute);
                if (t <= now) t = t.AddDays(1);
                return t;
            }

            case ScheduleKind.Weekly:
            {
                var t = now.Date.AddHours(s.Hour).AddMinutes(s.Minute);
                var delta = ((s.WeeklyDay - (int)t.DayOfWeek) + 7) % 7;
                t = t.AddDays(delta);
                if (t <= now) t = t.AddDays(7);
                return t;
            }

            default:
                return null;
        }
    }

    /// <summary>Время последнего запуска: своё у ВМ или общее.</summary>
    private DateTime? LastRun(string? vmName)
    {
        if (vmName != null)
        {
            if (_lastRunByVm.TryGetValue(vmName, out var mem)) return mem.ToLocalTime();
            var v = _settings.PerVmSettings(vmName);
            if (v?.LastRunUtc != null) return v.LastRunUtc.Value.ToLocalTime();
        }
        return _settings.LastRunUtc?.ToLocalTime();
    }

    public void Dispose() => _timer?.Dispose();
}
