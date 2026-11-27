using HvBackup.Ui;

namespace HvBackup;

/// <summary>
/// Диалог настроек автоматического копирования: включение/отключение расписания и его периодичность.
/// Открывается жёлтой кнопкой «По расписанию» в главном окне.
/// </summary>
public sealed class ScheduleForm : Form
{
    private readonly AppSettings _settings;

    private ToggleSwitch _switch = null!;
    private ComboBox _cmbKind = null!;
    private NumericUpDown _numEvery = null!;
    private NumericUpDown _numHour = null!;
    private NumericUpDown _numMinute = null!;
    private ComboBox _cmbDay = null!;
    private Panel _pnlEvery = null!;
    private Panel _pnlDaily = null!;
    private Panel _pnlWeekly = null!;
    private Panel _pnlOptions = null!;
    private TableLayoutPanel _root = null!;
    private Label _lblSummary = null!;
    private Label _lblNext = null!;
    private Label _lblMachines = null!;

    public ScheduleForm(AppSettings settings)
    {
        _settings = settings;
        BuildUi();
        LoadValues();
    }

    private void BuildUi()
    {
        Text = "Расписание — ГиперВ Резерв";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(580, 430);
        Font = Theme.Body;
        BackColor = Theme.Window;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Window,
            Padding = new Padding(16, 14, 16, 10)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _root = root;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));   // включение
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 114));   // периодичность
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));    // кто попадёт в расписание
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));    // кнопки

        // ── Карточка «включение» ──
        var cardOn = new RoundedPanel { Dock = DockStyle.Fill, Surface = Theme.Card };
        cardOn.Controls.Add(new Label
        {
            Text = "Автоматическое копирование",
            Font = Theme.BodyBold,
            ForeColor = Theme.Text,
            Location = new Point(16, 12),
            AutoSize = true
        });
        _switch = new ToggleSwitch { Checked = true, Location = new Point(16, 44) };
        _switch.CheckedChanged += (_, _) => UpdateState();
        cardOn.Controls.Add(_switch);
        cardOn.Controls.Add(new Label
        {
            Text = "Расписание включено",
            Font = Theme.Body,
            ForeColor = Theme.Text,
            Location = new Point(72, 45),
            AutoSize = true
        });
        _lblSummary = new Label
        {
            Font = Theme.Small,
            ForeColor = Theme.TextSecondary,
            Location = new Point(16, 76),
            Size = new Size(510, 18),
            AutoEllipsis = true
        };
        cardOn.Controls.Add(_lblSummary);

        // ── Карточка «периодичность» ──
        var cardKind = new RoundedPanel { Dock = DockStyle.Fill, Surface = Theme.Card };
        cardKind.Controls.Add(new Label
        {
            Text = "Периодичность",
            Font = Theme.BodyBold,
            ForeColor = Theme.Text,
            Location = new Point(16, 12),
            AutoSize = true
        });

        _cmbKind = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(16, 36),
            Width = 320,
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            FlatStyle = FlatStyle.Flat
        };
        _cmbKind.Items.AddRange(new object[] { "Каждые N минут", "Ежедневно в заданное время", "Еженедельно (день и время)" });
        _cmbKind.SelectedIndexChanged += (_, _) => LayoutOptions();
        cardKind.Controls.Add(_cmbKind);

        _pnlOptions = new Panel { Location = new Point(16, 70), Size = new Size(510, 66), BackColor = Theme.Card };

        _pnlEvery = new Panel { Size = new Size(510, 28), BackColor = Theme.Card };
        _pnlEvery.Controls.Add(new Label
        {
            Text = "Интервал, минут",
            Location = new Point(0, 4),
            Size = new Size(170, 22),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text
        });
        _numEvery = new NumericUpDown { Minimum = 1, Maximum = 43200, Value = 60, Location = new Point(172, 2), Width = 90 };
        _pnlEvery.Controls.Add(_numEvery);

        _pnlDaily = new Panel { Size = new Size(510, 28), BackColor = Theme.Card };
        _pnlDaily.Controls.Add(new Label
        {
            Text = "Время запуска",
            Location = new Point(0, 4),
            Size = new Size(170, 22),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text
        });
        _numHour = new NumericUpDown { Minimum = 0, Maximum = 23, Value = 3, Location = new Point(172, 2), Width = 58 };
        _numMinute = new NumericUpDown { Minimum = 0, Maximum = 59, Value = 0, Location = new Point(236, 2), Width = 58 };
        _pnlDaily.Controls.Add(_numHour);
        _pnlDaily.Controls.Add(_numMinute);

        _pnlWeekly = new Panel { Size = new Size(510, 28), BackColor = Theme.Card };
        _pnlWeekly.Controls.Add(new Label
        {
            Text = "День недели",
            Location = new Point(0, 4),
            Size = new Size(170, 22),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text
        });
        _cmbDay = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(172, 2),
            Width = 150,
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            FlatStyle = FlatStyle.Flat
        };
        for (var i = 0; i < 7; i++) _cmbDay.Items.Add(ScheduleText.DayFull(i));
        _pnlWeekly.Controls.Add(_cmbDay);

        _pnlOptions.Controls.Add(_pnlEvery);
        _pnlOptions.Controls.Add(_pnlDaily);
        _pnlOptions.Controls.Add(_pnlWeekly);
        cardKind.Controls.Add(_pnlOptions);

        // ── Карточка «что попадёт в расписание» ──
        var cardInfo = new RoundedPanel { Dock = DockStyle.Fill, Surface = Theme.Card };
        cardInfo.Controls.Add(new Label
        {
            Text = "В расписание попадут",
            Font = Theme.BodyBold,
            ForeColor = Theme.Text,
            Location = new Point(16, 12),
            AutoSize = true
        });
        _lblMachines = new Label
        {
            Font = Theme.Small,
            ForeColor = Theme.TextSecondary,
            Location = new Point(16, 36),
            Size = new Size(510, 18),
            AutoEllipsis = true
        };
        cardInfo.Controls.Add(_lblMachines);
        _lblNext = new Label
        {
            Font = Theme.Small,
            ForeColor = Theme.TextSecondary,
            Location = new Point(16, 60),
            Size = new Size(510, 18),
            AutoEllipsis = true
        };
        cardInfo.Controls.Add(_lblNext);
        cardInfo.Controls.Add(new Label
        {
            Text = "Свой график для отдельной машины задаётся шестерёнкой в главном окне.",
            Font = Theme.Small,
            ForeColor = Theme.TextDisabled,
            Location = new Point(16, 82),
            Size = new Size(510, 18),
            AutoEllipsis = true
        });

        // ── Кнопки ──
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 0, 0),
            BackColor = Theme.Window
        };
        var btnOk = new RoundedButton
        {
            Text = "Сохранить",
            Appearance = RoundedButton.ButtonKind.Accent,
            Pill = true,
            Bold = true,
            Size = new Size(124, 34),
            Font = Theme.BodyBold
        };
        var btnCancel = new RoundedButton { Text = "Отмена", Size = new Size(104, 34) };
        btnOk.Click += (_, _) => ApplyValues();
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(btnOk);
        buttons.Controls.Add(btnCancel);

        root.Controls.Add(cardOn, 0, 0);
        root.Controls.Add(cardKind, 0, 1);
        root.Controls.Add(cardInfo, 0, 2);
        root.Controls.Add(buttons, 0, 3);

        Controls.Add(root);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void LoadValues()
    {
        _switch.Checked = ScheduleText.IsActive(_settings);
        _cmbKind.SelectedIndex = _settings.Schedule switch
        {
            ScheduleKind.EveryMinutes => 0,
            ScheduleKind.Daily => 1,
            ScheduleKind.Weekly => 2,
            _ => 1
        };
        _numEvery.Value = Math.Clamp(_settings.EveryMinutes, 1, 43200);
        _numHour.Value = Math.Clamp(_settings.Hour, 0, 23);
        _numMinute.Value = Math.Clamp(_settings.Minute, 0, 59);
        _cmbDay.SelectedIndex = Math.Clamp(_settings.WeeklyDay, 0, 6);
        UpdateState();
    }

    /// <summary>Показывает/скрывает нужные поля и пересчитывает предпросмотр расписания.</summary>
    private void UpdateState()
    {
        var enabled = _switch.Checked;
        _cmbKind.Enabled = enabled;
        _numEvery.Enabled = enabled;
        _numHour.Enabled = enabled;
        _numMinute.Enabled = enabled;
        _cmbDay.Enabled = enabled;
        LayoutOptions();

        if (!enabled)
        {
            _lblSummary.Text = "Расписание выключено — автоматические копии не выполняются.";
            _lblSummary.ForeColor = Theme.TextSecondary;
            _lblNext.Text = "Следующий запуск: нет";
            _lblMachines.Text = _settings.SelectedVms.Count == 0
                ? "Машины автобэкапа: нет (отметьте их галочками в главном окне)"
                : "Машины автобэкапа: " + string.Join(", ", _settings.SelectedVms);
            return;
        }

        var probe = Probe();
        _lblSummary.Text = "Сейчас: " + ScheduleText.Describe(probe);
        _lblSummary.ForeColor = Theme.Text;
        _lblNext.Text = "Следующий запуск: " +
            (new BackupScheduler(probe).ComputeNext(DateTime.Now)?.ToString("dd.MM.yyyy HH:mm") ?? "—");
        _lblMachines.Text = _settings.SelectedVms.Count == 0
            ? "Машины автобэкапа: нет (отметьте их галочками в главном окне)"
            : "Машины автобэкапа: " + string.Join(", ", _settings.SelectedVms);
    }

    /// <summary>Текущий выбор как будто он сохранён — для предпросмотра следующего запуска.</summary>
    private AppSettings Probe()
    {
        var probe = _settings.Clone();
        probe.Schedule = CurrentKind();
        probe.EveryMinutes = (int)_numEvery.Value;
        probe.Hour = (int)_numHour.Value;
        probe.Minute = (int)_numMinute.Value;
        probe.WeeklyDay = _cmbDay.SelectedIndex;
        return probe;
    }

    private ScheduleKind CurrentKind() => _cmbKind.SelectedIndex switch
    {
        0 => ScheduleKind.EveryMinutes,
        2 => ScheduleKind.Weekly,
        _ => ScheduleKind.Daily
    };

    /// <summary>Расставляет видимые строки параметров одну за другой.</summary>
    private void LayoutOptions()
    {
        // важно: считать состояние явно — Control.Visible у невидимого родителя всегда false
        var kind = _cmbKind.SelectedIndex;
        var enabled = _switch.Checked;
        var showEvery = enabled && kind == 0;
        var showDaily = enabled && kind != 0;
        var showWeekly = enabled && kind == 2;

        _pnlEvery.Visible = showEvery;
        _pnlDaily.Visible = showDaily;
        _pnlWeekly.Visible = showWeekly;

        var y = 0;
        if (showEvery) { _pnlEvery.Location = new Point(0, y); y += 32; }
        if (showDaily) { _pnlDaily.Location = new Point(0, y); y += 32; }
        if (showWeekly) { _pnlWeekly.Location = new Point(0, y); y += 32; }

        // карточка и окно подстраиваются под число видимых полей
        var cardHeight = 82 + y;
        if (_root.RowStyles[1].Height != cardHeight)
        {
            _root.RowStyles[1].Height = cardHeight;
            ClientSize = new Size(580, 272 + cardHeight);
        }
    }

    private void ApplyValues()
    {
        if (_switch.Checked)
        {
            _settings.Schedule = CurrentKind();
            _settings.EveryMinutes = (int)_numEvery.Value;
            _settings.Hour = (int)_numHour.Value;
            _settings.Minute = (int)_numMinute.Value;
            _settings.WeeklyDay = _cmbDay.SelectedIndex;
        }
        else
        {
            _settings.Schedule = ScheduleKind.Disabled;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
