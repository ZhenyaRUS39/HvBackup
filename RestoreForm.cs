using System.Drawing.Drawing2D;
using HvBackup.Ui;

namespace HvBackup;

/// <summary>
/// Диалог восстановления виртуальной машины из найденной копии.
/// <para>
/// Раскладка «плоская»: одна таблица с колонками (подпись, значение, пояснение)
/// и панели с ручной расстановкой элементов. Вложенных TableLayoutPanel нет —
/// в WinForms они раскладываются непредсказуемо.
/// </para>
/// </summary>
public sealed class RestoreForm : Form
{
    private const int LabelWidth = 168;
    private const int ValueWidth = 330;

    /// <summary>Колонка с маленьким красным ведром — удалить конкретную копию.</summary>
    private const int ColTrash = 4;

    private readonly AppSettings _settings;
    private readonly List<BackupCopyInfo> _copies = new();

    private IconListView _lv = null!;
    private Bitmap _imgTrash = null!;
    private Label _lblDetails = null!;
    private Label _lblPhase = null!;
    private Label _lblStatus = null!;
    private Label _lblHintDest = null!;

    private ModernTextBox _txtName = null!;
    private ModernTextBox _txtDest = null!;

    private CheckBox _chkNewId = null!;
    private CheckBox _chkStart = null!;

    private RoundedProgressBar _pb = null!;
    private RoundedButton _btnRestore = null!;
    private RoundedButton _btnClose = null!;
    private RoundedButton _btnRefresh = null!;
    private RoundedButton _btnDeleteAll = null!;

    /// <summary>Подсказки к значкам без подписи.</summary>
    private ToolTip _tips = null!;

    /// <summary>Целевые папки подставляются автоматически — их можно поменять вручную.</summary>
    private bool _destAuto = true;

    private bool _busy;
    private bool _closed;
    private CancellationTokenSource? _cts;

    /// <summary>Имя, подставленное в поле при выборе копии (чтобы не затирать правку пользователя).</summary>
    private string _lastSuggestedName = "";

    /// <summary>Машины, выбранные в главном окне: для них показываем копии сразу.</summary>
    private readonly HashSet<string> _filter = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Заголовок списка копий — зависит от того, выбрана ли машина.</summary>
    private Label _lblListTitle = null!;

    public RestoreForm(AppSettings settings, IReadOnlyList<string>? vmNames = null)
    {
        _settings = settings;

        if (vmNames != null)
            foreach (var n in vmNames)
                if (!string.IsNullOrWhiteSpace(n)) _filter.Add(n.Trim());

        BuildUi();
        ReloadCopies();
    }

    // ─────────────────────────────── Интерфейс ───────────────────────────────

    private void BuildUi()
    {
        Text = "Восстановление виртуальной машины";
        MinimumSize = new Size(880, 620);
        ClientSize = new Size(940, 700);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Window;
        Font = Theme.Body;

        _tips = new ToolTip();

        var options = BuildOptions();
        var progress = BuildProgress();
        var buttons = BuildButtons();
        var list = BuildListCard();
        var header = BuildHeader();

        Controls.Add(options);      // Fill — раскладывается последним
        Controls.Add(progress);     // Bottom
        Controls.Add(buttons);      // Bottom
        Controls.Add(list);         // Top
        Controls.Add(header);       // Top

        FormClosing += (_, e) =>
        {
            if (_busy)
            {
                var answer = MessageBox.Show(this,
                    "Восстановление ещё выполняется. Прервать его?\n" +
                    "Прерванная копия дисков может остаться в целевой папке.",
                    "ГиперВ Резерв", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cts?.Cancel();
            }
            _closed = true;
        };
    }

    private Panel BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Theme.Window };

        panel.Controls.Add(new Label
        {
            Text = "Восстановление из копии",
            Font = Theme.Title,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(2, 0)
        });
        panel.Controls.Add(new Label
        {
            Text = "выберите копию, задайте имя машины и целевые папки — программа восстановит её средствами Hyper-V",
            Font = Theme.Subtitle,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Location = new Point(4, 28)
        });

        return panel;
    }

    private Panel BuildListCard()
    {
        var card = new RoundedPanel { Dock = DockStyle.Top, Height = 244, Padding = new Padding(12, 8, 12, 8) };

        var head = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Theme.Card };
        _lblListTitle = new Label
        {
            Text = "Доступные копии",
            Font = Theme.BodyBold,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(2, 0)
        };
        head.Controls.Add(_lblListTitle);
        head.Controls.Add(new Label
        {
            Text = "найдены в общей папке копий и в индивидуальных папках машин",
            Font = Theme.Small,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Location = new Point(2, 18)
        });

        _btnRefresh = new RoundedButton { Text = "Обновить", Glyph = "refresh", Size = new Size(108, 28) };
        _btnRefresh.Click += (_, _) => ReloadCopies();

        head.Controls.Add(_btnRefresh);
        head.Resize += (_, _) =>
        {
            _btnRefresh.Location = new Point(head.Width - _btnRefresh.Width - 2, 4);
        };
        _tips.SetToolTip(_btnRefresh, "Перечитать папки копий");

        _lblDetails = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            Font = Theme.Small,
            ForeColor = Theme.TextSecondary,
            AutoEllipsis = true
        };

        _imgTrash = TrashIcon();

        _lv = new IconListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            GridLines = false,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            RowIcon = _imgTrash,
            IconColumn = ColTrash
        };
        _lv.Columns.Add("Виртуальная машина", 190);
        _lv.Columns.Add("Дата копии", 130);
        _lv.Columns.Add("Тип", 140);
        _lv.Columns.Add("Размер", 90);
        _lv.Columns.Add("", 34);      // маленькое красное ведро — удалить эту копию
        _lv.SelectedIndexChanged += (_, _) => OnCopySelected();
        _lv.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete) return;
            e.Handled = true;
            DeleteSelectedCopy();
        };
        _lv.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            try
            {
                // клик по маленькому красному ведру — удалить именно эту копию
                if (!_lv.HitIcon(e.Location)) return;
                var hit = _lv.HitTest(e.Location);
                if (hit.Item == null) return;
                BeginInvoke(new Action(() => DeleteCopyOf(hit.Item.Tag as BackupCopyInfo)));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Ошибка удаления копии из списка: " + ex.Message);
            }
        };

        card.Controls.Add(_lv);
        card.Controls.Add(_lblDetails);
        card.Controls.Add(head);
        return card;
    }

    private Panel BuildOptions()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window, Padding = new Padding(2, 10, 2, 0), AutoScroll = true };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            AutoSize = true,
            BackColor = Theme.Window
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelWidth));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ValueWidth));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(table, "", SectionTitle("Параметры восстановления"), null, 26);

        _txtName = new ModernTextBox { Dock = DockStyle.Fill, Height = 30, Margin = new Padding(0, 2, 0, 2), Placeholder = "имя машины" };
        _txtName.TextChanged += (_, _) => OnNameChanged();
        AddRow(table, "Имя машины", _txtName, Hint("как в копии"), 34);

        _lblHintDest = Hint("диски и конфигурация — в эту папку");
        var destField = BuildPathField(out _txtDest, out var btnDest);
        _txtDest.TextChanged += (_, _) => OnDestChanged();
        btnDest.Click += (_, _) => BrowseFolder(_txtDest, "Папка для восстановления");
        AddRow(table, "Папка восстановления", destField, _lblHintDest, 34);

        _chkNewId = new CheckBox
        {
            Text = "Создать машину с новым идентификатором (исходная ВМ может остаться на месте)",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Checked = true,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 4, 0, 0)
        };
        AddRow(table, "", _chkNewId, null, 26);

        _chkStart = new CheckBox
        {
            Text = "Запустить машину после восстановления",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 4, 0, 0)
        };
        AddRow(table, "", _chkStart, null, 26);

        AddRow(table, "", Hint(
            "Экспорт Hyper-V: диски и конфигурация копируются в указанные папки. " +
            "Копия-контрольная точка: запускается готовый restore.ps1 из папки копии."), null, 40);
        host.Controls.Add(table);
        return host;
    }

    private Panel BuildProgress()
    {
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = Theme.Window };

        _lblPhase = new Label
        {
            Text = "Готов к работе",
            Font = Theme.BodyBold,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(2, 2)
        };
        _lblStatus = new Label
        {
            Text = "",
            Font = Theme.Small,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Location = new Point(2, 24)
        };

        _pb = new RoundedProgressBar { BarHeight = 8, Location = new Point(2, 40), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        panel.Resize += (_, _) => _pb.Width = Math.Max(50, panel.Width - 4);

        panel.Controls.Add(_lblPhase);
        panel.Controls.Add(_lblStatus);
        panel.Controls.Add(_pb);
        return panel;
    }

    private Panel BuildButtons()
    {
        // Высота панели 60: внутри (8 px сверху и снизу) остаётся 44 px —
        // кнопки по 40 px помещаются целиком, иначе низ срезается.
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.Window, Padding = new Padding(0, 8, 0, 8) };

        _btnRestore = new RoundedButton
        {
            Text = "Восстановить",
            Glyph = "restore",
            Appearance = RoundedButton.ButtonKind.Success,
            Pill = true,
            Bold = true,
            Size = new Size(190, 40),
            Font = Theme.BodyBold,
            Margin = new Padding(0, 2, 8, 2)
        };
        _btnRestore.Click += (_, _) => _ = RestoreAsync();

        _btnClose = new RoundedButton
        {
            Text = "Закрыть",
            Size = new Size(130, 40),
            Margin = new Padding(0, 2, 0, 2)
        };
        _btnClose.Click += (_, _) => Close();

        // Массовое удаление осталось отдельной кнопкой без значка ведра:
        // единственные корзины в окне — маленькие красные у каждой копии.
        _btnDeleteAll = new RoundedButton
        {
            Text = "Удалить все копии",
            Size = new Size(180, 40),
            Enabled = false,
            Margin = new Padding(0, 2, 0, 2)
        };
        _btnDeleteAll.Click += (_, _) => DeleteAllCopies();

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Width = 340,
            Height = 44,
            BackColor = Theme.Window,
            Padding = new Padding(0, 0, 0, 0)
        };
        actions.Controls.Add(_btnClose);
        actions.Controls.Add(_btnRestore);

        var remove = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Width = 200,
            Height = 44,
            BackColor = Theme.Window,
            Padding = new Padding(0, 0, 0, 0)
        };
        remove.Controls.Add(_btnDeleteAll);

        bar.Controls.Add(actions);
        bar.Controls.Add(remove);
        return bar;
    }

    // ─────────────────────────────── Список копий ───────────────────────────────

    /// <summary>Перечитывает папки копий и заполняет список.</summary>
    private void ReloadCopies()
    {
        var selected = SelectedCopy()?.Folder ?? "";

        _copies.Clear();
        try
        {
            _copies.AddRange(RestoreEngine.Scan(_settings));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось найти копии: " + ex.Message);
            _lblStatus.Text = "Не удалось прочитать папки копий: " + ex.Message;
        }

        FillList(selected);
    }

    /// <summary>Заполняет список найденными копиями.</summary>
    private void FillList(string selectedFolder = "")
    {
        // Показываем копии выбранных в главном окне машин (если машины выбраны).
        var shown = FilterCopies(_copies, _filter);
        _shown.Clear();
        _shown.AddRange(shown);

        _lblListTitle.Text = _filter.Count == 1
            ? "Копии машины «" + _filter.First() + "»"
            : _filter.Count > 1
                ? $"Копии машин ({_filter.Count} выбрано)"
                : "Доступные копии";

        _lv.BeginUpdate();
        try
        {
            _lv.Items.Clear();
            foreach (var copy in shown)
            {
                var item = new ListViewItem(copy.VmName) { Tag = copy };
                item.SubItems.Add(copy.CreatedText);
                item.SubItems.Add(copy.KindText);
                item.SubItems.Add(copy.SizeText);
                item.SubItems.Add("");                          // маленькое красное ведро (рисуется значком)
                _lv.Items.Add(item);

                if (string.Equals(copy.Folder, selectedFolder, StringComparison.OrdinalIgnoreCase))
                    item.Selected = true;
            }
        }
        finally
        {
            _lv.EndUpdate();
        }

        if (_lv.Items.Count == 0)
        {
            _lblDetails.Text = _filter.Count > 0
                ? "У выбранных машин копий не найдено. Проверьте папку копий в настройках."
                : "Копии не найдены. Проверьте папку копий в настройках.";
            _lblPhase.Text = "Нет доступных копий";
            SetFieldsEnabled(false);
            return;
        }

        if (_lv.SelectedItems.Count == 0) _lv.Items[0].Selected = true;
        _lblDetails.Text = shown.Count + " копий найдено.";
        _lblPhase.Text = "Готов к работе";
        UpdateDeleteButtons();
        OnCopySelected();   // сведения о выбранной копии
    }

    /// <summary>Копии, показанные в списке (с учётом фильтра по машине).</summary>
    private readonly List<BackupCopyInfo> _shown = new();

    private void UpdateDeleteButtons()
    {
        var can = _lv.Items.Count > 0 && !_busy;
        _btnDeleteAll.Enabled = can;
    }

    /// <summary>Удаляет выбранную копию (после подтверждения).</summary>
    private void DeleteSelectedCopy()
    {
        if (_busy) return;
        DeleteCopyOf(SelectedCopy());
    }

    /// <summary>
    /// Удаляет конкретную копию — по маленькому красному ведру в её строке
    /// или по клавише Del. Копия удаляется только после подтверждения.
    /// </summary>
    private void DeleteCopyOf(BackupCopyInfo? copy)
    {
        if (_busy || copy == null) return;

        var answer = MessageBox.Show(this,
            "Удалить копию?\n\n" +
            "Машина: " + copy.VmName + "\n" +
            "Дата копии: " + copy.CreatedText + "\n" +
            "Объём: " + copy.SizeText + "\n" +
            "Папка: " + copy.Folder + "\n\n" +
            "Копию будет нельзя восстановить. Действие необратимо.",
            "ГиперВ Резерв", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        if (!RestoreEngine.DeleteCopy(copy.Folder, msg => Log(msg)))
        {
            MessageBox.Show(this, "Не удалось удалить копию. Подробности — в журнале.",
                "ГиперВ Резерв", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        AppLog.Info("Копия удалена вручную: " + copy.Folder);
        ReloadCopies();
    }

    /// <summary>Удаляет все показанные копии (после подтверждения).</summary>
    private void DeleteAllCopies()
    {
        if (_busy || _shown.Count == 0) return;

        var names = _shown.Select(c => c.VmName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var total = _shown.Sum(c => c.SizeBytes);
        var whose = names.Count == 1 ? "машины «" + names[0] + "»" : "выбранных машин (" + names.Count + ")";

        var answer = MessageBox.Show(this,
            "Удалить все копии " + whose + "?\n\n" +
            "Копий: " + _shown.Count + ", общий объём: " + ThrottledCopier.FormatBytes(total) + "\n\n" +
            "Копии будут удалены безвозвратно — восстановить из них машину уже не получится.",
            "ГиперВ Резерв", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        var failed = 0;
        foreach (var copy in _shown)
        {
            if (!RestoreEngine.DeleteCopy(copy.Folder, msg => Log(msg))) failed++;
        }

        AppLog.Info($"Удаление копий по запросу: всего {_shown.Count}, с ошибкой {failed}.");
        ReloadCopies();

        if (failed > 0)
            MessageBox.Show(this, "Часть копий удалить не удалось. Подробности — в журнале.",
                "ГиперВ Резерв", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private BackupCopyInfo? SelectedCopy()
    {
        if (_lv.SelectedItems.Count == 0) return null;
        return _lv.SelectedItems[0].Tag as BackupCopyInfo;
    }

    /// <summary>
    /// Оставляет копии выбранных машин. Если машины не выбраны — возвращает все копии
    /// (так можно восстановить и машину, которой больше нет в Hyper-V).
    /// </summary>
    internal static List<BackupCopyInfo> FilterCopies(
        List<BackupCopyInfo> copies, IEnumerable<string> vmNames)
    {
        var names = new HashSet<string>(
            vmNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
            StringComparer.OrdinalIgnoreCase);

        if (names.Count == 0) return copies;
        return copies.Where(c => names.Contains(c.VmName)).ToList();
    }

    /// <summary>Показывает сведения о выбранной копии и подставляет имя машины.</summary>
    private void OnCopySelected()
    {
        var copy = SelectedCopy();
        if (copy == null)
        {
            _lblDetails.Text = "";
            return;
        }

        var consistency = string.IsNullOrWhiteSpace(copy.Consistency) ? "не указана" : copy.Consistency;
        _lblDetails.Text = "Папка копии: " + copy.Folder + Environment.NewLine +
                           "Тип: " + copy.KindText + " · объём: " + copy.SizeText + " · консистентность: " + consistency;

        if (_txtName.Text.Trim().Length == 0 || _txtName.Text.Trim() == _lastSuggestedName)
            _txtName.Text = copy.VmName;

        _lastSuggestedName = copy.VmName;
        UpdateForCopy(copy);
    }

    /// <summary>Меняет подписи и доступность полей под тип копии.</summary>
    private void UpdateForCopy(BackupCopyInfo copy)
    {
        var checkpoint = !copy.IsExport;

        _chkNewId.Enabled = !checkpoint && !_busy;
        _chkNewId.Checked = _chkNewId.Enabled && _chkNewId.Checked;

        _lblHintDest.Text = checkpoint
            ? "сюда — машина и её диски (обязательно)"
            : "сюда — диски и конфигурация машины";
    }

    /// <summary>Имя машины изменилось — пересчитываем целевую папку.</summary>
    private void OnNameChanged()
    {
        if (!_destAuto) return;
        ApplyAutoDestination(_txtName.Text.Trim());
    }

    /// <summary>
    /// Подставляет целевую папку по умолчанию: одна папка на машину, внутри неё
    /// программа сама разложит диски и конфигурацию.
    /// </summary>
    private void ApplyAutoDestination(string vmName)
    {
        if (_hostVhdPath.Length == 0) return;

        var safe = string.IsNullOrWhiteSpace(vmName) ? "VM" : BackupEngine.SafeName(vmName);
        _settingDest = true;
        try { _txtDest.Text = Path.Combine(_hostVhdPath, "HvRestore", safe); }
        finally { _settingDest = false; }
    }

    /// <summary>Папку задал пользователь — больше не пересчитываем её автоматически.</summary>
    private void OnDestChanged()
    {
        if (_settingDest) return;
        _destAuto = false;
    }

    private bool _settingDest;

    private string _hostVhdPath = "";

    // ─────────────────────────────── Восстановление ───────────────────────────────

    private void BrowseFolder(ModernTextBox box, string title)
    {
        try
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = title,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };
            if (box.Text.Trim().Length > 0 && Directory.Exists(box.Text.Trim())) dlg.SelectedPath = box.Text.Trim();
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                box.Text = dlg.SelectedPath;
                _destAuto = false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось открыть выбор папки: " + ex.Message);
        }
    }

    private async Task RestoreAsync()
    {
        if (_busy) return;

        var copy = SelectedCopy();
        if (copy == null)
        {
            MessageBox.Show(this, "Выберите копию для восстановления.", "ГиперВ Резерв",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var name = _txtName.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Укажите имя восстанавливаемой машины.", "ГиперВ Резерв",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _txtName.Focus();
            return;
        }

        var dest = _txtDest.Text.Trim();
        if (dest.Length == 0)
        {
            MessageBox.Show(this,
                "Укажите папку, куда будет восстановлена машина.",
                "ГиперВ Резерв", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _txtDest.Focus();
            return;
        }

        // Одна папка на машину: внутри неё программа сама разложит диски и конфигурацию.
        var (vhdPath, vmPath) = RestoreEngine.SplitDestination(dest, copy.IsExport);
        var options = new RestoreOptions
        {
            VmName = name,
            VhdPath = vhdPath,
            VmPath = vmPath,
            GenerateNewId = _chkNewId.Checked,
            StartAfter = _chkStart.Checked
        };

        // Папка назначения должна быть свободна — иначе Hyper-V не сможет скопировать диски.
        var free = RestoreEngine.UniqueFolder(dest);
        if (!string.Equals(free, dest, StringComparison.OrdinalIgnoreCase))
        {
            Log("Папка занята — выбрана свободная: " + free);
            dest = free;
            (vhdPath, vmPath) = RestoreEngine.SplitDestination(dest, copy.IsExport);
            options.VhdPath = vhdPath;
            options.VmPath = vmPath;
        }

        _busy = true;
        _cts = new CancellationTokenSource();
        SetFieldsEnabled(false);
        _btnRestore.Enabled = false;
        _btnRestore.Text = "Восстановление…";
        _pb.SetIndeterminate();
        _lblPhase.Text = "Восстановление: подготовка";
        _lblStatus.Text = "";

        var result = new RestoreResult();
        try
        {
            result = await RestoreEngine.RestoreAsync(copy, options,
                msg => Log(msg), p => PostProgress(p), _cts.Token);
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка восстановления: " + ex.Message);
            result = new RestoreResult { Ok = false, Error = ex.Message };
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _busy = false;
            _btnRestore.Enabled = true;
            _btnRestore.Text = "Восстановить";
            SetFieldsEnabled(true);
        }

        if (result.Ok)
        {
            _pb.SetProgress(1, 1);
            _lblPhase.Text = "Восстановление завершено";
            _lblStatus.Text = "Машина «" + result.VmName + "» восстановлена.";
            AppLog.Info($"Восстановление завершено: машина «{result.VmName}».");
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            _pb.Reset();
            _lblPhase.Text = "Восстановление не удалось";
            _lblStatus.Text = result.Error;
            MessageBox.Show(this, result.Error, "ГиперВ Резерв",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Log(string message)
    {
        AppLog.Info(message);
        if (_closed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(new Action(() => _lblStatus.Text = message));
            else _lblStatus.Text = message;
        }
        catch { }
    }

    /// <summary>Прогресс приходит из фонового потока — переносим его на поток интерфейса.</summary>
    private void PostProgress(BackupProgress progress)
    {
        if (_closed || IsDisposed) return;
        try
        {
            if (!IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(new Action(() => ApplyProgress(progress)));
            else ApplyProgress(progress);
        }
        catch { }
    }

    private void ApplyProgress(BackupProgress p)
    {
        if (_closed) return;
        _lblPhase.Text = p.Phase;
        if (p.Indeterminate) _pb.SetIndeterminate();
        else _pb.SetProgress(p.Done, p.Total);

        var speed = ProgressText.Speed(p);
        var eta = ProgressText.Eta(p);
        var volume = ProgressText.Volume(p);
        var parts = new List<string>();
        if (speed.Length > 0) parts.Add("скорость " + speed);
        if (eta.Length > 0) parts.Add("осталось " + eta);
        if (volume.Length > 0) parts.Add(volume);
        _lblStatus.Text = string.Join(" · ", parts);
    }

    private void SetFieldsEnabled(bool enabled)
    {
        _lv.Enabled = enabled;
        _txtName.Enabled = enabled;
        _txtDest.Enabled = enabled;
        _chkStart.Enabled = enabled;
        _btnRefresh.Enabled = enabled;

        var copy = SelectedCopy();
        if (copy != null)
        {
            UpdateForCopy(copy);
        }
        else
        {
            _chkNewId.Enabled = enabled;
        }

        UpdateDeleteButtons();
    }

    // ─────────────────────────────── Вспомогательное ───────────────────────────────

    /// <summary>
    /// Маленький красный значок корзины (16×16) — удаление конкретной копии
    /// прямо в её строке списка.
    /// </summary>
    private static Bitmap TrashIcon()
    {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Glyphs.Draw(g, "trash", new RectangleF(0, 0, 16, 16), Color.FromArgb(196, 43, 28));
        }
        return bmp;
    }

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
    private static void AddRow(TableLayoutPanel table, string label, Control control, Label? hint, int height)
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

        if (hint != null)
        {
            table.Controls.Add(hint, 2, r);
        }
        else
        {
            table.SetColumnSpan(control, 2);
        }
    }

    /// <summary>Поле ввода с кнопкой выбора папки (позиции заданы вручную).</summary>
    private static Panel BuildPathField(out ModernTextBox box, out RoundedButton browse)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Height = 34, BackColor = Theme.Window, Margin = new Padding(0) };

        box = new ModernTextBox { Dock = DockStyle.Fill, Height = 30, Margin = new Padding(0, 2, 0, 2) };
        browse = new RoundedButton { Text = "…", Dock = DockStyle.Right, Size = new Size(36, 30), Font = Theme.BodyBold };

        panel.Controls.Add(box);
        panel.Controls.Add(browse);
        return panel;
    }

    private static Label Hint(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = false,
        Height = 34,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.TextSecondary,
        Font = Theme.Small,
        Margin = new Padding(0)
    };

    private static Label SectionTitle(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = false,
        Height = 24,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.TextSecondary,
        Font = Theme.BodyBold,
        Margin = new Padding(0)
    };

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!SkipHostPaths) _ = LoadHostPathsAsync();
    }

    /// <summary>Не опрашивать пути хоста (для предпросмотра оформления).</summary>
    internal bool SkipHostPaths { get; set; }

    /// <summary>Рисует значки строк поверх снимка окна (для предпросмотра).</summary>
    internal void PaintRowIcons(Bitmap bmp) => _lv.PaintIconsOnto(bmp, this);

    /// <summary>Демонстрационные данные для предпросмотра оформления.</summary>
    internal void ApplyPreviewData()
    {
        // Как при открытии окна с выделенной машиной в главном окне.
        _filter.Add("Win10-Test");

        _copies.Clear();
        _copies.Add(new BackupCopyInfo
        {
            VmName = "Win10-Test",
            Folder = @"D:\Backups\Hyper-V\Win10-Test\2026-11-27_22-14-03",
            Created = new DateTime(2026, 11, 27, 22, 14, 3),
            SizeBytes = 42L << 30,
            Kind = "export",
            Consistency = "экспорт Hyper-V работающей ВМ (согласованный Production-снимок VSS, машина не останавливалась)"
        });
        _copies.Add(new BackupCopyInfo
        {
            VmName = "Win10-Test",
            Folder = @"D:\Backups\Hyper-V\Win10-Test\2026-11-25_03-00-00",
            Created = new DateTime(2026, 11, 25, 3, 0, 0),
            SizeBytes = 41L << 30,
            Kind = "export",
            Consistency = "экспорт Hyper-V (машина была выключена)"
        });
        _copies.Add(new BackupCopyInfo
        {
            VmName = "Win10-Test",
            Folder = @"D:\Backups\Hyper-V\Win10-Test\2026-11-26_03-00-00",
            Created = new DateTime(2026, 11, 26, 3, 0, 0),
            SizeBytes = 41L << 30,
            Kind = "checkpoint",
            Consistency = "контрольная точка Production, машина продолжала работать"
        });
        _copies.Add(new BackupCopyInfo
        {
            VmName = "Win-Server-2019",
            Folder = @"D:\Backups\Hyper-V\Win-Server-2019\2026-11-26_03-00-00",
            Created = new DateTime(2026, 11, 26, 3, 0, 0),
            SizeBytes = 320L << 30,
            Kind = "checkpoint",
            Consistency = "контрольная точка Production, машина продолжала работать"
        });
        _copies.Add(new BackupCopyInfo
        {
            VmName = "Ubuntu-Dev",
            Folder = @"D:\Backups\Hyper-V\Ubuntu-Dev\2026-11-25_03-00-00",
            Created = new DateTime(2026, 11, 25, 3, 0, 0),
            SizeBytes = 20L << 30,
            Kind = "export",
            Consistency = "экспорт Hyper-V (машина была выключена)"
        });

        FillList(_copies[0].Folder);
        _txtName.Text = "Win10-Test";
        _txtDest.Text = @"D:\HyperV\Virtual Hard Disks\HvRestore\Win10-Test";
        _lblStatus.Text = "Машина «Win10-Test» будет восстановлена в D:\\HyperV\\…\\Win10-Test.";
    }

    /// <summary>Узнаёт пути хранения машин и дисков на хосте (нужны права администратора).</summary>
    private async Task LoadHostPathsAsync()
    {
        try
        {
            var (ok, _, vhdPath, _) = await Task.Run(() => HyperV.GetHostPaths()).ConfigureAwait(true);
            if (!ok || vhdPath.Length == 0) return;

            _hostVhdPath = vhdPath;
            if (_destAuto) ApplyAutoDestination(_txtName.Text.Trim());
        }
        catch (Exception ex)
        {
            AppLog.Info("Пути Hyper-V недоступны: " + ex.Message);
        }
    }
}
