using System.Drawing.Drawing2D;

namespace HvBackup.Ui;

/// <summary>Простые векторные значки, нарисованные на лету (без ресурсов).</summary>
public static class Glyphs
{
    public static void Draw(Graphics g, string name, RectangleF b, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        using var pen = new Pen(color, Math.Max(1.2f, b.Height * 0.09f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        switch (name.ToLowerInvariant())
        {
            case "copy":
                DrawCopy(g, b, brush, pen);
                break;
            case "refresh":
                DrawRefresh(g, b, pen);
                break;
            case "settings":
                DrawSliders(g, b, pen, brush);
                break;
            case "folder":
                DrawFolder(g, b, brush);
                break;
            case "log":
                DrawLog(g, b, pen);
                break;
            case "stop":
                DrawStop(g, b, brush);
                break;
            case "shield":
                DrawShield(g, b, brush);
                break;
            case "clock":
                DrawClock(g, b, pen);
                break;
            case "restore":
                DrawRestore(g, b, pen);
                break;
            case "trash":
                DrawTrash(g, b, pen);
                break;
        }
    }

    /// <summary>Значок «копировать»: стрелка вниз в лоток.</summary>
    private static void DrawCopy(Graphics g, RectangleF b, Brush brush, Pen pen)
    {
        float cx = b.Left + b.Width / 2f;
        float top = b.Top + b.Height * 0.08f;
        float arrowBottom = b.Top + b.Height * 0.55f;

        g.DrawLine(pen, cx, top, cx, arrowBottom);
        g.DrawLine(pen, cx - b.Width * 0.16f, arrowBottom - b.Height * 0.17f, cx, arrowBottom);
        g.DrawLine(pen, cx + b.Width * 0.16f, arrowBottom - b.Height * 0.17f, cx, arrowBottom);

        float trayLeft = b.Left + b.Width * 0.16f;
        float trayRight = b.Right - b.Width * 0.16f;
        float trayY = b.Bottom - b.Height * 0.12f;
        g.DrawLine(pen, trayLeft, trayY - b.Height * 0.14f, trayLeft, trayY);
        g.DrawLine(pen, trayLeft, trayY, trayRight, trayY);
        g.DrawLine(pen, trayRight, trayY, trayRight, trayY - b.Height * 0.14f);
    }

    private static void DrawRefresh(Graphics g, RectangleF b, Pen pen)
    {
        var rect = new RectangleF(b.Left + b.Width * 0.18f, b.Top + b.Height * 0.18f,
            b.Width * 0.64f, b.Height * 0.64f);
        g.DrawArc(pen, rect, 40f, 290f);

        float cx = rect.Left + rect.Width / 2f;
        float cy = rect.Top + rect.Height / 2f;
        float r = rect.Width / 2f;
        double angle = (40f + 290f) * Math.PI / 180.0;
        float ax = cx + r * (float)Math.Cos(angle);
        float ay = cy + r * (float)Math.Sin(angle);
        using var brush = new SolidBrush(pen.Color);
        g.FillPolygon(brush, new[]
        {
            new PointF(ax, ay),
            new PointF(ax - r * 0.42f, ay - r * 0.10f),
            new PointF(ax + r * 0.16f, ay + r * 0.36f)
        });
    }

    private static void DrawSliders(Graphics g, RectangleF b, Pen pen, Brush brush)
    {
        int rows = 3;
        float gap = b.Height / (rows + 1f);
        for (int i = 0; i < rows; i++)
        {
            float y = b.Top + gap * (i + 1);
            float left = b.Left + b.Width * 0.08f;
            float right = b.Right - b.Width * 0.08f;
            g.DrawLine(pen, left, y, right, y);
            float knobX = b.Left + b.Width * (i == 1 ? 0.62f : 0.34f);
            float r = b.Height * 0.10f;
            g.FillEllipse(brush, knobX - r, y - r, r * 2f, r * 2f);
        }
    }

    private static void DrawFolder(Graphics g, RectangleF b, Brush brush)
    {
        using var path = new GraphicsPath();
        float left = b.Left + b.Width * 0.10f;
        float right = b.Right - b.Width * 0.10f;
        float top = b.Top + b.Height * 0.24f;
        float bottom = b.Bottom - b.Height * 0.14f;
        path.AddLine(new PointF(left, top), new PointF(b.Left + b.Width * 0.42f, top));
        path.AddLine(new PointF(b.Left + b.Width * 0.42f, top), new PointF(b.Left + b.Width * 0.52f, top + b.Height * 0.12f));
        path.AddLine(new PointF(b.Left + b.Width * 0.52f, top + b.Height * 0.12f), new PointF(right, top + b.Height * 0.12f));
        path.AddLine(new PointF(right, top + b.Height * 0.12f), new PointF(right, bottom));
        path.AddLine(new PointF(right, bottom), new PointF(left, bottom));
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private static void DrawLog(Graphics g, RectangleF b, Pen pen)
    {
        float y = b.Top + b.Height * 0.28f;
        float step = b.Height * 0.24f;
        for (int i = 0; i < 3; i++)
        {
            float width = i == 2 ? b.Width * 0.5f : b.Width * 0.78f;
            g.DrawLine(pen, b.Left + b.Width * 0.12f, y, b.Left + b.Width * 0.12f + width, y);
            y += step;
        }
    }

    private static void DrawStop(Graphics g, RectangleF b, Brush brush)
    {
        float size = b.Height * 0.58f;
        var rect = new RectangleF(b.Left + (b.Width - size) / 2f, b.Top + (b.Height - size) / 2f, size, size);
        g.FillRectangle(brush, rect);
    }

    private static void DrawShield(Graphics g, RectangleF b, Brush brush)
    {
        using var path = new GraphicsPath();
        float cx = b.Left + b.Width / 2f;
        float top = b.Top + b.Height * 0.12f;
        float bottom = b.Bottom - b.Height * 0.12f;
        path.AddLine(new PointF(cx - b.Width * 0.30f, top), new PointF(cx + b.Width * 0.30f, top));
        path.AddLine(new PointF(cx + b.Width * 0.30f, top), new PointF(cx + b.Width * 0.30f, b.Top + b.Height * 0.52f));
        path.AddCurve(new[] { new PointF(cx, bottom), new PointF(cx - b.Width * 0.30f, b.Top + b.Height * 0.52f) });
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    /// <summary>Значок «часы»: круг со стрелками — цикличность запусков.</summary>
    private static void DrawClock(Graphics g, RectangleF b, Pen pen)
    {
        var rect = new RectangleF(b.Left + b.Width * 0.10f, b.Top + b.Height * 0.10f,
            b.Width * 0.80f, b.Height * 0.80f);
        g.DrawEllipse(pen, rect);

        float cx = rect.Left + rect.Width / 2f;
        float cy = rect.Top + rect.Height / 2f;
        g.DrawLine(pen, cx, cy, cx, rect.Top + rect.Height * 0.22f);   // минутная стрелка
        g.DrawLine(pen, cx, cy, cx + rect.Width * 0.26f, cy);          // часовая стрелка
    }

    /// <summary>Значок «восстановление»: круговая стрелка против часовой стрелки.</summary>
    private static void DrawRestore(Graphics g, RectangleF b, Pen pen)
    {
        var rect = new RectangleF(b.Left + b.Width * 0.18f, b.Top + b.Height * 0.18f,
            b.Width * 0.64f, b.Height * 0.64f);

        const float start = 300f;
        const float sweep = 285f;
        g.DrawArc(pen, rect, start, sweep);

        float cx = rect.Left + rect.Width / 2f;
        float cy = rect.Top + rect.Height / 2f;
        float r = rect.Width / 2f;

        // Конец дуги и касательная к ней (направление движения по дуге).
        double angle = (start + sweep) * Math.PI / 180.0;
        float ex = cx + r * (float)Math.Cos(angle);
        float ey = cy + r * (float)Math.Sin(angle);
        float tx = -(float)Math.Sin(angle);
        float ty = (float)Math.Cos(angle);
        float nx = (float)Math.Cos(angle);
        float ny = (float)Math.Sin(angle);

        float len = b.Width * 0.22f;
        float half = b.Width * 0.15f;

        using var brush = new SolidBrush(pen.Color);
        g.FillPolygon(brush, new[]
        {
            new PointF(ex + tx * len, ey + ty * len),
            new PointF(ex + nx * half, ey + ny * half),
            new PointF(ex - nx * half, ey - ny * half)
        });
    }

    /// <summary>Значок «корзина»: крышка и ведро.</summary>
    private static void DrawTrash(Graphics g, RectangleF b, Pen pen)
    {
        float left = b.Left + b.Width * 0.26f;
        float right = b.Right - b.Width * 0.26f;
        float top = b.Top + b.Height * 0.34f;
        float bottom = b.Bottom - b.Height * 0.14f;

        // крышка
        g.DrawLine(pen, b.Left + b.Width * 0.16f, top, b.Right - b.Width * 0.16f, top);
        // ручка
        g.DrawLine(pen, b.Left + b.Width * 0.36f, top, b.Left + b.Width * 0.36f, b.Top + b.Height * 0.20f);
        g.DrawLine(pen, b.Left + b.Width * 0.36f, b.Top + b.Height * 0.20f, b.Right - b.Width * 0.36f, b.Top + b.Height * 0.20f);
        g.DrawLine(pen, b.Right - b.Width * 0.36f, b.Top + b.Height * 0.20f, b.Right - b.Width * 0.36f, top);
        // ведро
        g.DrawLine(pen, left, top, left + b.Width * 0.05f, bottom);
        g.DrawLine(pen, right, top, right - b.Width * 0.05f, bottom);
        g.DrawLine(pen, left + b.Width * 0.05f, bottom, right - b.Width * 0.05f, bottom);
    }
}
