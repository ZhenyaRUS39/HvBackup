using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace HvBackup.Ui;

/// <summary>
/// Список со значком-кнопкой в отдельном столбце каждой строки.
/// <para>
/// В Win32 у элемента списка значок бывает только в первой колонке, поэтому
/// значок в отдельном столбце (например, маленькое красное ведро у каждой
/// копии) приходится рисовать поверх списка. Рисование выполняется сразу
/// после штатной отрисовки окна, поэтому значок живёт ровно столько же,
/// сколько сама строка.
/// </para>
/// </summary>
public sealed class IconListView : ListView
{
    private const int WM_PAINT = 0x000F;
    private const int WM_PRINTCLIENT = 0x031A;

    /// <summary>LVM_GETTOPINDEX — номер первой видимой строки.</summary>
    private const int LVM_GETTOPINDEX = 0x1027;

    /// <summary>LVM_GETCOUNTPERPAGE — сколько строк помещается в список.</summary>
    private const int LVM_GETCOUNTPERPAGE = 0x1028;

    /// <summary>LVM_GETHEADER — окно заголовка колонок.</summary>
    private const int LVM_GETHEADER = 0x101F;

    private Image? _rowIcon;
    private int _iconColumn = -1;

    public IconListView()
    {
        DoubleBuffered = true;
    }

    /// <summary>Значок, который рисуется в столбце значков (null — не рисуем).</summary>
    public Image? RowIcon
    {
        get => _rowIcon;
        set
        {
            _rowIcon = value;
            Invalidate();
        }
    }

    /// <summary>Номер столбца, в котором рисуется значок (-1 — не рисуем).</summary>
    public int IconColumn
    {
        get => _iconColumn;
        set
        {
            _iconColumn = value;
            Invalidate();
        }
    }

    /// <summary>
    /// Значок рисуется после штатной отрисовки окна: иначе список затрёт его.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_PAINT || m.Msg == WM_PRINTCLIENT) DrawRowIcons();
    }

    /// <summary>Рисует значки в строках, видимых сейчас.</summary>
    public void DrawRowIcons()
    {
        if (_rowIcon == null || !IsHandleCreated) return;

        var rects = IconRects();
        if (rects.Count == 0) return;

        try
        {
            using var g = Graphics.FromHwnd(Handle);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            foreach (var r in rects) g.DrawImage(_rowIcon, r);
        }
        catch
        {
            // рисование значка не должно ломать список
        }
    }

    /// <summary>
    /// Прямоугольники значков видимых строк (координаты клиентской области списка).
    /// </summary>
    public List<Rectangle> IconRects()
    {
        var result = new List<Rectangle>();
        if (_rowIcon == null || _iconColumn < 0 || _iconColumn >= Columns.Count) return result;
        if (!IsHandleCreated) return result;

        int w = _rowIcon.Width;
        int h = _rowIcon.Height;

        // значок по центру своего столбца
        int x = 0;
        for (int c = 0; c < _iconColumn; c++) x += Columns[c].Width;
        x += Math.Max(0, (Columns[_iconColumn].Width - w) / 2);

        var top = TopIndex();
        if (top < 0) return result;
        var count = CountPerPage();
        if (count <= 0) return result;

        for (int i = 0; i < count; i++)
        {
            var index = top + i;
            if (index >= Items.Count) break;

            Rectangle row;
            try { row = GetItemRect(index); }
            catch { continue; }
            if (row.Height <= 0) continue;

            // координаты строки уже отсчитываются от начала окна списка
            // (заголовок колонок учтён), поэтому дополнительных сдвигов не нужно
            result.Add(new Rectangle(x, row.Top + Math.Max(0, (row.Height - h) / 2), w, h));
        }

        return result;
    }

    /// <summary>Попал ли щелчок в значок строки.</summary>
    public bool HitIcon(Point location)
    {
        foreach (var r in IconRects())
            if (r.Contains(location)) return true;
        return false;
    }

    /// <summary>
    /// Рисует значки поверх готового снимка окна (нужно для предпросмотра:
    /// снимок делается мимо WM_PAINT, поэтому значки в него не попадают).
    /// </summary>
    public void PaintIconsOnto(Bitmap target, Control owner)
    {
        if (_rowIcon == null) return;

        var rects = IconRects();
        if (rects.Count == 0) return;

        try
        {
            // смещение списка в клиентской области окна-владельца
            int dx = 0, dy = 0;
            Control? current = this;
            while (current != null && !ReferenceEquals(current, owner))
            {
                dx += current.Left;
                dy += current.Top;
                current = current.Parent;
            }

            // снимок окна начинается с рамки — клиентская область смещена на её размер
            GetWindowRect(owner.Handle, out var ownerWindow);
            var ownerClient = owner.PointToScreen(Point.Empty);
            dx += ownerClient.X - ownerWindow.Left;
            dy += ownerClient.Y - ownerWindow.Top;

            using var g = Graphics.FromImage(target);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            foreach (var r in rects)
                g.DrawImage(_rowIcon, new Rectangle(dx + r.X, dy + r.Y, r.Width, r.Height));
        }
        catch
        {
            // предпросмотр не должен падать из-за значков
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Номер первой видимой строки (нужен, чтобы значки не уезжали при прокрутке).</summary>
    private int TopIndex() => (int)SendMessage(Handle, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Сколько строк помещается в список без прокрутки.</summary>
    private int CountPerPage() => (int)SendMessage(Handle, LVM_GETCOUNTPERPAGE, IntPtr.Zero, IntPtr.Zero);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
