using System.Drawing.Text;

namespace AmazonMusicSmtc.UI;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(20, 21, 24);
    public static readonly Color Surface = Color.FromArgb(28, 30, 34);
    public static readonly Color SurfaceRaised = Color.FromArgb(36, 39, 44);
    public static readonly Color LogBackground = Color.FromArgb(23, 25, 29);
    public static readonly Color Border = Color.FromArgb(47, 51, 58);
    public static readonly Color BorderSoft = Color.FromArgb(38, 41, 47);
    public static readonly Color Text = Color.FromArgb(232, 234, 237);
    public static readonly Color LogText = Color.FromArgb(205, 211, 218);
    public static readonly Color TextMuted = Color.FromArgb(148, 157, 168);
    public static readonly Color TextDisabled = Color.FromArgb(95, 102, 112);
    public static readonly Color Hover = Color.FromArgb(45, 49, 56);
    public static readonly Color Accent = Color.FromArgb(190, 198, 208);
    public static readonly Color CloseHover = Color.FromArgb(92, 44, 50);
    public static readonly Color CloseText = Color.FromArgb(255, 205, 205);

    private static readonly string UiFontName = ResolveFont("Segoe UI Variable Text", "Segoe UI");
    private static readonly string TitleFontName = ResolveFont("Segoe UI Variable Display", "Segoe UI Semibold", "Segoe UI");
    private static readonly string MonoFontName = ResolveFont("Cascadia Mono", "Consolas", "Courier New");

    public static readonly Font TitleFont = new(TitleFontName, 11f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font BodyFont = new(UiFontName, 9.5f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font CaptionFont = new(UiFontName, 8.25f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font MonoFont = new(MonoFontName, 9.75f, FontStyle.Regular, GraphicsUnit.Point);

    private static string ResolveFont(params string[] candidates)
    {
        using var installed = new InstalledFontCollection();
        var names = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (names.Contains(candidate))
                return candidate;
        }

        return candidates[^1];
    }
}
