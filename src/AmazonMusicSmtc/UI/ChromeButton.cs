using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace AmazonMusicSmtc.UI;

internal enum ChromeButtonKind
{
    Minimize,
    Maximize,
    Restore,
    Close,
}

internal sealed class ChromeButton : Control
{
    private bool _hovered;
    private bool _pressed;
    private ChromeButtonKind _kind;

    public ChromeButton(ChromeButtonKind kind)
    {
        _kind = kind;
        BackColor = Theme.Background;
        Cursor = Cursors.Hand;
        TabStop = false;
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint,
            true);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ChromeButtonKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value)
                return;

            _kind = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var isClose = _kind == ChromeButtonKind.Close;
        if (_hovered || _pressed)
        {
            var color = isClose
                ? Theme.CloseHover
                : _pressed ? Theme.SurfaceRaised : Theme.Hover;

            using var brush = new SolidBrush(color);
            graphics.FillRectangle(brush, ClientRectangle);
        }

        var foreground = isClose && (_hovered || _pressed) ? Theme.CloseText : Theme.TextMuted;
        using var pen = new Pen(foreground, 1.4f);

        var centerX = ClientSize.Width / 2f;
        var centerY = ClientSize.Height / 2f;

        switch (_kind)
        {
            case ChromeButtonKind.Minimize:
                graphics.DrawLine(pen, centerX - 5, centerY + 5, centerX + 5, centerY + 5);
                break;

            case ChromeButtonKind.Maximize:
                graphics.DrawRectangle(pen, centerX - 5, centerY - 5, 10, 10);
                break;

            case ChromeButtonKind.Restore:
                graphics.DrawRectangle(pen, centerX - 4, centerY - 6, 9, 9);
                graphics.DrawRectangle(pen, centerX - 6, centerY - 4, 9, 9);
                break;

            case ChromeButtonKind.Close:
                graphics.DrawLine(pen, centerX - 5, centerY - 5, centerX + 5, centerY + 5);
                graphics.DrawLine(pen, centerX + 5, centerY - 5, centerX - 5, centerY + 5);
                break;
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }
}
