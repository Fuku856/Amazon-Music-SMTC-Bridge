using System.Runtime.InteropServices;

namespace AmazonMusicSmtc.UI;

internal class DarkChromeForm : Form
{
    protected const int TitleBarHeight = 48;

    private const int WmNcHitTest = 0x0084;
    private const int WmNcLButtonDblClk = 0x00A3;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int ResizeBorder = 6;
    private const int CsDropShadow = 0x00020000;

    private ChromeButton? _minimizeButton;
    private ChromeButton? _maximizeButton;
    private ChromeButton? _closeButton;
    private bool _windowActive = true;

    protected DarkChromeForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Theme.Background;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        MinimumSize = new Size(720, 420);
        Padding = new Padding(0, TitleBarHeight, 0, 0);

        _minimizeButton = new ChromeButton(ChromeButtonKind.Minimize);
        _minimizeButton.Click += (_, _) => WindowState = FormWindowState.Minimized;

        _maximizeButton = new ChromeButton(ChromeButtonKind.Maximize);
        _maximizeButton.Click += (_, _) => ToggleMaximize();

        _closeButton = new ChromeButton(ChromeButtonKind.Close);
        _closeButton.Click += (_, _) => Close();

        Controls.Add(_minimizeButton);
        Controls.Add(_maximizeButton);
        Controls.Add(_closeButton);
    }

    protected ChromeButton MinimizeButton => _minimizeButton!;

    protected ChromeButton MaximizeButton => _maximizeButton!;

    protected ChromeButton CloseButton => _closeButton!;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= CsDropShadow;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowTheme.Apply(Handle);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        SetWindowActive(true);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        SetWindowActive(false);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        if (_minimizeButton is null || _maximizeButton is null || _closeButton is null)
            return;

        const int width = 46;
        var closeLeft = ClientSize.Width - width;
        _closeButton.Bounds = new Rectangle(closeLeft, 0, width, TitleBarHeight);
        _maximizeButton.Bounds = new Rectangle(closeLeft - width, 0, width, TitleBarHeight);
        _minimizeButton.Bounds = new Rectangle(closeLeft - width * 2, 0, width, TitleBarHeight);

        _maximizeButton.Kind = WindowState == FormWindowState.Maximized
            ? ChromeButtonKind.Restore
            : ChromeButtonKind.Maximize;

        Invalidate(new Rectangle(0, 0, ClientSize.Width, TitleBarHeight));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var graphics = e.Graphics;
        var titleBar = new Rectangle(0, 0, ClientSize.Width, TitleBarHeight);

        using (var brush = new SolidBrush(Theme.Background))
            graphics.FillRectangle(brush, titleBar);

        using (var pen = new Pen(Theme.BorderSoft))
            graphics.DrawLine(pen, 0, TitleBarHeight - 1, ClientSize.Width, TitleBarHeight - 1);

        var textLeft = 18;

        if (Icon is not null)
        {
            var iconRectangle = new Rectangle(18, (TitleBarHeight - 16) / 2, 16, 16);
            graphics.DrawIcon(Icon, iconRectangle);
            textLeft = iconRectangle.Right + 10;
        }

        TextRenderer.DrawText(
            graphics,
            Text,
            Theme.TitleFont,
            new Rectangle(textLeft, 0, Math.Max(0, _minimizeButton!.Left - textLeft - 12), TitleBarHeight),
            _windowActive ? Theme.Text : Theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void SetWindowActive(bool active)
    {
        if (_windowActive == active)
            return;

        _windowActive = active;
        Invalidate(new Rectangle(0, 0, ClientSize.Width, TitleBarHeight));
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmGetMinMaxInfo)
        {
            base.WndProc(ref message);
            ApplyMaximizedBounds(message.LParam);
            return;
        }

        if (message.Msg == WmNcLButtonDblClk && message.WParam == HtCaption)
        {
            ToggleMaximize();
            return;
        }

        if (message.Msg == WmNcHitTest)
        {
            base.WndProc(ref message);

            if ((int)message.Result != HtClient)
                return;

            var screenPoint = new Point(
                unchecked((short)(long)message.LParam),
                unchecked((short)((long)message.LParam >> 16)));
            var point = PointToClient(screenPoint);

            if (WindowState == FormWindowState.Normal && TryResizeHit(point, out var resizeResult))
            {
                message.Result = resizeResult;
                return;
            }

            if (point.Y < TitleBarHeight &&
                (_minimizeButton is null || point.X < _minimizeButton.Left))
            {
                message.Result = HtCaption;
            }

            return;
        }

        base.WndProc(ref message);
    }

    private bool TryResizeHit(Point point, out IntPtr result)
    {
        var border = LogicalToDeviceUnits(ResizeBorder);
        var left = point.X <= border;
        var right = point.X >= ClientSize.Width - border;
        var top = point.Y <= border;
        var bottom = point.Y >= ClientSize.Height - border;

        var hit = (left, right, top, bottom) switch
        {
            (true, _, true, _) => HtTopLeft,
            (_, true, true, _) => HtTopRight,
            (true, _, _, true) => HtBottomLeft,
            (_, true, _, true) => HtBottomRight,
            (true, _, _, _) => HtLeft,
            (_, true, _, _) => HtRight,
            (_, _, true, _) => HtTop,
            (_, _, _, true) => HtBottom,
            _ => 0,
        };

        result = hit == 0 ? IntPtr.Zero : new IntPtr(hit);
        return hit != 0;
    }

    private void ApplyMaximizedBounds(IntPtr pointer)
    {
        var info = Marshal.PtrToStructure<MinMaxInfo>(pointer);
        var screen = Screen.FromHandle(Handle);
        var bounds = screen.Bounds;
        var workArea = screen.WorkingArea;

        info.MaxPosition.X = workArea.Left - bounds.Left;
        info.MaxPosition.Y = workArea.Top - bounds.Top;
        info.MaxSize.X = workArea.Width;
        info.MaxSize.Y = workArea.Height;

        Marshal.StructureToPtr(info, pointer, false);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MaxTrackSize;
        public NativePoint MinTrackSize;
    }
}
