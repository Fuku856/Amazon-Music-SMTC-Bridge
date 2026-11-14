using System.Runtime.InteropServices;

namespace AmazonMusicSmtc.UI;

internal static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    public static void Apply(IntPtr handle)
    {
        var dark = 1;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));

        var rounded = DwmwcpRound;
        DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref rounded, sizeof(int));

        var border = Theme.Border;
        var colorRef = border.R | border.G << 8 | border.B << 16;
        DwmSetWindowAttribute(handle, DwmwaBorderColor, ref colorRef, sizeof(int));
    }

    public static void ApplyDarkScrollBars(IntPtr handle) =>
        SetWindowTheme(handle, "DarkMode_Explorer", null);
}
