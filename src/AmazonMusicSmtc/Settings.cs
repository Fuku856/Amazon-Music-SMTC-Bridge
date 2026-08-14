using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmazonMusicSmtc;

/// <summary>
/// Where the bridge reads track metadata from.
/// </summary>
internal enum MetadataSource
{
    /// <summary>CDP when it is reachable, notifications otherwise. Switches back and forth at runtime.</summary>
    Auto,

    /// <summary>
    /// CDP only. The notification listener is never constructed, so its access
    /// prompt never appears and other apps' toasts are never read.
    /// </summary>
    Cdp,

    /// <summary>Notifications only. Amazon Music is never relaunched and no debug port is opened.</summary>
    Notification,
}

internal sealed class Settings
{
    /// <summary>
    /// Amazon's track-change notifications are persistent, so they pile up in the
    /// Action Center. Off by default: clearing them changes what the user sees.
    /// </summary>
    public bool RemoveNotificationsAfterProcessing { get; set; }

    /// <summary>
    /// Also stop Windows from showing the banner, by turning Amazon Music's per-app
    /// "show banners" setting off. Only takes effect alongside
    /// <see cref="RemoveNotificationsAfterProcessing"/>, which is the switch that
    /// says the user does not want to see these notifications at all - on its own
    /// this one would change a Windows setting nobody asked to change.
    /// </summary>
    public bool SuppressNotificationBanner { get; set; } = true;

    /// <summary>
    /// What ShowBanner held before the bridge turned it off, so the user's own
    /// choice survives. Null means the value did not exist.
    /// </summary>
    public int? PreviousShowBannerValue { get; set; }

    /// <summary>
    /// True while the bridge's ShowBanner = 0 is in place. Tells "not applied yet"
    /// apart from "applied over a value that did not exist", which
    /// <see cref="PreviousShowBannerValue"/> alone cannot express - without it a
    /// second run would record the bridge's own 0 as the value to restore.
    /// </summary>
    public bool BannerSuppressionApplied { get; set; }

    /// <summary>
    /// The exact notification-settings subkey ShowBanner was written to. Amazon's
    /// toast AUMID cannot be read off the toast itself (see
    /// NotificationBannerSuppressor), so the name is a best-effort resolution that
    /// can change between runs as Windows registers more of Amazon's AUMIDs. Pinning
    /// it here the moment it is first applied means Restore always targets the same
    /// key Apply wrote, instead of re-resolving and possibly landing on a different
    /// one that was never touched.
    /// </summary>
    public string? BannerSuppressionKeyName { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MetadataSource MetadataSource { get; set; } = MetadataSource.Auto;

    /// <summary>
    /// 0 means "not chosen yet": the bridge picks a random port from the dynamic
    /// range on first run and saves it here. Anything that reaches this port can
    /// run script in Amazon Music's signed-in renderer, so the conventional 9222 is
    /// deliberately avoided.
    /// </summary>
    public int RemoteDebuggingPort { get; set; }

    /// <summary>
    /// Keep Amazon Music running with a debug port: relaunch it whenever it is
    /// found running without one, including when the user starts it normally.
    /// </summary>
    public bool AutoRelaunchAmazonMusic { get; set; } = true;

    /// <summary>
    /// Off by default: covers stay in memory only and nothing is written to disk.
    /// Turning it on trades disk for working offline and for skipping repeat downloads.
    /// </summary>
    public bool KeepArtworkCache { get; set; }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string LocalFolder
    {
        get
        {
            try
            {
                // Packaged app: per-user state that survives reinstall of the layout.
                return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            }
            catch (Exception)
            {
                return AppContext.BaseDirectory;
            }
        }
    }

    private static string FilePath => Path.Combine(LocalFolder, "settings.json");

    public static Settings Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return new Settings();

            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Write($"settings load failed, using defaults: {ex.Message}");
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch (Exception ex)
        {
            Log.Write($"settings save failed: {ex.Message}");
        }
    }
}
