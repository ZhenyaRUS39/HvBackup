using HvBackup.Ui;

namespace HvBackup;

/// <summary>
/// Диалог индивидуальных настроек одной виртуальной машины.
/// Открывается по значку-шестерёнке в списке машин.
/// Всё, что здесь указано, применяется только к этой ВМ и перекрывает общие настройки.
/// </summary>
/// <remarks>
/// Раскладка сделана «плоской»: одна таблица с тремя колонками (подпись, значение,
/// пояснение) и панели с ручной расстановкой элементов. Вложенные TableLayoutPanel
/// в WinForms раскладываются непредсказуемо (строка получает высоту 100 px по умолчанию),
/// поэтому их здесь нет.
/// </remarks>
public sealed class VmSettingsForm : Form
{
    private const int RowCheck = 26;     // строка с флажком
    private const int RowHint = 24;      // строка с пояснением
    private const int RowSection = 30;   // заголовок раздела
    private const int RowField = 34;     // строка с текстовым полем
    private const int RowSpin = 30;      // строка с числовым полем
    private const int RowCombo = 30;     // строка с выпадающим списком
    private const int RowPanel = 36;     // строка с панелью параметров расписания

    private const int LabelWidth = 152;
    private const int ValueWidth = 132;

    private readonly AppSettings _settings;
    private readonly string _vmName;

    private CheckBox _chkOwn = null!;
    private CheckBox _chkAutoBackup = null!;
    private CheckBox _chkOwnSchedule = null!;

    private ModernTextBox _txtPath = null!;
    private NumericUpDown _numKeep = null!;
    private NumericUpDown _numSpeed = null!;
    private ComboBox _cmbMode = null!;
    private CheckBox _chkProduction = null!;
    private CheckBox _chkKeepLocal = null!;
    private ModernTextBox _txtNetwork = null!;
    private ModernTextBox _txtFtpDir = null!;

    private ComboBox _cmbKind = null!;
    private NumericUpDown _numEvery = null!;
    private NumericUpDown _numHour = null!;
    private NumericUpDown _numMinute = null!;
    private ComboBox _cmbDay = null!;

    private Panel _pnlEvery = null!;
    private Panel _pnlWhen = null!;
    private Label _lblNoDay = null!;
    private Label _lblColon = null!;

    public VmSettingsForm(AppSettings settings, string vmName)
    {
        _settings = settings;
        _vmName = vmName;
        BuildUi();
        LoadValues();
        UpdateState();
    }

    private void BuildUi()
    {
        Text = $"Настройки ВМ: {_vmName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(660, 680);
        Font = Theme.Body;
        BackColor = Theme.Window;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Window,
            Padding = new Padding(16, 12, 16, 10)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // содержимое
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));   // подсказка
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));   // кнопки

        var scroll = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window, AutoScroll = true, Padding = new Padding(0, 0, 6, 0) };
        var table = NewTable();
        table.Dock = DockStyle.Top;
        table.AutoSize = true;

        // ── Включение индивидуальных настроек ──
        _chkOwn = new CheckBox
        {
            Text = "Применять отдельные настройки к этой машине",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Font = Theme.BodyBold,
            Margin = new Padding(0, 4, 0, 0)
        };
        _chkOwn.CheckedChanged += (_, _) => UpdateState();
        AddRow(table, "", _chkOwn, null, RowCheck);

        AddRow(table, "", Hint("Пока флажок снят, эта ВМ использует общие настройки программы."), null, RowHint);

        // ── Автобэкап ──
        AddRow(table, "", SectionTitle("Автобэкап"), null, RowSection);

        _chkAutoBackup = new CheckBox
        {
            Text = "Включён для этой машины (та же галочка, что в списке)",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 4, 0, 0)
        };
        _chkAutoBackup.CheckedChanged += (_, _) => UpdateState();
        AddRow(table, "", _chkAutoBackup, null, RowCheck);

        _chkOwnSchedule = new CheckBox
        {
            Text = "Свой график копирования (иначе — общий)",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 4, 0, 0)
        };
        _chkOwnSchedule.CheckedChanged += (_, _) => UpdateState();
        AddRow(table, "", _chkOwnSchedule, null, RowCheck);

        _cmbKind = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 3, 0, 3),
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            FlatStyle = FlatStyle.Flat
        };
        _cmbKind.Items.Add("каждые N минут");
        _cmbKind.Items.Add("ежедневно в HH:mm");
        _cmbKind.Items.Add("еженедельно в день и время");
        _cmbKind.SelectedIndexChanged += (_, _) => UpdateState();
        AddRow(table, "", _cmbKind, null, RowCombo);

        _numEvery = new NumericUpDown { Minimum = 1, Maximum = 60 * 24 * 30, Value = 60, Size = new Size(76, 23), BackColor = Theme.Card, ForeColor = Theme.Text };
        _numHour = new NumericUpDown { Minimum = 0, Maximum = 23, Value = 3, Size = new Size(56, 23), BackColor = Theme.Card, ForeColor = Theme.Text };
        _numMinute = new NumericUpDown { Minimum = 0, Maximum = 59, Value = 0, Size = new Size(56, 23), BackColor = Theme.Card, ForeColor = Theme.Text };
        _cmbDay = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Size = new Size(150, 23), BackColor = Theme.Card, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat };
        for (int d = 0; d < 7; d++) _cmbDay.Items.Add(ScheduleText.DayFull(d));

        // Панели параметров расписания: позиции заданы вручную — без вложенных таблиц.
        _pnlEvery = new Panel { Dock = DockStyle.Top, Height = RowPanel, BackColor = Theme.Window, Margin = new Padding(0) };
        var lblEvery = HintAt("каждые");
        lblEvery.Location = new Point(0, 7);
        lblEvery.Size = new Size(54, 22);
        _numEvery.Location = new Point(58, 5);
        var lblMinutes = HintAt("минут");
        lblMinutes.Location = new Point(140, 7);
        lblMinutes.Size = new Size(60, 22);
        _pnlEvery.Controls.Add(lblEvery);
        _pnlEvery.Controls.Add(_numEvery);
        _pnlEvery.Controls.Add(lblMinutes);

        _pnlWhen = new Panel { Dock = DockStyle.Top, Height = RowPanel, BackColor = Theme.Window, Margin = new Padding(0) };
        _lblNoDay = HintAt("день недели не нужен");
        _lblNoDay.Location = new Point(0, 7);
        _lblNoDay.Size = new Size(150, 22);
        _cmbDay.Location = new Point(0, 5);
        _numHour.Location = new Point(0, 5);
        _numMinute.Location = new Point(70, 5);
        _lblColon = HintCenteredAt(":");
        _lblColon.Location = new Point(58, 7);
        _lblColon.Size = new Size(10, 22);
        _pnlWhen.Controls.Add(_lblNoDay);
        _pnlWhen.Controls.Add(_cmbDay);
        _pnlWhen.Controls.Add(_numHour);
        _pnlWhen.Controls.Add(_lblColon);
        _pnlWhen.Controls.Add(_numMinute);

        var whenHolder = new Panel { Dock = DockStyle.Fill, Height = RowPanel, BackColor = Theme.Window, Margin = new Padding(0) };
        whenHolder.Controls.Add(_pnlEvery);
        whenHolder.Controls.Add(_pnlWhen);
        AddRow(table, "", whenHolder, null, RowPanel);

        // ── Параметры копирования ──
        AddRow(table, "", SectionTitle("Параметры копирования"), null, RowSection);

        _txtPath = new ModernTextBox { Dock = DockStyle.Fill, Height = 30, Margin = new Padding(0, 2, 0, 2), Placeholder = "общая папка" };
        AddRow(table, "Папка для копий", _txtPath, "как в общих настройках", RowField);

        _numKeep = new NumericUpDown { Minimum = 0, Maximum = 999, Value = 0, Size = new Size(76, 23), BackColor = Theme.Card, ForeColor = Theme.Text };
        AddRow(table, "Хранить копий", _numKeep, "0 — как в общих", RowSpin);

        _numSpeed = new NumericUpDown { Minimum = 0, Maximum = 100000, Value = 0, Size = new Size(76, 23), BackColor = Theme.Card, ForeColor = Theme.Text };
        AddRow(table, "Ограничение скорости", _numSpeed, "МБ/с, 0 — как в общих", RowSpin);

        _cmbMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3), BackColor = Theme.Card, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat };
        _cmbMode.Items.Add("как в общих настройках");
        _cmbMode.Items.Add("Горячий экспорт Hyper-V");
        _cmbMode.Items.Add("Контрольная точка + своё копирование");
        _cmbMode.Items.Add("Прямой экспорт Hyper-V");
        AddRow(table, "Режим копирования", _cmbMode, null, RowCombo);

        _chkProduction = TriState("как в общих");
        AddRow(table, "Точка Production (VSS)", _chkProduction, null, RowCheck);

        _chkKeepLocal = TriState("как в общих");
        AddRow(table, "Хранить локальную копию", _chkKeepLocal, null, RowCheck);

        _txtNetwork = new ModernTextBox { Dock = DockStyle.Fill, Height = 30, Margin = new Padding(0, 2, 0, 2), Placeholder = "общая сетевая папка" };
        AddRow(table, "Сетевая папка", _txtNetwork, "как в общих настройках", RowField);

        _txtFtpDir = new ModernTextBox { Dock = DockStyle.Fill, Height = 30, Margin = new Padding(0, 2, 0, 2), Placeholder = "общий каталог FTP" };
        AddRow(table, "Каталог FTP", _txtFtpDir, "как в общих настройках", RowField);

        scroll.Controls.Add(table);
        root.Controls.Add(scroll);

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.TextSecondary,
            Font = Theme.Small,
            Text = "Учётные данные сетевой папки и FTP, режим шифрования и срок хранения журнала — общие для всех машин."
        });

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
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
        var btnReset = new RoundedButton { Text = "Вернуть общие", Glyph = "refresh", Appearance = RoundedButton.ButtonKind.Ghost, Size = new Size(158, 34) };
        btnReset.Click += (_, _) => ResetToCommon();
        btnOk.Click += (_, _) => ApplyValues();
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(btnOk);
        buttons.Controls.Add(btnCancel);
        buttons.Controls.Add(btnReset);
        root.Controls.Add(buttons);

        Controls.Add(root);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    // ─────────────────────────────── Значения ───────────────────────────────

    private void LoadValues()
    {
        var v = _settings.PerVmSettings(_vmName);
        _chkOwn.Checked = v != null;

        var global = _settings.ForVm(_vmName);

        _txtPath.Text = v?.BackupRoot ?? "";
        _numKeep.Value = v?.KeepCopies ?? 0;
        _numSpeed.Value = v?.SpeedLimitMBps ?? 0;
        _cmbMode.SelectedIndex = v?.Mode switch
        {
            CopyMode.HotExport => 1,
            CopyMode.CheckpointThrottled => 2,
            CopyMode.VmExport => 3,
            _ => 0
        };
        _chkProduction.CheckState = v?.UseProductionCheckpoint switch
        {
            true => CheckState.Checked,
            false => CheckState.Unchecked,
            _ => CheckState.Indeterminate
        };
        _chkKeepLocal.CheckState = v?.KeepLocalCopy switch
        {
            true => CheckState.Checked,
            false => CheckState.Unchecked,
            _ => CheckState.Indeterminate
        };
        _txtNetwork.Text = v?.NetworkPath ?? "";
        _txtFtpDir.Text = v?.FtpRemoteDir ?? "";

        _chkAutoBackup.Checked = _settings.SelectedVms.Contains(_vmName, StringComparer.OrdinalIgnoreCase);

        _chkOwnSchedule.Checked = v?.UseOwnSchedule ?? false;
        _cmbKind.SelectedIndex = (v?.Schedule ?? global.Schedule) switch
        {
            ScheduleKind.EveryMinutes => 0,
            ScheduleKind.Weekly => 2,
            _ => 1
        };
        _numEvery.Value = Math.Clamp(v?.EveryMinutes ?? global.EveryMinutes, 1, 60 * 24 * 30);
        _numHour.Value = Math.Clamp(v?.Hour ?? global.Hour, 0, 23);
        _numMinute.Value = Math.Clamp(v?.Minute ?? global.Minute, 0, 59);
        _cmbDay.SelectedIndex = Math.Clamp(v?.WeeklyDay ?? global.WeeklyDay, 0, 6);
    }

    private void ApplyValues()
    {
        // Автобэкап: галочка в списке и переключатель в диалоге — одно и то же.
        var inSchedule = _settings.SelectedVms.Contains(_vmName, StringComparer.OrdinalIgnoreCase);
        if (_chkAutoBackup.Checked && !inSchedule) _settings.SelectedVms.Add(_vmName);
        if (!_chkAutoBackup.Checked && inSchedule) _settings.SelectedVms.Remove(_vmName);

        if (!_chkOwn.Checked)
        {
            _settings.SetPerVm(_vmName, null);
        }
        else
        {
            var v = _settings.PerVmSettings(_vmName) ?? new VmSettings();
            v.BackupRoot = _txtPath.Text.Trim();
            v.KeepCopies = _numKeep.Value > 0 ? (int)_numKeep.Value : null;
            v.SpeedLimitMBps = _numSpeed.Value > 0 ? (int)_numSpeed.Value : null;
            v.Mode = _cmbMode.SelectedIndex switch
            {
                1 => CopyMode.HotExport,
                2 => CopyMode.CheckpointThrottled,
                3 => CopyMode.VmExport,
                _ => null
            };
            v.UseProductionCheckpoint = _chkProduction.CheckState switch
            {
                CheckState.Checked => true,
                CheckState.Unchecked => false,
                _ => null
            };
            v.KeepLocalCopy = _chkKeepLocal.CheckState switch
            {
                CheckState.Checked => true,
                CheckState.Unchecked => false,
                _ => null
            };
            v.NetworkPath = _txtNetwork.Text.Trim();
            v.FtpRemoteDir = _txtFtpDir.Text.Trim();

            v.UseOwnSchedule = _chkOwnSchedule.Checked;
            v.Schedule = _cmbKind.SelectedIndex switch
            {
                0 => ScheduleKind.EveryMinutes,
                2 => ScheduleKind.Weekly,
                _ => ScheduleKind.Daily
            };
            v.EveryMinutes = (int)_numEvery.Value;
            v.Hour = (int)_numHour.Value;
            v.Minute = (int)_numMinute.Value;
            v.WeeklyDay = _cmbDay.SelectedIndex;

            _settings.SetPerVm(_vmName, v);
        }

        SettingsStore.Save(_settings);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ResetToCommon()
    {
        _chkOwn.Checked = false;
        _txtPath.Text = "";
        _numKeep.Value = 0;
        _numSpeed.Value = 0;
        _cmbMode.SelectedIndex = 0;
        _chkProduction.CheckState = CheckState.Indeterminate;
        _chkKeepLocal.CheckState = CheckState.Indeterminate;
        _txtNetwork.Text = "";
        _txtFtpDir.Text = "";
        _chkOwnSchedule.Checked = false;
        UpdateState();
    }

    // ─────────────────────────────── Состояние ───────────────────────────────

    private void UpdateState()
    {
        var own = _chkOwn.Checked;
        foreach (var c in new Control[] { _txtPath, _numKeep, _numSpeed, _cmbMode, _chkProduction, _chkKeepLocal, _txtNetwork, _txtFtpDir })
            c.Enabled = own;

        var sched = _chkOwnSchedule.Checked && _chkAutoBackup.Checked;
        _cmbKind.Enabled = sched;

        // Видимостью не управляем: у скрытого родителя Visible не работает надёжно.
        // Вместо этого панель получает нулевую высоту, а список дней — нулевой размер.
        var kind = _cmbKind.SelectedIndex;
        _pnlEvery.Height = kind == 0 ? RowPanel : 0;
        _pnlWhen.Height = kind == 0 ? 0 : RowPanel;
        _pnlEvery.Enabled = sched;
        _pnlWhen.Enabled = sched;

        var weekly = kind == 2;
        _cmbDay.Size = weekly ? new Size(150, 23) : Size.Empty;
        _lblNoDay.Size = weekly ? Size.Empty : new Size(150, 22);
        if (weekly)
        {
            _numHour.Location = new Point(156, 5);
            _numMinute.Location = new Point(226, 5);
            _lblColon.Location = new Point(214, 7);
        }
        else
        {
            _numHour.Location = new Point(0, 5);
            _numMinute.Location = new Point(70, 5);
            _lblColon.Location = new Point(58, 7);
        }
    }

    // ─────────────────────────────── Вспомогательное ───────────────────────────────

    private static TableLayoutPanel NewTable()
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = false,
            BackColor = Theme.Window
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelWidth));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ValueWidth));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    /// <summary>Добавляет строку: подпись | значение | пояснение.</summary>
    private static void AddRow(TableLayoutPanel table, string label, Control control, string? hint, int height)
    {
        var r = table.RowStyles.Count;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));

        if (!string.IsNullOrEmpty(label))
            table.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.Text,
                AutoSize = false,
                Margin = new Padding(0)
            }, 0, r);

        table.Controls.Add(control, 1, r);

        if (!string.IsNullOrEmpty(hint))
        {
            table.Controls.Add(new Label
            {
                Text = hint,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.TextSecondary,
                Font = Theme.Small,
                AutoSize = false,
                Margin = new Padding(6, 0, 0, 0)
            }, 2, r);
        }
        else
        {
            table.SetColumnSpan(control, 2);
        }
    }

    /// <summary>Пояснение внутри ячейки таблицы.</summary>
    private static Label Hint(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = false,
        Height = 24,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.TextSecondary,
        Font = Theme.Small,
        Margin = new Padding(0)
    };

    /// <summary>Пояснение с ручной расстановкой (внутри панели).</summary>
    private static Label HintAt(string text) => new()
    {
        Text = text,
        Dock = DockStyle.None,
        AutoSize = false,
        Size = new Size(120, 22),
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.TextSecondary,
        Font = Theme.Small,
        Margin = new Padding(0)
    };

    private static Label HintCenteredAt(string text) => new()
    {
        Text = text,
        Dock = DockStyle.None,
        AutoSize = false,
        Size = new Size(10, 22),
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Theme.TextSecondary,
        Margin = new Padding(0)
    };

    private static Label SectionTitle(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = false,
        Height = 26,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.TextSecondary,
        Font = Theme.BodyBold,
        Margin = new Padding(0)
    };

    private static CheckBox TriState(string hint) => new()
    {
        Text = hint,
        Dock = DockStyle.Fill,
        AutoSize = true,
        ThreeState = true,
        CheckState = CheckState.Indeterminate,
        ForeColor = Theme.TextSecondary,
        Margin = new Padding(0, 6, 0, 0)
    };
}
