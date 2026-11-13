using System.Globalization;
using System.Resources;

namespace AmazonMusicSmtc.Localization;

internal static class Strings
{
    private static readonly ResourceManager Resources =
        new("AmazonMusicSmtc.Resources.Strings", typeof(Strings).Assembly);

    public static string ShowLog => Get(nameof(ShowLog));

    public static string SourceMenu => Get(nameof(SourceMenu));

    public static string SourceAuto => Get(nameof(SourceAuto));

    public static string SourceAutoTip => Get(nameof(SourceAutoTip));

    public static string SourceCdp => Get(nameof(SourceCdp));

    public static string SourceCdpTip => Get(nameof(SourceCdpTip));

    public static string SourceNotification => Get(nameof(SourceNotification));

    public static string SourceNotificationTip => Get(nameof(SourceNotificationTip));

    public static string RestartWithDebugPort => Get(nameof(RestartWithDebugPort));

    public static string AlwaysRunWithDebugPort => Get(nameof(AlwaysRunWithDebugPort));

    public static string AlwaysRunWithDebugPortTip => Get(nameof(AlwaysRunWithDebugPortTip));

    public static string SessionPriority => Get(nameof(SessionPriority));

    public static string SessionPriorityTip => Get(nameof(SessionPriorityTip));

    public static string HideTrackChangeNotifications => Get(nameof(HideTrackChangeNotifications));

    public static string HideTrackChangeNotificationsTip => Get(nameof(HideTrackChangeNotificationsTip));

    public static string SaveArtworkOffline => Get(nameof(SaveArtworkOffline));

    public static string SaveArtworkOfflineTip => Get(nameof(SaveArtworkOfflineTip));

    public static string LanguageMenu => Get(nameof(LanguageMenu));

    public static string Exit => Get(nameof(Exit));

    public static void Apply(UiLanguage language)
    {
        var culture = CultureInfo.GetCultureInfo(language == UiLanguage.Japanese ? "ja" : "en");

        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private static string Get(string name) => Resources.GetString(name) ?? name;
}
