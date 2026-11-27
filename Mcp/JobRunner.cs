using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace HvBackup.Mcp;

/// <summary>Фоновая операция сервера (копирование виртуальной машины).</summary>
public sealed class Job
{
    private readonly TaskCompletionSource<BackupResult> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];

    public string Kind { get; }

    public string VmName { get; }

    public DateTime Started { get; } = DateTime.Now;

    public string Status { get; private set; } = "running";

    public string Phase { get; private set; } = "Запуск";

    public long Done { get; private set; }

    public long Total { get; private set; }

    public double Speed { get; private set; }

    public string Error { get; private set; } = "";

    public BackupResult? Result { get; private set; }

    public Job(string kind, string vmName)
    {
        Kind = kind;
        VmName = vmName;
    }

    public string StatusText => Status switch
    {
        "running" => "выполняется",
        "done" => "завершено",
        "failed" => "ошибка",
        "cancelled" => "отменено",
        _ => Status
    };

    /// <summary>Ждать окончания операции (для вызова с wait=true).</summary>
    public async Task<BackupResult> WaitAsync(CancellationToken ct)
    {
        using var registration = ct.Register(() => _done.TrySetCanceled());
        return await _done.Task.ConfigureAwait(false);
    }

    public void ReportProgress(BackupProgress progress)
    {
        Phase = progress.Phase;
        Done = progress.Done;
        Total = progress.Total;
        Speed = progress.Speed;
    }

    public void Finish(BackupResult result)
    {
        Result = result;
        Error = result.Error;
        Status = result.Cancelled ? "cancelled" : result.Ok ? "done" : "failed";
        Phase = result.Ok ? "Готово" : result.Cancelled ? "Отменено" : "Ошибка";
        _done.TrySetResult(result);
    }

    public JsonObject ToJson() => new()
    {
        ["jobId"] = Id,
        ["kind"] = Kind,
        ["vmName"] = VmName,
        ["status"] = Status,
        ["statusText"] = StatusText,
        ["phase"] = Phase,
        ["doneBytes"] = Done,
        ["totalBytes"] = Total,
        ["speedBytesPerSecond"] = Math.Round(Speed),
        ["startedAt"] = Started.ToString("yyyy-MM-dd HH:mm:ss"),
        ["error"] = Error,
        ["folder"] = Result?.Folder ?? "",
        ["durationSeconds"] = Result == null ? 0 : Math.Round(Result.Duration.TotalSeconds)
    };
}

/// <summary>Хранилище фоновых операций.</summary>
public sealed class JobRunner
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    public IReadOnlyList<Job> All
    {
        get
        {
            lock (_order)
            {
                var list = new List<Job>();
                foreach (var id in _order)
                    if (_jobs.TryGetValue(id, out var job)) list.Add(job);
                list.Reverse();
                return list;
            }
        }
    }

    public int Count => _jobs.Count;

    public Job? Find(string id) => _jobs.TryGetValue(id ?? "", out var job) ? job : null;

    /// <summary>Запускает горячее копирование виртуальной машины в фоне.</summary>
    public Job StartBackup(string vmName, AppSettings settings)
    {
        var job = new Job("backup", vmName);
        _jobs[job.Id] = job;
        lock (_order)
        {
            _order.Add(job.Id);
            // history не растёт бесконечно
            while (_order.Count > 50) _jobs.TryRemove(_order[0], out _);
            if (_order.Count > 50) _order.RemoveAt(0);
        }

        var engine = new BackupEngine(
            settings,
            message => AppLog.Info(message),
            progress => job.ReportProgress(progress));

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await engine.RunAsync(vmName, CancellationToken.None).ConfigureAwait(false);
                job.Finish(result);
            }
            catch (Exception ex)
            {
                AppLog.Error("Ошибка фонового копирования: " + ex.Message);
                job.Finish(new BackupResult { Ok = false, Error = ex.Message });
            }
        });

        return job;
    }
}

/// <summary>Версия MCP-сервера (берётся из свойств сборки).</summary>
public static class McpVersion
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
