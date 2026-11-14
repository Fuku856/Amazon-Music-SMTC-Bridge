using System.Drawing.Drawing2D;

namespace AmazonMusicSmtc.UI;

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer()
        : base(new DarkMenuColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown)
        {
            base.OnRenderToolStripBackground(e);
            return;
        }

        using var brush = new SolidBrush(Theme.Surface);
        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.ToolStrip.Size));
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown)
        {
            base.OnRenderToolStripBorder(e);
            return;
        }

        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(
            pen,
            0,
            0,
            Math.Max(0, e.ToolStrip.Width - 1),
            Math.Max(0, e.ToolStrip.Height - 1));
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown)
        {
            base.OnRenderImageMargin(e);
            return;
        }

        using var brush = new SolidBrush(Theme.Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed)
            return;

        var bounds = new Rectangle(2, 1, Math.Max(0, e.Item.Width - 4), Math.Max(0, e.Item.Height - 2));
        using var path = RoundedRectangle(bounds, 5);
        using var brush = new SolidBrush(e.Item.Pressed ? Theme.SurfaceRaised : Theme.Hover);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextDisabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        if (e.Vertical)
        {
            base.OnRenderSeparator(e);
            return;
        }

        using var pen = new Pen(Theme.BorderSoft);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 12, y, Math.Max(12, e.Item.Width - 12), y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var bounds = e.ImageRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var x = bounds.Left + bounds.Width / 2f - 4;
        var y = bounds.Top + bounds.Height / 2f - 3;

        using var pen = new Pen(Theme.Accent, 1.8f);
        graphics.DrawLines(pen,
        [
            new PointF(x, y + 4),
            new PointF(x + 3, y + 7),
            new PointF(x + 9, y - 1),
        ]);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.TextMuted;
        base.OnRenderArrow(e);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));

        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }

    private sealed class DarkMenuColorTable : ProfessionalColorTable
    {
        public DarkMenuColorTable()
        {
            UseSystemColors = false;
        }

        public override Color ToolStripDropDownBackground => Theme.Surface;

        public override Color ImageMarginGradientBegin => Theme.Surface;

        public override Color ImageMarginGradientMiddle => Theme.Surface;

        public override Color ImageMarginGradientEnd => Theme.Surface;

        public override Color MenuBorder => Theme.Border;

        public override Color MenuItemBorder => Theme.Hover;

        public override Color MenuItemSelected => Theme.Hover;

        public override Color MenuItemSelectedGradientBegin => Theme.Hover;

        public override Color MenuItemSelectedGradientEnd => Theme.Hover;

        public override Color MenuItemPressedGradientBegin => Theme.SurfaceRaised;

        public override Color MenuItemPressedGradientMiddle => Theme.SurfaceRaised;

        public override Color MenuItemPressedGradientEnd => Theme.SurfaceRaised;

        public override Color SeparatorDark => Theme.BorderSoft;

        public override Color SeparatorLight => Theme.BorderSoft;

        public override Color CheckBackground => Theme.Surface;

        public override Color CheckSelectedBackground => Theme.Hover;

        public override Color CheckPressedBackground => Theme.SurfaceRaised;

        public override Color GripDark => Theme.Border;

        public override Color GripLight => Theme.SurfaceRaised;
    }
}
