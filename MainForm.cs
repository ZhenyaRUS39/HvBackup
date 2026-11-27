using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using HvBackup.Mcp;
using HvBackup.Ui;

namespace HvBackup;

public sealed class MainForm : Form
{
    /// <summary>Колонка со значком-шестерёнкой (индивидуальные настройки ВМ).</summary>
    private const int ColGear = 1;

    /// <summary>Колонка со значком восстановления (копии этой машины).</summary>
    private const int ColRestore = 2;

    /// <summary>Колонка с цикличностью копирования.</summary>
    private const int ColSchedule = 3;

    private readonly AppSettings _settings;
    private readonly BackupScheduler _scheduler;

    /// <summary>Встроенный MCP-сервер: работает внутри программы, с правами администратора.</summary>
    private readonly McpHost _mcp;

    private IconListView _lvVms = null!;
    private Bitmap _imgRestore = null!;
    private RoundedProgressBar _pb = null!;
    private Label _lblPhase = null!;
    private Label _lblSpeed = null!;
    private Label _lblEta = null!;
    private Label _lblVolume = null!;
    private TextBox _txtLog = null!;
    private RoundedButton _btnBackup = null!;
    private RoundedButton _btnSchedule = null!;
    private RoundedButton _btnStop = null!;
    private RoundedButton _btnRefresh = null!;
    private RoundedButton _btnRestore = null!;
    private ImageList _imgBadges = null!;
    private Label _lblStatus = null!;
    private NotifyIcon _tray = null!;
    private ToolStripMenuItem _traySchedule = null!;
    private ToolStripMenuItem _trayMcp = null!;

    private Panel _header = null!;
    private FlowLayoutPanel _headerTools = null!;
    private Label _lblSubtitle = null!;

    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _reallyExit;

    /// <summary>Список перестраивается — события списка игнорируем.</summary>
    private bool _suppressListEvents;

    /// <summary>Обновление значков уже запланировано.</summary>
    private bool _badgeUpdateQueued;

    /// <summary>Ворота обновлений прогресса — отбрасывают поздние сообщения завершённого запуска.</summary>
    private readonly ProgressGate _progressGate = new();

    public MainForm(AppSettings settings)
    {
        _settings = settings;
        _mcp = new McpHost(_settings);
        _scheduler = new BackupScheduler(_settings);
        _scheduler.Due += vms => BeginInvoke(new Action(() => StartBackupFor(vms)));

        Theme.Init();
        BuildUi();
        AppLog.Logged += OnLog;
        _scheduler.Start();
        UpdateStatus();
        StartSettingsWatcher();
        ApplyMcpSettings();
    }

    /// <summary>Приводит встроенный MCP-сервер в соответствие с настройками.</summary>
    private void ApplyMcpSettings()
    {
        try
        {
            _mcp.ApplySettings(_settings);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось применить настройки MCP-сервера: " + ex.Message);
        }
    }

    // ─────────────────────── Настройки, изменённые извне ───────────────────────

    /// <summary>
    /// Следит за файлом настроек: если его изменили из другого процесса
    /// (например, через MCP-сервер), программа подхватывает изменения сама.
    /// </summary>
    private void StartSettingsWatcher()
    {
        try
        {
            var dir = SettingsStore.DataDir;
            if (!Directory.Exists(dir)) return;

            _settingsReloadTimer = new System.Windows.Forms.Timer { Interval = 700 };
            _settingsReloadTimer.Tick += (_, _) =>
            {
                _settingsReloadTimer.Stop();
                ReloadSettingsFromDisk();
            };

            _settingsWatcher = new FileSystemWatcher(dir, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _settingsWatcher.Changed += (_, _) => QueueSettingsReload();
            _settingsWatcher.Created += (_, _) => QueueSettingsReload();
            _settingsWatcher.Renamed += (_, _) => QueueSettingsReload();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось следить за файлом настроек: " + ex.Message);
        }
    }

    private FileSystemWatcher? _settingsWatcher;
    private System.Windows.Forms.Timer? _settingsReloadTimer;

    /// <summary>Откладывает перечитывание настроек (файл мог быть перезаписан не полностью).</summary>
    private void QueueSettingsReload()
    {
        try
        {
            if (_settingsReloadTimer == null || _settingsReloadTimer.Enabled) return;
            _settingsReloadTimer.Start();
        }
        catch
        {
            // перечитывание настроек не должно ломать программу
        }
    }

    /// <summary>Перечитывает настройки с диска и применяет их по месту.</summary>
    private void ReloadSettingsFromDisk()
    {
        try
        {
            var loaded = SettingsStore.Load();
            _settings.CopyFrom(loaded);
            _scheduler.Recalculate();
            ApplyMcpSettings();
            UpdateScheduleBadges();
            UpdateStatus();
            SetStatus("Настройки обновлены извне.");
            AppLog.Info("Настройки перечитаны с диска (изменены другим процессом).");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось перечитать настройки: " + ex.Message);
        }
    }

    // ─────────────────────────────── Интерфейс ───────────────────────────────
    private void BuildUi()
    {
        Text = "ГиперВ Резерв";
        MinimumSize = new Size(1020, 660);
        Size = new Size(1320, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Window;
        Font = Theme.Body;
        DoubleBuffered = true;

        Controls.Add(BuildContent());
        Controls.Add(BuildCommandBar());
        Controls.Add(BuildHeader());

        _tray = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "ГиперВ Резерв",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("Показать окно", null, (_, _) => RestoreFromTray());
        _tray.ContextMenuStrip.Items.Add("Сделать копию (выделенной машины)", null, (_, _) => StartBackup(scheduled: false));
        _tray.ContextMenuStrip.Items.Add("Восстановление из иных источников…", null, (_, _) => ShowRestore());
        _traySchedule = new ToolStripMenuItem("Расписание: —");
        _traySchedule.Click += (_, _) => ShowSchedule();
        _tray.ContextMenuStrip.Items.Add(_traySchedule);
        _trayMcp = new ToolStripMenuItem("MCP-сервер: выключен");
        _trayMcp.Click += (_, _) => OpenMcpSwagger();
        _tray.ContextMenuStrip.Items.Add(_trayMcp);
        _tray.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Выход", null, (_, _) => { _reallyExit = true; Close(); });
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray) HideToTray();
            AdjustVmColumns();
            AdjustHeader();
        };
        FormClosing += OnFormClosing;
        AdjustHeader();
    }

    private Panel BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Theme.Window, Padding = new Padding(20, 14, 20, 6) };

        var title = new Label
        {
            Text = "ГиперВ Резерв",
            Font = Theme.Title,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(20, 14)
        };
        var subtitle = new Label
        {
            Text = "Горячее резервное копирование ВМ Hyper-V — без остановки машины",
            Font = Theme.Subtitle,
            ForeColor = Theme.TextSecondary,
            AutoSize = false,
            Height = 20,
            AutoEllipsis = true,
            Location = new Point(22, 44)
        };
        _lblSubtitle = subtitle;

        _btnRefresh = new RoundedButton { Text = "Обновить", Glyph = "refresh", Size = new Size(120, 34) };
        _btnRefresh.Click += (_, _) => _ = RefreshVmsAsync();

        var btnSettings = new RoundedButton { Text = "Настройки…", Glyph = "settings", Size = new Size(126, 34) };
        btnSettings.Click += (_, _) => ShowSettings();

        var btnFolder = new RoundedButton { Text = "Папка копий", Glyph = "folder", Size = new Size(132, 34) };
        btnFolder.Click += (_, _) => OpenBackupFolder();

        var btnLog = new RoundedButton { Text = "Журнал", Glyph = "log", Size = new Size(112, 34) };
        btnLog.Click += (_, _) => OpenLog();

        // Общий путь восстановления: копии не из карточки выбранной машины,
        // а из любых источников — своей папки, сетевой папки, архива.
        _btnRestore = new RoundedButton { Text = "Восстановление из иных источников…", Glyph = "restore", Size = new Size(272, 34) };
        _btnRestore.Click += (_, _) => ShowRestore();

        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Width = 800,
            Height = 40,
            BackColor = Theme.Window,
            Padding = new Padding(0, 4, 0, 0)
        };
        tools.Controls.Add(_btnRestore);
        tools.Controls.Add(btnLog);
        tools.Controls.Add(btnFolder);
        tools.Controls.Add(btnSettings);
        tools.Controls.Add(_btnRefresh);
        _headerTools = tools;

        header.Controls.Add(tools);
        header.Controls.Add(subtitle);
        header.Controls.Add(title);
        _header = header;
        return header;
    }

    /// <summary>
    /// Подгоняет ширину подзаголовка под свободное место слева от кнопок:
    /// иначе длинная строка налезала бы на панель инструментов.
    /// </summary>
    private void AdjustHeader()
    {
        if (_header == null || _headerTools == null || _lblSubtitle == null) return;
        var avail = _header.ClientSize.Width - _headerTools.Width - _header.Padding.Right - 26;
        _lblSubtitle.Width = Math.Max(120, avail);
    }

    private Panel BuildContent()
    {
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window, Padding = new Padding(20, 0, 20, 8) };

        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Window
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

        split.Controls.Add(BuildVmCard(), 0, 0);
        split.Controls.Add(BuildRightColumn(), 1, 0);

        content.Controls.Add(split);
        return content;
    }

    private RoundedPanel BuildVmCard()
    {
        var card = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 12), Margin = new Padding(0, 0, 8, 0) };

        var head = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.Card };
        head.Controls.Add(new Label
        {
            Text = "Виртуальные машины",
            Font = Theme.BodyBold,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(2, 0)
        });
        head.Controls.Add(new Label
        {
            Text = "галочка — автобэкап; для ручной копии выделите строку",
            Font = Theme.Small,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Location = new Point(2, 21)
        });

        _imgBadges = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
        _imgBadges.Images.Add(ScheduleBadgeImage());      // 0 — зелёный бейдж расписания

        // Кнопка восстановления в строке машины: значок рисуется прямо в ячейке,
        // потому что Win32 умеет значки только в первой колонке.
        _imgRestore = RestoreButtonImage();

        _lvVms = new IconListView
        {
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            SmallImageList = _imgBadges,
            RowIcon = _imgRestore,
            IconColumn = ColRestore,
            ShowItemToolTips = true
        };
        _lvVms.Columns.Add("Машина", 128);
        _lvVms.Columns.Add("⚙", 30);
        _lvVms.Columns.Add("↺", 28);
        _lvVms.Columns.Add("Расписание", 86);
        _lvVms.Columns.Add("Состояние", 72);
        _lvVms.Columns.Add("ЦП", 37);
        _lvVms.Columns.Add("Память", 54);
        _lvVms.Columns.Add("Дисков", 52);
        _lvVms.Columns.Add("Размер", 64);
        _lvVms.Columns.Add("Копий", 54);
        _lvVms.Columns.Add("Последняя копия", 115);
        _lvVms.ItemChecked += (_, _) =>
        {
            if (_suppressListEvents) return;
            try
            {
                SaveSelection();
                // Состав машин автобэкапа изменился — пересчитываем время запуска,
                // чтобы отметка галочкой не вызывала копирование задним числом.
                _scheduler.Recalculate();
                QueueBadgeUpdate();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Ошибка обработки отметки виртуальной машины: " + ex.Message);
            }
        };

        // Значок-шестерёнка: клик по нему открывает индивидуальные настройки этой ВМ.
        // Значок восстановления: клик открывает восстановление из копий этой ВМ.
        _lvVms.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            try
            {
                var hit = _lvVms.HitTest(e.Location);
                if (hit.Item == null) return;
                // ListViewHitTestInfo.SubItem — это сама ячейка, сравниваем по ссылке
                if (hit.Item.SubItems.Count <= ColGear) return;

                var vmName = hit.Item.Tag as string ?? "";
                if (vmName.Length == 0) return;

                // Синяя кнопка восстановления в строке: открывает копии этой машины.
                if (_lvVms.HitIcon(e.Location))
                {
                    BeginInvoke(new Action(() => ShowRestore(new[] { vmName })));
                    return;
                }

                if (!ReferenceEquals(hit.SubItem, hit.Item.SubItems[ColGear])) return;
                BeginInvoke(new Action(() => OpenVmSettings(vmName)));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Ошибка обработки клика в списке виртуальных машин: " + ex.Message);
            }
        };

        card.Controls.Add(_lvVms);
        card.Controls.Add(head);
        return card;
    }

    private TableLayoutPanel BuildRightColumn()
    {
        var column = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Window,
            Margin = new Padding(8, 0, 0, 0)
        };
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 186));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // ── Карточка состояния ──
        var progressCard = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 14, 16, 12) };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 4,
            BackColor = Theme.Card
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _lblPhase = new Label
        {
            Text = "Готов к работе",
            Font = Theme.BodyBold,
            ForeColor = Theme.Text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        grid.Controls.Add(_lblPhase, 0, 0);
        grid.SetColumnSpan(_lblPhase, 4);

        _pb = new RoundedProgressBar { Dock = DockStyle.Fill, BarHeight = 8 };
        grid.Controls.Add(_pb, 0, 1);
        grid.SetColumnSpan(_pb, 4);

        _lblSpeed = MetricLabel();
        _lblEta = MetricLabel();
        _lblVolume = MetricLabel();

        grid.Controls.Add(CaptionLabel("Скорость"), 0, 2);
        grid.Controls.Add(_lblSpeed, 1, 2);
        grid.Controls.Add(CaptionLabel("Осталось"), 2, 2);
        grid.Controls.Add(_lblEta, 3, 2);
        grid.Controls.Add(CaptionLabel("Объём"), 0, 3);
        grid.Controls.Add(_lblVolume, 1, 3);

        progressCard.Controls.Add(grid);

        // ── Карточка журнала ──
        var logCard = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            Surface = Theme.LogBg,
            BorderColor = Color.Transparent
        };
        _txtLog = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = Theme.Mono,
            BackColor = Theme.LogBg,
            ForeColor = Theme.LogText,
            BorderStyle = BorderStyle.None
        };
        logCard.Controls.Add(_txtLog);

        column.Controls.Add(progressCard, 0, 0);
        column.Controls.Add(logCard, 0, 1);
        return column;
    }

    private static Label MetricLabel() => new()
    {
        Text = "—",
        Font = Theme.Body,
        ForeColor = Theme.Text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };

    private static Label CaptionLabel(string text) => new()
    {
        Text = text,
        Font = Theme.Small,
        ForeColor = Theme.TextSecondary,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private Panel BuildCommandBar()
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 68, BackColor = Theme.Window, Padding = new Padding(20, 8, 20, 12) };

        _btnBackup = new RoundedButton
        {
            Text = "Сделать копию",
            Glyph = "copy",
            Appearance = RoundedButton.ButtonKind.Success,
            Pill = true,
            Bold = true,
            Size = new Size(200, 42),
            Font = Theme.BodyBold
        };
        _btnBackup.Click += (_, _) => StartBackup(scheduled: false);

        _btnSchedule = new RoundedButton
        {
            Text = "По расписанию",
            Glyph = "clock",
            Appearance = RoundedButton.ButtonKind.Scheduled,
            Pill = true,
            Bold = true,
            Size = new Size(190, 42),
            Font = Theme.BodyBold
        };
        _btnSchedule.Click += (_, _) => ShowSchedule();

        _btnStop = new RoundedButton
        {
            Text = "Остановить",
            Glyph = "stop",
            Appearance = RoundedButton.ButtonKind.Danger,
            Size = new Size(136, 42),
            Visible = false
        };
        _btnStop.Click += (_, _) =>
        {
            _btnStop.Enabled = false;
            _btnStop.Text = "Завершение…";
            SetStatus("Отмена принята: удаляем контрольную точку и возвращаем исходный тип точки — это может занять до минуты.");
            AppLog.Info("Пользователь нажал «Остановить» — копирование отменяется, завершаем контрольную точку.");
            _cts?.Cancel();
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Width = 560,
            Height = 46,
            BackColor = Theme.Window,
            Padding = new Padding(0, 2, 0, 0),
            Margin = new Padding(0)
        };
        actions.Controls.Add(_btnBackup);
        actions.Controls.Add(_btnSchedule);
        actions.Controls.Add(_btnStop);

        _lblStatus = new Label
        {
            Dock = DockStyle.Right,
            Width = 560,
            TextAlign = ContentAlignment.MiddleRight,
            Font = Theme.Small,
            ForeColor = Theme.TextSecondary,
            AutoEllipsis = true
        };

        bar.Controls.Add(_lblStatus);
        bar.Controls.Add(actions);
        return bar;
    }

    /// <summary>Рисует значки строк поверх снимка окна (для предпросмотра).</summary>
    internal void PaintRowIcons(Bitmap bmp) => _lvVms.PaintIconsOnto(bmp, this);

    /// <summary>Наполняет окно демонстрационными данными (для предпросмотра оформления).</summary>
    internal void ApplyPreviewData(IReadOnlyList<VmInfo> vms, string logText)
    {
        // демо-расписание, чтобы в предпросмотре были видны зелёные значки
        _settings.Schedule = ScheduleKind.Daily;
        _settings.Hour = 3;
        _settings.Minute = 0;

        _suppressListEvents = true;
        try
        {
            _lvVms.BeginUpdate();
            _lvVms.Items.Clear();
            foreach (var vm in vms)
            {
                var item = new ListViewItem(vm.Name) { Tag = vm.Name, Checked = true };
                item.SubItems.Add("⚙");                    // шестерёнка — индивидуальные настройки ВМ
                item.SubItems.Add("");                     // кнопка восстановления (значок рисуется в ячейке)
                item.SubItems.Add("");                     // зелёный бейдж расписания
                item.SubItems.Add(vm.State);
                item.SubItems.Add(vm.ProcessorCount.ToString());
                item.SubItems.Add(vm.MemoryText);
                item.SubItems.Add(vm.Disks.Count.ToString());
                item.SubItems.Add(vm.DiskSizeText);
                item.SubItems.Add("3");
                item.SubItems.Add("24.11.2026 03:00");
                _lvVms.Items.Add(item);
            }
            _lvVms.EndUpdate();
        }
        finally
        {
            _suppressListEvents = false;
        }
        UpdateScheduleBadges();

        _txtLog.Text = logText;
        _lblPhase.Text = "Копирование: Win10-Test_Disk1.vhdx";
        _lblSpeed.Text = "28,4 МБ/с";
        _lblEta.Text = "00:12:37";
        _lblVolume.Text = "18,6 ГБ из 41,2 ГБ (45 %)";
        _pb.SetProgress((long)(41.2 * 0.45), (long)41.2);
        AdjustVmColumns();
        SetStatus("копирование Win10-Test · хранилища: локально + сеть + FTP · копий: 3 · лимит 40 МБ/с");
    }

    /// <summary>Значок зелёной «пилюли» расписания для списка виртуальных машин.</summary>
    private static Bitmap ScheduleBadgeImage()
    {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(Color.FromArgb(22, 163, 74));
            using var ring = new Pen(Color.FromArgb(70, 200, 120), 1.2f);
            g.FillEllipse(fill, 2, 2, 12, 12);
            g.DrawEllipse(ring, 2.6f, 2.6f, 10.8f, 10.8f);
            using var hand = new Pen(Color.White, 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(hand, 8, 5.2f, 8, 8.2f);
            g.DrawLine(hand, 8, 8.2f, 10.4f, 9.4f);
        }
        return bmp;
    }

    /// <summary>
    /// Кнопка восстановления в строке виртуальной машины: синяя скруглённая
    /// кнопка с белой круговой стрелкой. Клик открывает восстановление
    /// из копий этой машины.
    /// </summary>
    private static Bitmap RestoreButtonImage()
    {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var fill = new SolidBrush(Color.FromArgb(0, 103, 192));
            using var edge = new Pen(Color.FromArgb(0, 74, 145), 1f);
            var box = new RectangleF(0.5f, 0.5f, 15, 15);

            using var path = new GraphicsPath();
            path.AddArc(box, 90, 90);
            path.AddArc(box, 270, 90);
            path.CloseFigure();
            g.FillPath(fill, path);
            g.DrawPath(edge, path);

            Glyphs.Draw(g, "restore", new RectangleF(3, 3, 10, 10), Color.White);
        }
        return bmp;
    }

    /// <summary>
    /// Проставляет зелёный значок с цикличностью рядом с виртуальными машинами,
    /// которые входят в копирование по расписанию.
    /// </summary>
    private void UpdateScheduleBadges()
    {
        if (_lvVms is null) return;

        try
        {
            ApplyScheduleBadges(_lvVms, _settings);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось обновить значки расписания: " + ex.Message);
        }
    }

    /// <summary>
    /// Проставляет зелёный значок и цикличность в списке машин.
    /// Сделано отдельным безопасным методом: список может меняться прямо во время
    /// обработки уведомления ListView, и обращение к неготовой подписи не должно ронять программу.
    /// </summary>
    internal static void ApplyScheduleBadges(ListView list, AppSettings settings)
    {
        foreach (ListViewItem item in list.Items)
        {
            if (item is null) continue;

            // во время перестроения списка подпись может быть ещё не создана
            var cell = item.SubItems.Count > ColSchedule ? item.SubItems[ColSchedule] : null;
            if (cell is null) continue;

            var vmName = item.Tag as string ?? "";
            var eff = settings.ForVm(vmName);          // у ВМ может быть свой график
            var active = ScheduleText.IsActive(eff);
            var badge = ScheduleText.Badge(eff);
            var full = ScheduleText.Describe(eff);

            if (active && item.Checked)
            {
                item.ImageIndex = 0;
                cell.Text = badge;
                item.ToolTipText = $"Копирование по расписанию: {full}" + RestoreHint(settings, vmName);
            }
            else
            {
                item.ImageIndex = -1;
                cell.Text = "";
                item.ToolTipText = (active
                    ? "Машина не включена в копирование по расписанию"
                    : "Расписание выключено") + RestoreHint(settings, vmName);
            }
        }
    }

    /// <summary>Подсказка о копиях машины (их можно восстановить кнопкой в строке).</summary>
    private static string RestoreHint(AppSettings settings, string vmName)
    {
        var (copies, last) = BackupStats(settings, vmName);
        return copies > 0
            ? $" · копий: {copies} (последняя {last}) — восстановление кнопкой в строке"
            : " · копий нет — восстанавливать нечего";
    }

    /// <summary>
    /// Обновление значков откладывается на потом: менять список прямо во время
    /// обработки уведомления ListView нельзя — это приводит к падению.
    /// </summary>
    private void QueueBadgeUpdate()
    {
        if (_suppressListEvents || _badgeUpdateQueued) return;
        _badgeUpdateQueued = true;
        try
        {
            BeginInvoke(new Action(() =>
            {
                _badgeUpdateQueued = false;
                UpdateScheduleBadges();
            }));
        }
        catch (InvalidOperationException)
        {
            // окно ещё не создано или уже закрыто — обновим позже
            _badgeUpdateQueued = false;
        }
    }

    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.FromArgb(0, 103, 192));
            using var green = new SolidBrush(Color.FromArgb(16, 124, 16));
            using var white = new SolidBrush(Color.White);
            g.FillEllipse(body, 2, 2, 28, 28);
            g.FillRectangle(green, 10, 13, 12, 11);
            g.FillRectangle(white, 14, 8, 4, 12);
            g.FillPolygon(white, new[]
            {
                new PointF(11, 16), new PointF(21, 16), new PointF(16, 23)
            });
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    // ─────────────────────────────── Логика ───────────────────────────────
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TryRoundWindowCorners();
        try
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var admin = new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            if (!admin) SetStatus("Запустите программу от имени администратора — без прав Hyper-V недоступен.");
        }
        catch { }
        BeginInvoke(new Action(AdjustVmColumns));
        _ = RefreshVmsAsync();
        ApplyAutoStart();
        CleanupLogs();
        if (_settings.RunOnStart) BeginInvoke(new Action(() => StartBackup(scheduled: true)));
    }

    /// <summary>Удаляет файлы журнала старше заданного срока хранения.</summary>
    private void CleanupLogs()
    {
        var removed = AppLog.CleanupOldLogs(_settings.LogRetentionDays);
        if (removed > 0)
            AppLog.Info($"Удалено старых файлов журнала: {removed} (срок хранения {_settings.LogRetentionDays} дн.).");
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void TryRoundWindowCorners()
    {
        try
        {
            var pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
        }
        catch { }
    }

    private async Task RefreshVmsAsync()
    {
        _btnRefresh.Enabled = false;
        var (ok, vms, err) = await Task.Run(() => HyperV.ListVms());
        if (!ok)
        {
            SetStatus("Нет доступа к Hyper-V: " + err);
            AppLog.Warn("Нет доступа к списку виртуальных машин: " + err);
            _btnRefresh.Enabled = true;
            return;
        }

        var names = vms.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _settings.SelectedVms.Where(s => !names.Contains(s)).ToList())
            _settings.SelectedVms.Remove(stale);

        // Выделение строк сохраняем: по нему выбирается машина для ручной копии.
        var selected = _lvVms.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag as string ?? "")
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _suppressListEvents = true;
        try
        {
            _lvVms.BeginUpdate();
            _lvVms.Items.Clear();
            foreach (var vm in vms.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new ListViewItem(vm.Name) { Tag = vm.Name, Checked = _settings.SelectedVms.Contains(vm.Name) };
                item.SubItems.Add("⚙");                    // шестерёнка — индивидуальные настройки ВМ
                item.SubItems.Add("");                     // кнопка восстановления (значок рисуется в ячейке)
                item.SubItems.Add("");                     // зелёный бейдж расписания
                item.SubItems.Add(vm.State);
                item.SubItems.Add(vm.ProcessorCount.ToString());
                item.SubItems.Add(vm.MemoryText);
                item.SubItems.Add(vm.Disks.Count.ToString());
                item.SubItems.Add(vm.DiskSizeText);
                var (copies, last) = BackupStats(vm.Name);
                item.SubItems.Add(copies.ToString());
                item.SubItems.Add(last);
                if (selected.Contains(vm.Name)) item.Selected = true;
                _lvVms.Items.Add(item);
            }
            _lvVms.EndUpdate();
        }
        finally
        {
            _suppressListEvents = false;
        }
        UpdateScheduleBadges();
        AdjustVmColumns();

        SetStatus($"Найдено виртуальных машин: {vms.Count}");
        _btnRefresh.Enabled = true;
        UpdateStatus();
    }

    /// <summary>Последняя колонка списка растягивается на всю оставшуюся ширину.</summary>
    private void AdjustVmColumns()
    {
        if (_lvVms.Columns.Count < 2) return;
        var columns = _lvVms.Columns.Cast<ColumnHeader>().ToList();
        // запас на рамку и возможную вертикальную полосу прокрутки
        var available = Math.Max(120, _lvVms.ClientSize.Width - 24);

        // Последняя колонка забирает всю оставшуюся ширину.
        var fixedWidth = columns.Take(columns.Count - 1).Sum(c => c.Width);
        columns[^1].Width = Math.Max(70, available - fixedWidth);

        // Если всё равно шире — подрезаем колонку с именем машины, затем последнюю.
        var total = columns.Sum(c => c.Width);
        if (total > available)
        {
            var excess = total - available;
            columns[0].Width = Math.Max(110, columns[0].Width - excess);
            total = columns.Sum(c => c.Width);
            if (total > available)
                columns[^1].Width = Math.Max(70, columns[^1].Width - (total - available));
        }
    }
    private (int Copies, string Last) BackupStats(string vmName) => BackupStats(_settings, vmName);

    /// <summary>Сколько копий у машины и когда самая свежая (по папкам копий).</summary>
    internal static (int Copies, string Last) BackupStats(AppSettings settings, string vmName)
    {
        try
        {
            var root = settings.ForVm(vmName).BackupRoot;   // у ВМ может быть своя папка
            var folder = Path.Combine(root, BackupEngine.SafeName(vmName));
            if (!Directory.Exists(folder)) return (0, "—");
            var dirs = Directory.GetDirectories(folder)
                .Select(d => Path.GetFileName(d)!)
                .Where(n => BackupEngine.IsCopyFolderName(n))
                .OrderByDescending(n => n, StringComparer.Ordinal)
                .ToList();
            if (dirs.Count == 0) return (0, "—");
            var last = DateTime.ParseExact(dirs[0][..19], "yyyy-MM-dd_HH-mm-ss", null);
            return (dirs.Count, last.ToString("dd.MM.yyyy HH:mm"));
        }
        catch
        {
            return (0, "—");
        }
    }

    private void SaveSelection()
    {
        _settings.SelectedVms = _lvVms.CheckedItems.Cast<ListViewItem>()
            .Select(i => i.Tag as string ?? "")
            .Where(s => s.Length > 0)
            .ToList();
        SettingsStore.Save(_settings);
    }

    /// <summary>
    /// Запуск копирования.
    /// <para><c>scheduled = true</c> — автобэкап: копируются машины, отмеченные галочками.</para>
    /// <para><c>scheduled = false</c> — вручную: копируется выделенная в списке строка (одна или несколько).</para>
    /// </summary>
    private void StartBackup(bool scheduled)
    {
        if (_busy)
        {
            SetStatus("Копирование уже выполняется.");
            return;
        }

        List<string> targets;

        if (scheduled)
        {
            targets = CheckedVmNames(_lvVms.CheckedItems.Cast<ListViewItem>());

            if (targets.Count == 0)
            {
                SetStatus("Нет машин, включённых в автобэкап — копирование пропущено.");
                AppLog.Warn("Нет машин, включённых в автобэкап — копирование пропущено.");
                return;
            }
        }
        else
        {
            targets = SelectedVmNames(_lvVms.SelectedItems.Cast<ListViewItem>());

            if (targets.Count == 0)
            {
                if (!Visible) RestoreFromTray();
                MessageBox.Show(this,
                    "Выберите в списке виртуальную машину, для которой нужно сделать копию.\n" +
                    "Чтобы выбрать несколько машин, держите Ctrl или Shift.",
                    "ГиперВ Резерв", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }

        _ = RunBackupAsync(targets);
    }

    /// <summary>Запуск копирования по расписанию: планировщик решил, что эти машины «должны» копироваться.</summary>
    private void StartBackupFor(IReadOnlyList<string> vms)
    {
        if (_busy)
        {
            SetStatus("Копирование уже выполняется — запуск по расписанию пропущен.");
            AppLog.Warn("Копирование уже выполняется — запуск по расписанию пропущен.");
            return;
        }

        var targets = vms.Where(s => s.Length > 0).ToList();
        if (targets.Count == 0) return;

        AppLog.Info($"Запуск по расписанию: {string.Join(", ", targets)}.");
        _ = RunBackupAsync(targets);
    }

    /// <summary>Открывает диалог индивидуальных настроек виртуальной машины.</summary>
    private void OpenVmSettings(string vmName)
    {
        try
        {
            using var form = new VmSettingsForm(_settings, vmName);
            if (form.ShowDialog(this) != DialogResult.OK) return;

            SettingsStore.Save(_settings);
            _scheduler.Recalculate();

            // галочка автобэкапа могла измениться — приводим список в соответствие
            _suppressListEvents = true;
            try
            {
                foreach (ListViewItem item in _lvVms.Items)
                {
                    var name = item.Tag as string ?? "";
                    if (name.Length == 0) continue;
                    item.Checked = _settings.SelectedVms.Contains(name, StringComparer.OrdinalIgnoreCase);
                }
            }
            finally
            {
                _suppressListEvents = false;
            }

            UpdateScheduleBadges();
            UpdateStatus();
            SetStatus($"Настройки ВМ «{vmName}» сохранены.");
            AppLog.Info($"Настройки ВМ «{vmName}» сохранены.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка сохранения настроек ВМ: " + ex.Message);
            MessageBox.Show(this, "Не удалось сохранить настройки: " + ex.Message,
                "ГиперВ Резерв", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Машины автобэкапа: строки, отмеченные галочками.</summary>
    internal static List<string> CheckedVmNames(IEnumerable<ListViewItem> items) =>
        items.Select(i => i.Tag as string ?? "")
            .Where(s => s.Length > 0)
            .ToList();

    /// <summary>Машины для ручной копии: выделенные строки списка.</summary>
    internal static List<string> SelectedVmNames(IEnumerable<ListViewItem> items) =>
        items.Select(i => i.Tag as string ?? "")
            .Where(s => s.Length > 0)
            .ToList();

    private async Task RunBackupAsync(List<string> targets)
    {
        _busy = true;
        _cts = new CancellationTokenSource();
        _btnBackup.Enabled = false;
        _btnRefresh.Enabled = false;
        _btnRestore.Enabled = false;
        _btnStop.Visible = true;

        var okCount = 0;
        var failed = 0;
        try
        {
            foreach (var vmName in targets)
            {
                if (_cts.IsCancellationRequested) break;
                SetStatus($"Копирование: {vmName}");
                // Свой идентификатор на каждую машину: поздние фазы уборки предыдущей
                // ВМ не должны перекрывать прогресс следующей.
                var runId = _progressGate.Begin();
                var engine = new BackupEngine(_settings, AppLog.Info, p => PostProgress(p, runId));
                var res = await engine.RunAsync(vmName, _cts.Token);
                if (res.Ok) okCount++;
                else if (!res.Cancelled) failed++;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Непредвиденная ошибка: " + ex.Message);
            failed++;
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            _btnBackup.Enabled = true;
            _btnRefresh.Enabled = true;
            _btnRestore.Enabled = true;
            _btnStop.Visible = false;
            _btnStop.Enabled = true;
            _btnStop.Text = "Остановить";
            // Сброс интерфейса выполняется до снятия активного запуска — всё, что уже
            // поставлено в очередь (поздние фазы уборки), будет отброшено ниже.
            _pb.Reset();
            _lblPhase.Text = "Готов к работе";
            _lblSpeed.Text = "—";
            _lblEta.Text = "—";
            _lblVolume.Text = "—";
            _progressGate.End();
            SetStatus(failed == 0
                ? $"Готово. Успешно: {okCount}."
                : $"Готово. Успешно: {okCount}, с ошибкой: {failed}.");
            _ = RefreshVmsAsync();
        }
    }

    /// <summary>
    /// Обновление прогресса приходит из движка и может «опоздать»: фаза уборки
    /// (удаление точки, возврат типа точки) отправляется уже после того, как интерфейс
    /// сброшен. Такие поздние обновления отбрасываются — иначе надпись фазы и анимация
    /// полосы остаются висеть после завершения копирования.
    /// </summary>
    private void PostProgress(BackupProgress p, int runId)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                if (!_progressGate.IsCurrent(runId)) return;
                _lblPhase.Text = p.Phase;
                if (p.Indeterminate)
                {
                    _pb.SetIndeterminate();
                }
                else
                {
                    _pb.SetProgress(p.Done, p.Total);
                    _lblSpeed.Text = ProgressText.Speed(p);
                    _lblEta.Text = ProgressText.Eta(p);
                    _lblVolume.Text = ProgressText.Volume(p);
                }
            }));
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>Начать новый «запуск» показателей карточки (для демонстрации хода копирования).</summary>
    internal int BeginProgress() => _progressGate.Begin();

    /// <summary>Подать обновление показателей в карточку (для демонстрации хода копирования).</summary>
    internal void UpdateProgress(BackupProgress p, int runId) => PostProgress(p, runId);

    private void OnLog(AppLog.Entry entry)    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                _txtLog.AppendText($"{entry.Time:HH:mm:ss} | {entry.Level} | {entry.Message}{Environment.NewLine}");
                if (_txtLog.Lines.Length > 2000) _txtLog.Lines = _txtLog.Lines.Skip(1000).ToArray();
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void SetStatus(string text) => _lblStatus.Text = text;

    private void UpdateStatus()
    {
        var next = _scheduler.NextRun;
        var active = ScheduleText.IsActive(_settings);
        var ownSchedules = _settings.PerVm.Values.Count(v => v.UseOwnSchedule);
        var schedule = active ? ScheduleText.Describe(_settings) : "расписание выключено";
        if (ownSchedules > 0) schedule += $" + свои графики: {ownSchedules}";
        if (next != null) schedule += $" · след. запуск {next:dd.MM HH:mm}";

        var targets = new List<string> { "локально" };
        if (_settings.NetworkEnabled) targets.Add("сеть");
        if (_settings.FtpEnabled) targets.Add("FTP");

        var volume = "";
        try
        {
            var root = Path.GetFullPath(_settings.BackupRoot);
            var drive = Path.GetPathRoot(root);
            if (!string.IsNullOrEmpty(drive))
            {
                var di = new DriveInfo(drive);
                volume = $" · свободно {ThrottledCopier.FormatBytes(di.AvailableFreeSpace)}";
            }
        }
        catch { }

        var limit = _settings.SpeedLimitMBps > 0 ? $"{_settings.SpeedLimitMBps} МБ/с" : "без лимита";
        var copies = $"{_settings.KeepCopies} копии";

        var text = $"{schedule} · {string.Join("+", targets)} · {copies} · {limit}{volume}";
        if (_mcp.IsRunning) text += $" · MCP: {_mcp.McpUrl}";
        _traySchedule.Text = active || ownSchedules > 0
            ? "Расписание: " + ScheduleText.Describe(_settings) + (ownSchedules > 0 ? $" (+{ownSchedules} свои)" : "")
            : "Расписание выключено";
        _trayMcp.Text = _mcp.IsRunning ? "MCP-сервер: " + _mcp.BaseUrl : "MCP-сервер: выключен";
        if (!_busy) SetStatus(text);
    }

    /// <summary>Открывает документацию MCP-сервера из трея.</summary>
    private void OpenMcpSwagger()
    {
        if (!_mcp.IsRunning)
        {
            MessageBox.Show(this,
                "MCP-сервер выключен. Включите его в «Настройки → MCP-сервер».",
                "MCP-сервер", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = _mcp.SwaggerUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось открыть документацию MCP-сервера: " + ex.Message);
        }
    }

    /// <summary>Диалог настроек расписания (жёлтая кнопка «По расписанию»).</summary>
    private void ShowSchedule()
    {
        using var form = new ScheduleForm(_settings);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            SettingsStore.Save(_settings);
            _scheduler.Recalculate();
            UpdateScheduleBadges();
            UpdateStatus();
            SetStatus(ScheduleText.IsActive(_settings)
                ? "Расписание сохранено: " + ScheduleText.Describe(_settings) + "."
                : "Расписание отключено.");
        }
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_settings, _mcp);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            SettingsStore.Save(_settings);
            _scheduler.Recalculate();
            ApplyAutoStart();
            ApplyMcpSettings();
            CleanupLogs();
            UpdateScheduleBadges();
            UpdateStatus();
            SetStatus("Настройки сохранены.");
        }
    }

    /// <summary>
    /// Диалог восстановления виртуальной машины из найденной копии.
    /// <para><paramref name="vmNames"/> — машины, чьи копии показываем (пусто — все копии).</para>
    /// </summary>
    private void ShowRestore(IReadOnlyList<string>? vmNames = null)
    {
        if (_busy)
        {
            SetStatus("Копирование уже выполняется — восстановление отложено.");
            MessageBox.Show(this,
                "Сейчас выполняется копирование. Восстановление доступно после его окончания.",
                "ГиперВ Резерв", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            // Кнопка в шапке — общий путь: восстановление из произвольной копии.
            // Кнопка у машины в списке — восстановление этой машины из её копий.
            using var form = new RestoreForm(_settings, vmNames);
            if (form.ShowDialog(this) != DialogResult.OK) return;

            SetStatus("Восстановление завершено.");
            AppLog.Info("Восстановление завершено — список машин обновлён.");
            _ = RefreshVmsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка восстановления: " + ex.Message);
            MessageBox.Show(this, "Не удалось выполнить восстановление: " + ex.Message,
                "ГиперВ Резерв", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ApplyAutoStart()
    {
        try
        {
            var exe = Application.ExecutablePath;
            if (_settings.AutoStart)
            {
                if (!AutoStart.IsEnabled())
                {
                    var (ok, err) = AutoStart.Enable(exe);
                    if (ok) AppLog.Info("Автозапуск при входе в Windows включён.");
                    else AppLog.Warn("Не удалось включить автозапуск: " + err);
                }
            }
            else if (AutoStart.IsEnabled())
            {
                AutoStart.Disable();
                AppLog.Info("Автозапуск при входе в Windows отключён.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Ошибка настройки автозапуска: " + ex.Message);
        }
    }

    private void OpenBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.BackupRoot);
            Process.Start(new ProcessStartInfo { FileName = _settings.BackupRoot, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось открыть папку: " + ex.Message, "ГиперВ Резерв",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenLog()
    {
        try
        {
            var file = Path.Combine(SettingsStore.DataDir, $"log-{DateTime.Now:yyyy-MM}.txt");
            if (File.Exists(file)) Process.Start(new ProcessStartInfo { FileName = file, UseShellExecute = true });
            else MessageBox.Show(this, "Файл журнала ещё не создан.", "ГиперВ Резерв",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось открыть журнал: " + ex.Message, "ГиперВ Резерв",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        _tray.ShowBalloonTip(2500, "ГиперВ Резерв",
            "Программа свёрнута в трей и продолжает копировать по расписанию.", ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_reallyExit) return;
        if (_settings.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            AppLog.Logged -= OnLog;
            _scheduler.Dispose();
            _mcp.Dispose();
            _settingsWatcher?.Dispose();
            _settingsReloadTimer?.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
