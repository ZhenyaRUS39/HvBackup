namespace HvBackup;

/// <summary>Режим копирования.</summary>
public enum CopyMode
{
    /// <summary>Контрольная точка Hyper-V + собственное копирование с ограничением скорости. ВМ продолжает работать.</summary>
    CheckpointThrottled = 0,

    /// <summary>Экспорт ВМ средствами Hyper-V (быстро, без ограничения скорости, на выходе готовый к импорту ВМ).</summary>
    VmExport = 1,

    /// <summary>
    /// Горячий экспорт Hyper-V: работающая ВМ выгружается целиком (конфигурация + диски),
    /// затем копия переносится в папку копий с ограничением скорости. Восстановление — Import-VM.
    /// </summary>
    HotExport = 2
}

/// <summary>Периодичность автоматических копий.</summary>
public enum ScheduleKind
{
    Disabled = 0,
    EveryMinutes = 1,
    Daily = 2,
    Weekly = 3
}

/// <summary>Настройки приложения (сохраняются в %ProgramData%\HvBackup\settings.json).</summary>
public sealed class AppSettings
{
    /// <summary>Корневая папка для копий.</summary>
    public string BackupRoot { get; set; } = @"D:\Backups\Hyper-V";

    /// <summary>Сколько копий хранить на каждую ВМ (лишние самые старые удаляются).</summary>
    public int KeepCopies { get; set; } = 3;

    /// <summary>Ограничение скорости копирования, МБ/с. 0 — без ограничения.</summary>
    public int SpeedLimitMBps { get; set; } = 100;

    public CopyMode Mode { get; set; } = CopyMode.HotExport;

    /// <summary>
    /// Папка для временного экспорта Hyper-V (режим «горячий экспорт»).
    /// Пусто — используется системная временная папка. Нужно свободное место ≈ объёму ВМ.
    /// </summary>
    public string ExportTempRoot { get; set; } = "";

    public ScheduleKind Schedule { get; set; } = ScheduleKind.Daily;

    /// <summary>Интервал для ScheduleKind.EveryMinutes.</summary>
    public int EveryMinutes { get; set; } = 60;

    public int Hour { get; set; } = 3;
    public int Minute { get; set; } = 0;

    /// <summary>День недели для ScheduleKind.Weekly (0 = воскресенье).</summary>
    public int WeeklyDay { get; set; } = 0;

    /// <summary>Делать копию сразу при запуске программы.</summary>
    public bool RunOnStart { get; set; }

    /// <summary>
    /// Временно выставлять ВМ тип контрольной точки Production (VSS, без копии памяти).
    /// Исходное значение возвращается после копии.
    /// </summary>
    public bool UseProductionCheckpoint { get; set; } = true;

    /// <summary>Сворачивать окно в трей вместо закрытия.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Запускать программу автоматически при входе в Windows.</summary>
    public bool AutoStart { get; set; }

    // ───────────────────────────── MCP-сервер ─────────────────────────────

    /// <summary>
    /// Встроенный MCP-сервер включён. Сервер работает внутри программы
    /// (с правами администратора) и слушает только 127.0.0.1.
    /// </summary>
    public bool McpEnabled { get; set; }

    /// <summary>Порт встроенного MCP-сервера.</summary>
    public int McpPort { get; set; } = 8787;

    // ─────────────────────────── Сетевая папка (UNC) ───────────────────────────

    public bool NetworkEnabled { get; set; }
    public string NetworkPath { get; set; } = @"\\server\share\Hyper-V";
    public string NetworkUser { get; set; } = "";

    /// <summary>Пароль в зашифрованном виде (DPAPI, машинный уровень).</summary>
    public string NetworkPasswordProtected { get; set; } = "";

    // ─────────────────────────────── FTP / FTPS ───────────────────────────────

    public bool FtpEnabled { get; set; }
    public string FtpHost { get; set; } = "";
    public int FtpPort { get; set; } = 21;
    public string FtpUser { get; set; } = "anonymous";
    public string FtpRemoteDir { get; set; } = "/backup";

    /// <summary>Пароль в зашифрованном виде (DPAPI, машинный уровень).</summary>
    public string FtpPasswordProtected { get; set; } = "";

    /// <summary>Пассивный режим (true) или активный (false).</summary>
    public bool FtpPassive { get; set; } = true;

    /// <summary>0 — без шифрования, 1 — явный FTPS (AUTH TLS), 2 — неявный FTPS (обычно порт 990).</summary>
    public int FtpTlsMode { get; set; }

    /// <summary>Не проверять TLS-сертификат сервера.</summary>
    public bool FtpSkipCertificateValidation { get; set; }

    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Хранить локальную копию после успешной отправки в сеть/FTP.</summary>
    public bool KeepLocalCopy { get; set; } = true;

    /// <summary>Срок хранения файлов журнала в днях. 0 — хранить всё.</summary>
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>ВМ, включённые в автоматическое копирование.</summary>
    public List<string> SelectedVms { get; set; } = new();

    /// <summary>Отдельные настройки конкретных ВМ (ключ — имя виртуальной машины).</summary>
    public Dictionary<string, VmSettings> PerVm { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime? LastRunUtc { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>
    /// Переносит значения из другого набора настроек в этот.
    /// <para>
    /// Нужно, когда файл настроек изменили извне (например, через MCP-сервер):
    /// программа читает файл заново и обновляет свои значения по месту —
    /// так работает и планировщик, он смотрит в тот же объект настроек.
    /// </para>
    /// </summary>
    public void CopyFrom(AppSettings other)
    {
        if (other == null) return;

        BackupRoot = other.BackupRoot;
        KeepCopies = other.KeepCopies;
        SpeedLimitMBps = other.SpeedLimitMBps;
        Mode = other.Mode;
        ExportTempRoot = other.ExportTempRoot;
        Schedule = other.Schedule;
        EveryMinutes = other.EveryMinutes;
        Hour = other.Hour;
        Minute = other.Minute;
        WeeklyDay = other.WeeklyDay;
        RunOnStart = other.RunOnStart;
        UseProductionCheckpoint = other.UseProductionCheckpoint;
        MinimizeToTray = other.MinimizeToTray;
        AutoStart = other.AutoStart;

        McpEnabled = other.McpEnabled;
        McpPort = other.McpPort;

        NetworkEnabled = other.NetworkEnabled;
        NetworkPath = other.NetworkPath;
        NetworkUser = other.NetworkUser;
        NetworkPasswordProtected = other.NetworkPasswordProtected;

        FtpEnabled = other.FtpEnabled;
        FtpHost = other.FtpHost;
        FtpPort = other.FtpPort;
        FtpUser = other.FtpUser;
        FtpRemoteDir = other.FtpRemoteDir;
        FtpPasswordProtected = other.FtpPasswordProtected;
        FtpPassive = other.FtpPassive;
        FtpTlsMode = other.FtpTlsMode;
        FtpSkipCertificateValidation = other.FtpSkipCertificateValidation;

        KeepLocalCopy = other.KeepLocalCopy;
        LogRetentionDays = other.LogRetentionDays;
        LastRunUtc = other.LastRunUtc;

        SelectedVms = new List<string>(other.SelectedVms ?? new List<string>());

        var perVm = new Dictionary<string, VmSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in other.PerVm ?? new Dictionary<string, VmSettings>())
            perVm[pair.Key] = pair.Value?.Clone() ?? new VmSettings();
        PerVm = perVm;
    }

    /// <summary>Пароль сетевой папки (расшифрованный, для работы).</summary>
    public string NetworkPassword => Dpapi.Unprotect(NetworkPasswordProtected);

    /// <summary>Пароль FTP (расшифрованный, для работы).</summary>
    public string FtpPassword => Dpapi.Unprotect(FtpPasswordProtected);

    /// <summary>Индивидуальные настройки ВМ (null — общие настройки).</summary>
    public VmSettings? PerVmSettings(string vmName)
    {
        if (string.IsNullOrWhiteSpace(vmName)) return null;
        if (PerVm == null) return null;
        return PerVm.TryGetValue(vmName, out var v) ? v : null;
    }

    /// <summary>
    /// Эффективные настройки для конкретной ВМ: общие настройки, перекрытые индивидуальными.
    /// Если индивидуальных настроек нет — возвращаются общие без изменений.
    /// </summary>
    public AppSettings ForVm(string vmName)
    {
        var v = PerVmSettings(vmName);
        if (v == null) return this;

        var s = Clone();
        if (v.SpeedLimitMBps.HasValue) s.SpeedLimitMBps = v.SpeedLimitMBps.Value;
        if (v.KeepCopies.HasValue) s.KeepCopies = v.KeepCopies.Value;
        if (v.Mode.HasValue) s.Mode = v.Mode.Value;
        if (v.UseProductionCheckpoint.HasValue) s.UseProductionCheckpoint = v.UseProductionCheckpoint.Value;
        if (v.KeepLocalCopy.HasValue) s.KeepLocalCopy = v.KeepLocalCopy.Value;
        if (!string.IsNullOrWhiteSpace(v.BackupRoot)) s.BackupRoot = v.BackupRoot.Trim();
        if (!string.IsNullOrWhiteSpace(v.NetworkPath)) s.NetworkPath = v.NetworkPath.Trim();
        if (!string.IsNullOrWhiteSpace(v.FtpRemoteDir)) s.FtpRemoteDir = v.FtpRemoteDir.Trim();

        if (v.UseOwnSchedule)
        {
            s.Schedule = v.Schedule;
            s.EveryMinutes = v.EveryMinutes;
            s.Hour = v.Hour;
            s.Minute = v.Minute;
            s.WeeklyDay = v.WeeklyDay;
        }
        return s;
    }

    /// <summary>Записывает индивидуальные настройки ВМ (null — удалить их).</summary>
    public void SetPerVm(string vmName, VmSettings? value)
    {
        if (PerVm == null) PerVm = new Dictionary<string, VmSettings>(StringComparer.OrdinalIgnoreCase);
        if (value == null) PerVm.Remove(vmName);
        else PerVm[vmName] = value;
    }
}

/// <summary>
/// Индивидуальные настройки одной виртуальной машины.
/// Перекрывают общие настройки; пустые значения означают «как в общих».
/// </summary>
public sealed class VmSettings
{
    /// <summary>Использовать свой график копирования вместо общего.</summary>
    public bool UseOwnSchedule { get; set; }

    public ScheduleKind Schedule { get; set; } = ScheduleKind.Daily;
    public int EveryMinutes { get; set; } = 60;
    public int Hour { get; set; } = 3;
    public int Minute { get; set; }
    public int WeeklyDay { get; set; }

    /// <summary>Ограничение скорости, МБ/с. null — общий лимит.</summary>
    public int? SpeedLimitMBps { get; set; }

    /// <summary>Сколько копий хранить. null — общий счётчик.</summary>
    public int? KeepCopies { get; set; }

    /// <summary>Режим копирования. null — общий.</summary>
    public CopyMode? Mode { get; set; }

    /// <summary>Своя папка для копий этой ВМ. Пусто — общая папка.</summary>
    public string BackupRoot { get; set; } = "";

    /// <summary>Production-контрольная точка. null — как в общих настройках.</summary>
    public bool? UseProductionCheckpoint { get; set; }

    /// <summary>Хранить локальную копию после отправки в сеть/FTP. null — как в общих.</summary>
    public bool? KeepLocalCopy { get; set; }

    /// <summary>Своя сетевая папка для этой ВМ. Пусто — общая.</summary>
    public string NetworkPath { get; set; } = "";

    /// <summary>Свой каталог FTP для этой ВМ. Пусто — общий.</summary>
    public string FtpRemoteDir { get; set; } = "";

    /// <summary>Время последнего запуска по расписанию (якорь для «каждые N минут»).</summary>
    public DateTime? LastRunUtc { get; set; }

    public VmSettings Clone() => (VmSettings)MemberwiseClone();
}
