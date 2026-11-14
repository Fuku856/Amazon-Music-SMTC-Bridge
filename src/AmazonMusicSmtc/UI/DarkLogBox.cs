namespace AmazonMusicSmtc.UI;

internal sealed class DarkLogBox : TextBox
{
    public DarkLogBox()
    {
        BackColor = Theme.LogBackground;
        ForeColor = Theme.LogText;
        Font = Theme.MonoFont;
        BorderStyle = BorderStyle.None;
        Multiline = true;
        ReadOnly = true;
        WordWrap = false;
        ScrollBars = ScrollBars.Both;
        HideSelection = false;

        SetStyle(
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint,
            true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowTheme.ApplyDarkScrollBars(Handle);
    }
}
