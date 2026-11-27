using System.Diagnostics;
using HvBackup.Mcp;
using HvBackup.Ui;

namespace HvBackup;

public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;

    // Основное
    private ModernTextBox _txtPath = null!;
    private NumericUpDown _numKeep = null!;
    private NumericUpDown _numSpeed = null!;
    private NumericUpDown _numLogDays = null!;
    private ComboBox _cmbMode = null!;
    private ModernTextBox _txtExportTemp = null!;
    private CheckBox _chkProduction = null!;
    private CheckBox _chkRunOnStart = null!;
    private CheckBox _chkTray = null!;
    private CheckBox _chkAutoStart = null!;
    private CheckBox _chkKeepLocal = null!;

    // Сетевая папка
    private CheckBox _chkNetwork = null!;
    private ModernTextBox _txtNetworkPath = null!;
    private ModernTextBox _txtNetworkUser = null!;
    private ModernTextBox _txtNetworkPass = null!;

    // FTP
    private CheckBox _chkFtp = null!;
    private ModernTextBox _txtFtpHost = null!;
    private NumericUpDown _numFtpPort = null!;
    private ModernTextBox _txtFtpUser = null!;
    private ModernTextBox _txtFtpPass = null!;
    private ModernTextBox _txtFtpDir = null!;
    private ComboBox _cmbFtpTls = null!;
    private CheckBox _chkFtpPassive = null!;
    private CheckBox _chkFtpSkipCert = null!;

    // MCP-сервер
    private CheckBox _chkMcp = null!;
    private NumericUpDown _numMcpPort = null!;
    private Label _lblMcpStatus = null!;
    private RoundedButton _btnMcpToggle = null!;

    private readonly McpHost? _mcp;
    private ModernTabHost? _tabs;

    public SettingsForm(AppSettings settings, McpHost? mcp = null)
    {
        _settings = settings;
        _mcp = mcp;
        BuildUi();
        LoadValues();
    }

    /// <summary>Переключает вкладку окна настроек (для предпросмотра и проверки).</summary>
    public void SelectTab(int index) => _tabs?.Select(index);

    private void BuildUi()
    {
        Text = "Настройки — ГиперВ Резерв";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 580);
        Font = Theme.Body;
        BackColor = Theme.Window;

        var tabs = new ModernTabHost { Dock = DockStyle.Fill };
        _tabs = tabs;
        tabs.AddPage("Основное", BuildGeneralTab());
        tabs.AddPage("Сетевая папка", BuildNetworkTab());
        tabs.AddPage("FTP / FTPS", BuildFtpTab());
        tabs.AddPage("MCP-сервер", BuildMcpTab());

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(12, 8, 12, 8),
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
        var btnReset = new RoundedButton { Text = "По умолчанию", Glyph = "refresh", Appearance = RoundedButton.ButtonKind.Ghost, Size = new Size(146, 34) };
        btnReset.Click += (_, _) => ResetToDefaults();
        btnOk.Click += (_, _) => ApplyValues();
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(btnOk);
        buttons.Controls.Add(btnCancel);
        buttons.Controls.Add(btnReset);

        Controls.Add(tabs);
        Controls.Add(buttons);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    // ─────────────────────────────── Вкладка «Основное» ───────────────────────────────
    private Panel BuildGeneralTab()
    {
        var page = new Panel { Padding = new Padding(12, 4, 12, 12), BackColor = Theme.Window };
        var table = NewTable();

        AddRow(table, "Папка для копий", PathRow());

        _numKeep = new NumericUpDown { Minimum = 1, Maximum = 999, Value = 3, Width = 90, Margin = new Padding(0, 2, 0, 2), BackColor = Theme.Card, ForeColor = Theme.Text };
        AddRow(table, "Хранить копий (на ВМ)", _numKeep);

        _numLogDays = new NumericUpDown { Minimum = 0, Maximum = 3650, Value = 30, Width = 90, Margin = new Padding(0, 2, 6, 2), BackColor = Theme.Card, ForeColor = Theme.Text };
        var logBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), AutoSize = false };
        logBox.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        logBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        logBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        var logHint = new Label { Text = "0 — хранить все файлы журнала", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        logBox.Controls.Add(_numLogDays, 0, 0);
        logBox.Controls.Add(logHint, 1, 0);
        AddRow(table, "Хранить журнал, дней", logBox);

        _numSpeed = new NumericUpDown { Minimum = 0, Maximum = 100000, Value = 100, Width = 90, Margin = new Padding(0, 2, 6, 2), BackColor = Theme.Card, ForeColor = Theme.Text };
        var speedBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), AutoSize = false };
        speedBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        speedBox.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        speedBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var speedHint = new Label { Text = "0 — без ограничения", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        speedBox.Controls.Add(_numSpeed, 0, 0);
        speedBox.Controls.Add(speedHint, 1, 0);
        AddRow(table, "Ограничение скорости, МБ/с", speedBox);

        _cmbMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2), BackColor = Theme.Card, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat };
        _cmbMode.Items.Add("Горячий экспорт Hyper-V (полная конфигурация, перенос с ограничением скорости)");
        _cmbMode.Items.Add("Контрольная точка + своё копирование (с ограничением скорости)");
        _cmbMode.Items.Add("Прямой экспорт Hyper-V (быстро, без ограничения скорости)");
        AddRow(table, "Режим копирования", _cmbMode);

        _txtExportTemp = new ModernTextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2) };
        AddRow(table, "Папка временного экспорта", _txtExportTemp);
        AddRow(table, "", new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.TextSecondary,
            Text = "пусто — системная временная папка; нужно место ≈ объёму ВМ"
        });

        var scheduleText = ScheduleText.IsActive(_settings)
            ? ScheduleText.Describe(_settings)
            : "выключено";
        AddRow(table, "Расписание", new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ScheduleText.IsActive(_settings) ? Theme.Success : Theme.TextSecondary,
            Text = scheduleText + "   — меняется кнопкой «По расписанию» в главном окне"
        });

        _chkProduction = new CheckBox
        {
            Text = "На время копии временно ставить тип контрольной точки Production (VSS)",
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4)
        };
        AddRow(table, "", _chkProduction);

        _chkKeepLocal = new CheckBox
        {
            Text = "Хранить локальную копию после отправки в сеть/FTP",
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4)
        };
        AddRow(table, "", _chkKeepLocal);

        _chkRunOnStart = new CheckBox { Text = "Делать копию при запуске программы", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        AddRow(table, "", _chkRunOnStart);

        _chkTray = new CheckBox { Text = "Сворачивать в трей вместо закрытия", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        AddRow(table, "", _chkTray);

        _chkAutoStart = new CheckBox { Text = "Запускать программу при входе в Windows", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        AddRow(table, "", _chkAutoStart);

        AddRow(table, "", new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 70,
            ForeColor = Color.DimGray,
            Text = "В режиме «контрольная точка» виртуальная машина продолжает работать во время копии.\n" +
                   "Ограничение скорости действует на копирование дисков и на отправку в сеть/FTP;\n" +
                   "слияние разностного диска Hyper-V выполняет сам и лимитом не ограничивается."
        });

        page.Controls.Add(table);
        return page;
    }

    // ─────────────────────────── Вкладка «Сетевая папка» ───────────────────────────
    private Panel BuildNetworkTab()
    {
        var page = new Panel { Padding = new Padding(12, 4, 12, 12), BackColor = Theme.Window };
        var table = NewTable();

        _chkNetwork = new CheckBox { Text = "Копировать копии ещё и в сетевую папку (UNC)", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        AddRow(table, "", _chkNetwork);

        var netBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), AutoSize = false };
        netBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        netBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        netBox.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        _txtNetworkPath = new ModernTextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 6, 2) };
        var btnNetFolder = new RoundedButton { Text = "Обзор…", Dock = DockStyle.Fill, Height = 28, Appearance = RoundedButton.ButtonKind.Neutral };
        btnNetFolder.Click += (_, _) => BrowseNetworkPath();
        netBox.Controls.Add(_txtNetworkPath, 0, 0);
        netBox.Controls.Add(btnNetFolder, 1, 0);
        AddRow(table, "Папка (\\\\сервер\\папка)", netBox);

        _txtNetworkUser = new ModernTextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2) };
        AddRow(table, "Пользователь", _txtNetworkUser);

        _txtNetworkPass = new ModernTextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2), UseSystemPasswordChar = true };
        AddRow(table, "Пароль", _txtNetworkPass);

        var btnNetTest = new RoundedButton { Text = "Проверить подключение", Glyph = "shield", Width = 190, Height = 30 };
        btnNetTest.Click += async (_, _) => await TestNetworkAsync();
        AddRow(table, "", btnNetTest);

        AddRow(table, "", new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, Height = 90, ForeColor = Color.DimGray,
            Text = "Папка должна быть доступна пользователю, от имени которого запущена программа.\n" +
                   "Пароль (если нужен) хранится в зашифрованном виде средствами Windows (DPAPI).\n" +
                   "В сетевой папке создаётся та же структура, что и локально: <папка>\\<ВМ>\\<дата-время>\\,\n" +
                   "и там тоже работает ротация — лишние старые копии удаляются."
        });

        page.Controls.Add(table);
        return page;
    }

    // ─────────────────────────────── Вкладка «FTP» ───────────────────────────────
    private Panel BuildFtpTab()
    {
        var page = new Panel { Padding = new Padding(12, 4, 12, 12), BackColor = Theme.Window };
        var table = NewTable();

        _chkFtp = new CheckBox { Text = "Отправлять копии на FTP-сервер", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        AddRow(table, "", _chkFtp);

        _txtFtpHost = new ModernTextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2) };
        AddRow(table, "Сервер", _txtFtpHost);

        _numFtpPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 21, Width = 90, Margin = new Padding(0, 2, 0, 2) };
        AddRow(table, "Порт", _numFtpPort);

        _txtFtpUser = new ModernTextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2) };
        AddRow(table, "Пользователь", _txtFtpUser);

        _txtFtpPass = new ModernTextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2), UseSystemPasswordChar = true };
        AddRow(table, "Пароль", _txtFtpPass);

        _txtFtpDir = new ModernTextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2) };
        AddRow(table, "Каталог на сервере", _txtFtpDir);

        _cmbFtpTls = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2), BackColor = Theme.Card, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat };
        _cmbFtpTls.Items.AddRange(new object[] { "Без шифрования (FTP)", "FTPS сразу (AUTH TLS)", "FTPS implicit (обычно порт 990)" });
        AddRow(table, "Шифрование", _cmbFtpTls);

        _chkFtpPassive = new CheckBox { Text = "Пассивный режим (рекомендуется)", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        AddRow(table, "", _chkFtpPassive);

        _chkFtpSkipCert = new CheckBox { Text = "Не проверять TLS-сертификат сервера", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        AddRow(table, "", _chkFtpSkipCert);

        var btnFtpTest = new RoundedButton { Text = "Проверить подключение", Glyph = "shield", Width = 190, Height = 30 };
        btnFtpTest.Click += async (_, _) => await TestFtpAsync();
        AddRow(table, "", btnFtpTest);

        AddRow(table, "", new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, Height = 90, ForeColor = Color.DimGray,
            Text = "Структура на сервере повторяет локальную: <каталог>/<ВМ>/<дата-время>/,\n" +
                   "включая папку Disks. Ротация работает и на FTP: лишние старые копии удаляются.\n" +
                   "Пароль хранится в зашифрованном виде средствами Windows (DPAPI)."
        });

        page.Controls.Add(table);
        return page;
    }

    // ─────────────────────────── Вкладка «MCP-сервер» ───────────────────────────
    private Panel BuildMcpTab()
    {
        var page = new Panel { Padding = new Padding(12, 4, 12, 12), BackColor = Theme.Window };
        var table = NewTable();

        _chkMcp = new CheckBox
        {
            Text = "Включить встроенный MCP-сервер (управление копиями через ИИ-клиент)",
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4)
        };
        AddRow(table, "", _chkMcp);

        _numMcpPort = new NumericUpDown
        {
            Minimum = 1024, Maximum = 65535, Value = 8787, Width = 90,
            Margin = new Padding(0, 2, 6, 2), BackColor = Theme.Card, ForeColor = Theme.Text
        };
        var portBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), AutoSize = false };
        portBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        portBox.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        portBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var portHint = new Label { Text = "сервер слушает только 127.0.0.1", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        portBox.Controls.Add(_numMcpPort, 0, 0);
        portBox.Controls.Add(portHint, 1, 0);
        AddRow(table, "Порт", portBox);

        _lblMcpStatus = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, Height = 30,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.TextSecondary
        };
        AddRow(table, "Состояние", _lblMcpStatus);

        _btnMcpToggle = new RoundedButton { Text = "Включить сейчас", Glyph = "refresh", Width = 132, Height = 28, Margin = new Padding(0, 2, 8, 2) };
        _btnMcpToggle.Click += (_, _) => ToggleMcp();

        var btnCopy = new RoundedButton { Text = "Конфигурация клиента", Glyph = "copy", Width = 190, Height = 28, Margin = new Padding(0, 2, 8, 2) };
        btnCopy.Click += (_, _) => CopyMcpConfig();

        var btnOpen = new RoundedButton { Text = "Открыть Swagger", Glyph = "folder", Width = 160, Height = 28, Margin = new Padding(0, 2, 8, 2) };
        btnOpen.Click += (_, _) => OpenSwagger();

        var mcpButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = false, Height = 34,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0)
        };
        mcpButtons.Controls.Add(_btnMcpToggle);
        mcpButtons.Controls.Add(btnCopy);
        mcpButtons.Controls.Add(btnOpen);
        AddRow(table, "", mcpButtons, 34);

        AddRow(table, "", new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, Height = 132, ForeColor = Color.DimGray,
            Text = "Сервер встроен в программу и работает с правами администратора — операции с Hyper-V доступны.\n" +
                   "Адрес: http://127.0.0.1:порт/mcp — доступен только с этого компьютера, извне закрыт.\n" +
                   "ИИ-клиент (Claude Desktop, Cursor и другие) получает 14 инструментов: список машин и копий,\n" +
                   "копирование, удаление копии, расписание, восстановление, настройки, состояние и журнал.\n" +
                   "Кнопка «Включить сейчас» действует сразу. Чтобы сервер поднимался при запуске программы,\n" +
                   "включите его и нажмите «Сохранить»."
        });

        page.Controls.Add(table);
        return page;
    }

    /// <summary>Включение и выключение встроенного MCP-сервера прямо из окна настроек.</summary>
    private void ToggleMcp()
    {
        if (_mcp == null)
        {
            MessageBox.Show(this,
                "Сервер запускается только в работающей программе «ГиперВ Резерв».",
                "MCP-сервер", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_mcp.IsRunning)
        {
            _mcp.Stop();
            _chkMcp.Checked = false;
            AppLog.Info("MCP-сервер выключен пользователем.");
            UpdateMcpStatus();
            return;
        }

        var port = (int)_numMcpPort.Value;
        if (_mcp.Start(port, out var error))
        {
            _chkMcp.Checked = true;
            _numMcpPort.Value = port;
            AppLog.Info("MCP-сервер включён: " + _mcp.McpUrl);
        }
        else
        {
            MessageBox.Show(this, "Не удалось запустить MCP-сервер: " + error,
                "MCP-сервер", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        UpdateMcpStatus();
    }

    /// <summary>Обновляет строку состояния сервера и текст кнопки.</summary>
    private void UpdateMcpStatus()
    {
        if (_lblMcpStatus == null) return;

        var running = _mcp?.IsRunning == true;

        _lblMcpStatus.Text = _mcp == null
            ? "сервер запускается в работающей программе"
            : running ? "работает: " + _mcp.McpUrl : "выключен";
        _lblMcpStatus.ForeColor = running ? Theme.Success : Theme.TextSecondary;

        if (_btnMcpToggle != null)
        {
            _btnMcpToggle.Text = running ? "Выключить сейчас" : "Включить сейчас";
            _btnMcpToggle.Glyph = running ? "stop" : "refresh";
        }
    }

    /// <summary>Копирует в буфер обмена готовую конфигурацию MCP-клиента.</summary>
    private void CopyMcpConfig()
    {
        try
        {
            var config = _mcp?.ClientConfigJson() ?? ClientConfig((int)_numMcpPort.Value);
            Clipboard.SetText(config);
            MessageBox.Show(this,
                "Конфигурация скопирована в буфер обмена. Вставьте её в файл настроек MCP-клиента:\n\n" + config,
                "MCP-сервер", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось скопировать конфигурацию: " + ex.Message,
                "MCP-сервер", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Открывает документацию Swagger в браузере.</summary>
    private void OpenSwagger()
    {
        try
        {
            var url = _mcp?.IsRunning == true
                ? _mcp.SwaggerUrl
                : "http://127.0.0.1:" + (int)_numMcpPort.Value + "/swagger";
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось открыть браузер: " + ex.Message,
                "MCP-сервер", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Конфигурация клиента для указанного порта (используется, когда сервер не запущен).</summary>
    internal static string ClientConfig(int port)
    {
        var config = new System.Text.Json.Nodes.JsonObject
        {
            ["mcpServers"] = new System.Text.Json.Nodes.JsonObject
            {
                ["hvbackup"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "http",
                    ["url"] = "http://127.0.0.1:" + port + "/mcp"
                }
            }
        };

        return config.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    // ─────────────────────────────── Вспомогательное ───────────────────────────────
    private static TableLayoutPanel NewTable()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            BackColor = Theme.Window
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static void AddRow(TableLayoutPanel table, string label, Control control, int? height = null)
    {
        var r = table.RowStyles.Count;
        var rowHeight = height ?? (control is Label { AutoSize: false } l ? l.Height : 30);
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
        if (!string.IsNullOrEmpty(label))
            table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, r);
        table.Controls.Add(control, 1, r);
    }

    private Control PathRow()
    {
        var box = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), AutoSize = false };
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        box.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _txtPath = new ModernTextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 6, 2) };
        var btn = new RoundedButton { Text = "Обзор…", Dock = DockStyle.Fill, Appearance = RoundedButton.ButtonKind.Neutral };
        btn.Click += (_, _) => BrowseLocalPath();
        box.Controls.Add(_txtPath, 0, 0);
        box.Controls.Add(btn, 1, 0);
        return box;
    }

    private void BrowseLocalPath()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Папка для резервных копий",
            SelectedPath = _txtPath.Text,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _txtPath.Text = dlg.SelectedPath;
    }

    private void BrowseNetworkPath()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Сетевая папка для резервных копий",
            SelectedPath = _txtNetworkPath.Text,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _txtNetworkPath.Text = dlg.SelectedPath;
    }

    private async Task TestNetworkAsync()
    {
        var path = _txtNetworkPath.Text.Trim();
        if (path.Length == 0)
        {
            MessageBox.Show(this, "Укажите сетевую папку.", "Проверка", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        UseWaitCursor = true;
        try
        {
            var (ok, err) = NetworkShare.Connect(path, _txtNetworkUser.Text.Trim(), _txtNetworkPass.Text);
            if (!ok)
            {
                MessageBox.Show(this, "Не удалось подключиться: " + err, "Проверка сетевой папки",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var probe = Path.Combine(path, ".hvbackup-probe");
            try
            {
                Directory.CreateDirectory(probe);
                File.WriteAllText(Path.Combine(probe, "test.txt"), "ok");
                Directory.Delete(probe, true);
                MessageBox.Show(this, "Подключение работает, папка доступна для записи.", "Проверка сетевой папки",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Подключиться удалось, но запись не удалась: " + ex.Message,
                    "Проверка сетевой папки", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                NetworkShare.Disconnect(path);
            }
        }
        finally
        {
            UseWaitCursor = false;
        }
        await Task.CompletedTask;
    }

    private async Task TestFtpAsync()
    {
        var host = _txtFtpHost.Text.Trim();
        if (host.Length == 0)
        {
            MessageBox.Show(this, "Укажите адрес FTP-сервера.", "Проверка", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        UseWaitCursor = true;
        try
        {
            using var ftp = new FtpClient(host, (int)_numFtpPort.Value, _txtFtpUser.Text, _txtFtpPass.Text,
                _cmbFtpTls.SelectedIndex == 1, _cmbFtpTls.SelectedIndex == 2)
            {
                Passive = _chkFtpPassive.Checked,
                SkipCertificateValidation = _chkFtpSkipCert.Checked
            };
            await ftp.ConnectAsync();
            var dir = _txtFtpDir.Text.Trim();
            if (dir.Length > 0) await ftp.EnsureDirectoryAsync(dir);
            MessageBox.Show(this, "Подключение к FTP-серверу установлено, каталог доступен.",
                "Проверка FTP", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось подключиться: " + ex.Message, "Проверка FTP",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void LoadValues()
    {
        _txtPath.Text = _settings.BackupRoot;
        _numKeep.Value = Math.Clamp(_settings.KeepCopies, 1, 999);
        _numLogDays.Value = Math.Clamp(_settings.LogRetentionDays, 0, 3650);
        _numSpeed.Value = Math.Clamp(_settings.SpeedLimitMBps, 0, 100000);
        _cmbMode.SelectedIndex = _settings.Mode switch
        {
            CopyMode.HotExport => 0,
            CopyMode.CheckpointThrottled => 1,
            _ => 2
        };
        _txtExportTemp.Text = _settings.ExportTempRoot;
        _chkProduction.Checked = _settings.UseProductionCheckpoint;
        _chkRunOnStart.Checked = _settings.RunOnStart;
        _chkTray.Checked = _settings.MinimizeToTray;
        _chkAutoStart.Checked = _settings.AutoStart;
        _chkKeepLocal.Checked = _settings.KeepLocalCopy;

        _chkNetwork.Checked = _settings.NetworkEnabled;
        _txtNetworkPath.Text = _settings.NetworkPath;
        _txtNetworkUser.Text = _settings.NetworkUser;
        _txtNetworkPass.Text = _settings.NetworkPassword;

        _chkFtp.Checked = _settings.FtpEnabled;
        _txtFtpHost.Text = _settings.FtpHost;
        _numFtpPort.Value = Math.Clamp(_settings.FtpPort, 1, 65535);
        _txtFtpUser.Text = _settings.FtpUser;
        _txtFtpPass.Text = _settings.FtpPassword;
        _txtFtpDir.Text = _settings.FtpRemoteDir;
        _cmbFtpTls.SelectedIndex = Math.Clamp(_settings.FtpTlsMode, 0, 2);
        _chkFtpPassive.Checked = _settings.FtpPassive;
        _chkFtpSkipCert.Checked = _settings.FtpSkipCertificateValidation;

        _chkMcp.Checked = _settings.McpEnabled;
        _numMcpPort.Value = Math.Clamp(_settings.McpPort, 1024, 65535);
        UpdateMcpStatus();
    }

    private void ApplyValues()
    {
        _settings.BackupRoot = string.IsNullOrWhiteSpace(_txtPath.Text) ? @"D:\Backups\Hyper-V" : _txtPath.Text.Trim();
        _settings.KeepCopies = (int)_numKeep.Value;
        _settings.LogRetentionDays = (int)_numLogDays.Value;
        _settings.SpeedLimitMBps = (int)_numSpeed.Value;
        _settings.Mode = _cmbMode.SelectedIndex switch
        {
            0 => CopyMode.HotExport,
            1 => CopyMode.CheckpointThrottled,
            _ => CopyMode.VmExport
        };
        _settings.ExportTempRoot = _txtExportTemp.Text.Trim();
        _settings.UseProductionCheckpoint = _chkProduction.Checked;
        _settings.RunOnStart = _chkRunOnStart.Checked;
        _settings.MinimizeToTray = _chkTray.Checked;
        _settings.AutoStart = _chkAutoStart.Checked;
        _settings.KeepLocalCopy = _chkKeepLocal.Checked;

        _settings.NetworkEnabled = _chkNetwork.Checked;
        _settings.NetworkPath = _txtNetworkPath.Text.Trim();
        _settings.NetworkUser = _txtNetworkUser.Text.Trim();
        _settings.NetworkPasswordProtected = Dpapi.Protect(_txtNetworkPass.Text);

        _settings.FtpEnabled = _chkFtp.Checked;
        _settings.FtpHost = _txtFtpHost.Text.Trim();
        _settings.FtpPort = (int)_numFtpPort.Value;
        _settings.FtpUser = _txtFtpUser.Text;
        _settings.FtpRemoteDir = _txtFtpDir.Text.Trim();
        _settings.FtpPasswordProtected = Dpapi.Protect(_txtFtpPass.Text);
        _settings.FtpTlsMode = _cmbFtpTls.SelectedIndex;
        _settings.FtpPassive = _chkFtpPassive.Checked;
        _settings.FtpSkipCertificateValidation = _chkFtpSkipCert.Checked;

        _settings.McpEnabled = _chkMcp.Checked;
        _settings.McpPort = (int)_numMcpPort.Value;
    }

    private void ResetToDefaults()
    {
        _txtPath.Text = @"D:\Backups\Hyper-V";
        _numKeep.Value = 3;
        _numLogDays.Value = 30;
        _numSpeed.Value = 100;
        _cmbMode.SelectedIndex = 0;
        _txtExportTemp.Text = "";
        _chkProduction.Checked = true;
        _chkRunOnStart.Checked = false;
        _chkTray.Checked = true;
        _chkAutoStart.Checked = false;
        _chkKeepLocal.Checked = true;
        _chkNetwork.Checked = false;
        _chkFtp.Checked = false;
        _cmbFtpTls.SelectedIndex = 0;
        _chkFtpPassive.Checked = true;
        _chkFtpSkipCert.Checked = false;
        _chkMcp.Checked = false;
        _numMcpPort.Value = 8787;
    }
}
