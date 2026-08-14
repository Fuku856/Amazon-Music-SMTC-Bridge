using Microsoft.Win32;

namespace AmazonMusicSmtc;

/// <summary>
/// Turns Windows' notification banner off for Amazon Music, and puts it back.
/// </summary>
/// <remarks>
/// The bridge cannot suppress a banner from inside the notification listener:
/// NotificationChanged fires only once Windows has already drawn it, so
/// RemoveNotification clears the Action Center entry and nothing else. Whether a
/// banner appears at all is the shell's decision, and this per-app value is the
/// switch it reads - the same one behind Settings > System > Notifications >
/// Amazon Music > "Show notification banners".
///
/// Only ShowBanner is touched. Enabled and ShowInActionCenter would also stop the
/// toast from ever reaching UserNotificationListener, and that toast is where the
/// bridge reads title and album from: silencing the banner must not cost the
/// metadata.
/// </remarks>
internal static class NotificationBannerSuppressor
{
    private const string SettingsKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    private const string ShowBannerValue = "ShowBanner";

    /// <summary>
    /// The application id half of the AUMID Amazon Music publishes toasts under.
    /// Deliberately not <c>AmazonPaths.ResolveAppId</c>: that returns the id from
    /// the package manifest (AmazonMobileLLC.AmazonMusic), which is what the SMTC
    /// session uses, while the toasts come from the Win32 half of the same package
    /// under an id that resolves to no installed application.
    /// </summary>
    private const string ToastAppId = "Amazon.Music";

    /// <summary>
    /// MSIX redirects a packaged app's HKCU writes into a per-package hive, so this
    /// only reaches the real setting because the manifest opts out with
    /// desktop6:RegistryWriteVirtualization - which Windows honours from 1903
    /// onwards. Below that the element is ignored and the write would land in the
    /// private hive, changing nothing, so it is not attempted.
    /// </summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362);

    public static void Apply(Settings settings, Action<string> log)
    {
        if (!IsSupported)
        {
            log("banner suppression needs Windows 10 1903 or later; leaving notification settings alone");
            return;
        }

        var name = ResolveKeyName(log);
        if (name is null)
            return;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{SettingsKey}\{name}", writable: true);
            if (key is null)
            {
                log($"could not open notification settings for {name}");
                return;
            }

            // Recorded once, at the transition. Re-asserting on every start must not
            // capture the bridge's own 0 as the value to restore.
            if (!settings.BannerSuppressionApplied)
            {
                settings.PreviousShowBannerValue = key.GetValue(ShowBannerValue) as int?;
                settings.BannerSuppressionApplied = true;
            }

            key.SetValue(ShowBannerValue, 0, RegistryValueKind.DWord);
            log($"notification banners turned off for {name}");
        }
        catch (Exception ex)
        {
            log($"could not turn Amazon Music's notification banners off: {ex.Message}");
        }
    }

    public static void Restore(Settings settings, Action<string> log)
    {
        if (!settings.BannerSuppressionApplied)
            return;

        var name = ResolveKeyName(log);

        try
        {
            using var key = name is null
                ? null
                : Registry.CurrentUser.OpenSubKey($@"{SettingsKey}\{name}", writable: true);

            if (key is not null)
            {
                // The value is deleted rather than set to 1 when there was nothing
                // there before, so Windows goes back to its own default instead of
                // inheriting a choice the user never made.
                if (settings.PreviousShowBannerValue is { } previous)
                    key.SetValue(ShowBannerValue, previous, RegistryValueKind.DWord);
                else
                    key.DeleteValue(ShowBannerValue, throwOnMissingValue: false);

                log($"notification banners restored for {name}");
            }
        }
        catch (Exception ex)
        {
            log($"could not restore Amazon Music's notification banners: {ex.Message}");
        }

        // Cleared even when the write failed: holding a stale "applied" would make
        // the next Apply record the bridge's own 0 as the user's value.
        settings.BannerSuppressionApplied = false;
        settings.PreviousShowBannerValue = null;
    }

    /// <summary>
    /// The subkey Windows keeps Amazon Music's notification settings under. Resolved
    /// against what is actually registered rather than assembled blind, because the
    /// application id is Amazon's to choose.
    /// </summary>
    private static string? ResolveKeyName(Action<string> log)
    {
        var family = AmazonPaths.PackageFamilyName();
        if (family is null)
        {
            log("Amazon Music's package folder was not found; cannot find its notification settings");
            return null;
        }

        var expected = $"{family}!{ToastAppId}";

        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(SettingsKey);
            if (settings is not null)
            {
                var names = settings.GetSubKeyNames();

                if (names.Contains(expected, StringComparer.OrdinalIgnoreCase))
                    return expected;

                // The package registers a second AUMID for its SMTC session, so this
                // is only reached when the toast publisher has been renamed.
                var other = names.FirstOrDefault(n =>
                    n.StartsWith(family + "!", StringComparison.OrdinalIgnoreCase));

                if (other is not null)
                {
                    log($"using {other} for notification settings");
                    return other;
                }
            }
        }
        catch (Exception ex)
        {
            log($"could not enumerate notification settings ({ex.Message}); using {expected}");
        }

        // Nothing registered yet: Windows creates the key on the first toast, and
        // writing it ahead of that works just as well.
        return expected;
    }
}
