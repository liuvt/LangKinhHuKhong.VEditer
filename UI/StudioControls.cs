using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LangKinhHuKhong.Editer.UI;

/// <summary>
/// Modern Pro Studio custom controls with anti-aliased GDI+ rendering.
/// Avoids setting Win32 Control.Region masks which cause jagged edges, GDI handle churn, and hover glitches.
/// </summary>
internal static class StudioShape
{
    public static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 1 || bounds.Height <= 1) return path;
        var diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class StudioCard : Panel
{
    public Color BorderColor { get; set; } = Color.FromArgb(38, 50, 68);
    public Color SurfaceColor { get; set; } = Color.FromArgb(22, 30, 43);
    public int CornerRadius { get; set; } = 10;

    public StudioCard()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.FromArgb(22, 30, 43);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var parentColor = Parent?.BackColor ?? Color.FromArgb(14, 19, 27);
        e.Graphics.Clear(parentColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (rect.Width <= 1 || rect.Height <= 1) return;

        using var path = StudioShape.Rounded(rect, CornerRadius);
        using var fill = new SolidBrush(BackColor);
        using var pen = new Pen(BorderColor, 1f);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(pen, path);
    }
}

internal sealed class StudioButton : Button
{
    private bool _hovered;
    private bool _pressed;

    public bool IsPrimary { get; set; }
    public Color HoverBackColor { get; set; } = Color.Empty;
    public Color PressedBackColor { get; set; } = Color.Empty;
    public Color BorderColor { get; set; } = Color.FromArgb(51, 65, 85);
    public int CornerRadius { get; set; } = 7;

    public StudioButton()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (Enabled && !_hovered)
        {
            _hovered = true;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered || _pressed)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        base.OnMouseDown(mevent);
        if (mevent.Button == MouseButtons.Left && Enabled)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        base.OnMouseUp(mevent);
        if (_pressed)
        {
            _pressed = false;
            Invalidate();
        }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _hovered = false;
        _pressed = false;
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        var parentBg = Parent?.BackColor ?? Color.FromArgb(22, 30, 43);
        pevent.Graphics.Clear(parentBg);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (rect.Width <= 1 || rect.Height <= 1) return;

        Color fill;
        Color textCol;
        Color borderCol;

        if (!Enabled)
        {
            fill = Color.FromArgb(26, 34, 48);
            textCol = Color.FromArgb(95, 110, 132);
            borderCol = Color.FromArgb(38, 49, 66);
        }
        else if (_pressed)
        {
            fill = PressedBackColor != Color.Empty
                ? PressedBackColor
                : (IsPrimary ? Color.FromArgb(29, 78, 216) : Color.FromArgb(24, 34, 48));
            textCol = ForeColor;
            borderCol = IsPrimary ? Color.FromArgb(59, 130, 246) : Color.FromArgb(71, 85, 105);
        }
        else if (_hovered)
        {
            fill = HoverBackColor != Color.Empty
                ? HoverBackColor
                : (IsPrimary ? Color.FromArgb(59, 130, 246) : Color.FromArgb(43, 56, 78));
            textCol = ForeColor;
            borderCol = IsPrimary ? Color.FromArgb(96, 165, 250) : Color.FromArgb(82, 100, 126);
        }
        else
        {
            fill = BackColor;
            textCol = ForeColor;
            borderCol = IsPrimary ? Color.FromArgb(37, 99, 235) : BorderColor;
        }

        using var path = StudioShape.Rounded(rect, CornerRadius);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(borderCol, 1f);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);

        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        flags |= TextAlign switch
        {
            ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft
                => TextFormatFlags.Left | TextFormatFlags.VerticalCenter,
            ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight
                => TextFormatFlags.Right | TextFormatFlags.VerticalCenter,
            _ => TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
        };

        var textBounds = new Rectangle(
            rect.X + Math.Max(8, Padding.Left),
            rect.Y,
            Math.Max(1, rect.Width - Math.Max(16, Padding.Horizontal)),
            rect.Height);
        TextRenderer.DrawText(g, Text, Font, textBounds, textCol, flags);
    }
}
