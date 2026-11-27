using System.Drawing.Drawing2D;

namespace HvBackup.Ui;

/// <summary>Скруглённый путь по прямоугольнику (Win11-стиль).</summary>
public static class Shapes
{
    public static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        radius = Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>Кнопка в стиле Windows 11: скругление, состояния наведения, значок.</summary>
public class RoundedButton : Control, IButtonControl
{
    public enum ButtonKind { Neutral, Accent, Success, Danger, Scheduled, Ghost }

    private bool _hover;
    private bool _pressed;
    private ButtonKind _appearance = ButtonKind.Neutral;

    public ButtonKind Appearance
    {
        get => _appearance;
        set { _appearance = value; Invalidate(); }
    }

    public string Glyph { get; set; } = "";
    public bool Pill { get; set; }
    public int Radius { get; set; } = Theme.ButtonRadius;
    public bool Bold { get; set; }

    public DialogResult DialogResult { get; set; } = DialogResult.None;

    void IButtonControl.NotifyDefault(bool value) { }

    public void PerformClick() => OnClick(EventArgs.Empty);

    public RoundedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Font = Theme.Body;
        TabStop = true;
        Cursor = Cursors.Hand;
        Size = new Size(120, 32);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = Pill ? rect.Height / 2 : Math.Min(Radius, rect.Height / 2);
        using var path = Shapes.RoundedPath(rect, radius);

        var (bg, fg, border) = Palette();
        if (bg.A > 0)
        {
            using var brush = new SolidBrush(bg);
            g.FillPath(brush, path);
        }
        if (border.A > 0)
        {
            using var pen = new Pen(border);
            g.DrawPath(pen, path);
        }
        if (Focused && Enabled)
        {
            using var focusPen = new Pen(Theme.Accent, 1.6f);
            var inner = Rectangle.Inflate(rect, -2, -2);
            using var focusPath = Shapes.RoundedPath(inner, Math.Max(1, radius - 2));
            g.DrawPath(focusPen, focusPath);
        }

        var glyphSize = Glyph.Length > 0 ? Math.Min(16, Height - 12) : 0;
        var textSize = TextRenderer.MeasureText(g, Text, Font);
        var gap = glyphSize > 0 ? 7 : 0;
        var totalWidth = glyphSize + gap + textSize.Width;
        var startX = Math.Max(0, (Width - totalWidth) / 2);

        if (glyphSize > 0)
        {
            var glyphRect = new RectangleF(startX, (Height - glyphSize) / 2f, glyphSize, glyphSize);
            Glyphs.Draw(g, Glyph, glyphRect, fg);
        }
        if (Text.Length > 0)
        {
            var textRect = new Rectangle(startX + glyphSize + gap, 0, textSize.Width, Height);
            TextRenderer.DrawText(g, Text, Bold ? Theme.BodyBold : Font, textRect, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    private (Color Bg, Color Fg, Color Border) Palette()
    {
        if (!Enabled)
            return (Theme.Subtle, Theme.TextDisabled, Color.Transparent);

        return Appearance switch
        {
            ButtonKind.Accent => (_pressed ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent,
                                  Color.White, Color.Transparent),
            ButtonKind.Success => (_pressed ? Theme.SuccessPressed : _hover ? Theme.SuccessHover : Theme.Success,
                                  Color.White, Color.Transparent),
            ButtonKind.Danger => (_pressed ? Theme.Danger : _hover ? Theme.DangerHover : Theme.Danger,
                                  Color.White, Color.Transparent),
            ButtonKind.Scheduled => (_pressed ? Theme.ScheduledPressed : _hover ? Theme.ScheduledHover : Theme.Scheduled,
                                  Theme.ScheduledFg, Color.Transparent),
            ButtonKind.Ghost => (_pressed ? Theme.Subtle : _hover ? Theme.CardHover : Color.Transparent,
                                  Theme.Text, Color.Transparent),
            _ => (_pressed ? Theme.Subtle : _hover ? Theme.CardHover : Theme.Card,
                  Theme.Text, Theme.CardBorder)
        };
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData == Keys.Space || keyData == Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
    }
}

/// <summary>Карточка со скруглёнными углами (контейнер для содержимого).</summary>
public class RoundedPanel : Panel
{
    private int _radius = Theme.CardRadius;

    public int Radius
    {
        get => _radius;
        set { _radius = value; UpdateRegion(); Invalidate(); }
    }

    public bool ShowBorder { get; set; } = true;

    /// <summary>Цвет поверхности карточки.</summary>
    public Color Surface
    {
        get => BackColor;
        set { BackColor = value; Invalidate(); }
    }

    /// <summary>Цвет рамки карточки.</summary>
    public Color BorderColor { get; set; } = Theme.CardBorder;

    public RoundedPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Padding = new Padding(12);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!ShowBorder || BorderColor.A == 0) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Shapes.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        using var pen = new Pen(BorderColor);
        g.DrawPath(pen, path);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    private void UpdateRegion()
    {
        try
        {
            var w = Width;
            var h = Height;
            if (w <= 0 || h <= 0) return;
            using var path = Shapes.RoundedPath(new Rectangle(0, 0, w, h), Radius);
            Region = new Region(path);
            if (Width != w || Height != h)
            {
                Width = w;
                Height = h;
            }
        }
        catch
        {
            // форма элемента не изменилась — пропускаем
        }
    }
}

/// <summary>Тонкий скруглённый индикатор выполнения в стиле Windows 11.</summary>
public class RoundedProgressBar : Control
{
    private readonly System.Windows.Forms.Timer _timer;
    private double _value;
    private bool _indeterminate;
    private double _offset;

    public int BarHeight { get; set; } = 8;
    public Color FillColor { get; set; } = Theme.Accent;

    public RoundedProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 14;
        _timer = new System.Windows.Forms.Timer { Interval = 30 };
        _timer.Tick += (_, _) =>
        {
            if (!_indeterminate) return;
            _offset = (_offset + 0.035) % 1.6;
            Invalidate();
        };
    }

    public void SetProgress(long done, long total)
    {
        _indeterminate = false;
        _timer.Stop();
        _value = total <= 0 ? 0 : Math.Clamp((double)done / total, 0, 1);
        Invalidate();
    }

    public void SetIndeterminate()
    {
        if (!_indeterminate)
        {
            _indeterminate = true;
            _offset = 0;
            _timer.Start();
        }
        Invalidate();
    }

    public void Reset()
    {
        _indeterminate = false;
        _timer.Stop();
        _value = 0;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var h = Math.Min(BarHeight, Height);
        var track = new Rectangle(0, (Height - h) / 2, Math.Max(1, Width), h);
        using (var brush = new SolidBrush(Theme.Track))
        using (var path = Shapes.RoundedPath(track, h / 2))
            g.FillPath(brush, path);

        if (_indeterminate)
        {
            var w = Math.Max(40, Width / 4);
            var x = (int)((Width + w) * _offset - w);
            var seg = new Rectangle(x, track.Y, w, h);
            using var brush = new SolidBrush(FillColor);
            using var path = Shapes.RoundedPath(seg, h / 2);
            g.FillPath(brush, path);
            return;
        }

        if (_value <= 0) return;
        var fillWidth = (int)Math.Max(h, Math.Round(track.Width * _value));
        if (fillWidth <= 0) return;
        var fill = new Rectangle(track.X, track.Y, fillWidth, h);
        using (var brush = new SolidBrush(FillColor))
        using (var path = Shapes.RoundedPath(fill, h / 2))
            g.FillPath(brush, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Переключатель вкл/выкл в стиле Windows 11.</summary>
public class ToggleSwitch : Control
{
    private bool _checked;

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(46, 24);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var h = Math.Min(Height, 24);
        var w = Math.Min(Width, 46);
        var track = new Rectangle(0, (Height - h) / 2, w, h);
        using (var path = Shapes.RoundedPath(track, h / 2))
        {
            using var brush = new SolidBrush(_checked ? Theme.Accent : Theme.BorderStrong);
            g.FillPath(brush, path);
        }

        var knob = h - 6;
        var kx = _checked ? track.Right - knob - 3 : track.Left + 3;
        using (var brush = new SolidBrush(Color.White))
        {
            g.FillEllipse(brush, kx, track.Y + 3, knob, knob);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !Checked;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space)
        {
            Checked = !Checked;
            e.Handled = true;
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData == Keys.Space || base.IsInputKey(keyData);
}

/// <summary>Поле ввода в стиле Windows 11: скруглённая рамка, подсветка при фокусе.</summary>
public class ModernTextBox : Panel
{
    private readonly TextBox _inner;
    private bool _focused;

    public string Placeholder { get; set; } = "";

    public bool UseSystemPasswordChar
    {
        get => _inner.UseSystemPasswordChar;
        set => _inner.UseSystemPasswordChar = value;
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => _inner.Text;
        set => _inner.Text = value;
    }

    public ModernTextBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Padding = new Padding(10, 6, 10, 6);
        TabStop = false;

        _inner = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            Dock = DockStyle.Fill
        };
        _inner.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        _inner.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        Controls.Add(_inner);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Shapes.RoundedPath(rect, 4);
        using var pen = new Pen(_focused ? Theme.Accent : Theme.BorderStrong, _focused ? 1.5f : 1f);
        g.DrawPath(pen, path);

        if (_focused && Placeholder.Length > 0 && _inner.Text.Length == 0)
        {
            TextRenderer.DrawText(g, Placeholder, Theme.Body, rect, Theme.TextDisabled,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) _inner.Focus();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _inner.Focus();
    }
}

/// <summary>Переключатель вкладок в стиле Windows 11 — овальные «пилюли».</summary>
public class ModernTabHost : Panel
{
    private sealed class Page
    {
        public string Title = "";
        public Control Body = null!;
        public RoundedButton Button = null!;
    }

    private readonly FlowLayoutPanel _strip;
    private readonly Panel _content;
    private readonly List<Page> _pages = new();
    private int _selected = -1;

    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex => _selected;

    public ModernTabHost()
    {
        BackColor = Theme.Window;
        _strip = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(2, 7, 2, 0),
            BackColor = Theme.Window
        };
        _content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window };
        Controls.Add(_content);
        Controls.Add(_strip);
    }

    public void AddPage(string title, Control body)
    {
        body.Dock = DockStyle.Fill;
        body.Visible = false;
        _content.Controls.Add(body);

        var size = TextRenderer.MeasureText(title, Theme.Body);
        var btn = new RoundedButton
        {
            Text = title,
            Pill = true,
            Size = new Size(size.Width + 46, 32),
            Margin = new Padding(2, 0, 4, 0),
            Appearance = RoundedButton.ButtonKind.Ghost
        };
        var index = _pages.Count;
        btn.Click += (_, _) => Select(index);
        _strip.Controls.Add(btn);

        _pages.Add(new Page { Title = title, Body = body, Button = btn });
        if (_pages.Count == 1) Select(0);
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _pages.Count || index == _selected) return;
        _selected = index;
        for (var i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            var selected = i == index;
            page.Body.Visible = selected;
            page.Button.Appearance = selected ? RoundedButton.ButtonKind.Neutral : RoundedButton.ButtonKind.Ghost;
            page.Button.Bold = selected;
        }
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }
}
