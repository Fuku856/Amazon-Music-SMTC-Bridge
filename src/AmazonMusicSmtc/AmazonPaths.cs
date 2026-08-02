using Windows.Management.Deployment;

namespace AmazonMusicSmtc;

/// <summary>
/// Amazon Music ships as both an MSIX (Store) build and a plain installer build.
/// The Store build's %LOCALAPPDATA% writes are virtualised into its package
/// container, so the artwork cache lives in two quite different places.
/// </summary>
internal static class AmazonPaths
{
    private const string PackageNamePrefix = "AmazonMobileLLC.AmazonMusic_";

    /// <summary>AUMID fragment identifying Amazon Music's own SMTC session.</summary>
    public const string SessionIdFragment = "AmazonMobileLLC.AmazonMusic";

    /// <summary>Session id used by the non-Store build.</summary>
    public const string SessionIdExe = "Amazon Music.exe";

    /// <summary>Process name (no extension) of Amazon Music's browser process.</summary>
    public const string MainProcessName = "Amazon Music";

    /// <summary>
    /// Returns candidate "Data" directories, Store build first. Both are returned
    /// because a machine can have either build installed.
    /// </summary>
    public static IEnumerable<string> DataDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var dir in PackageDataRoots(localAppData))
        {
            var candidate = Path.Combine(dir, "LocalCache", "Local", "Amazon Music", "Data");
            if (Directory.Exists(candidate))
                yield return candidate;
        }

        var unpackaged = Path.Combine(localAppData, "Amazon Music", "Data");
        if (Directory.Exists(unpackaged))
            yield return unpackaged;
    }

    private static IEnumerable<string> PackageDataRoots(string localAppData)
    {
        var packagesRoot = Path.Combine(localAppData, "Packages");
        if (!Directory.Exists(packagesRoot))
            return [];

        try
        {
            // Resolve the package family hash at runtime rather than hardcoding it.
            return Directory.EnumerateDirectories(packagesRoot, PackageNamePrefix + "*");
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// The package family name, taken from the per-package data directory whose
    /// name is exactly that. Cheaper and less permission-sensitive than enumerating
    /// installed packages, and it works even while Amazon Music is not running.
    /// </summary>
    public static string? PackageFamilyName()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var dir in PackageDataRoots(localAppData))
            return Path.GetFileName(dir);

        return null;
    }

    /// <summary>
    /// Install location and AUMID application id for the Store build, or null when
    /// Amazon Music is not installed as a package.
    /// </summary>
    public static (string InstallLocation, string AppId)? ResolveInstalledPackage()
    {
        var family = PackageFamilyName();
        if (family is null)
            return null;

        try
        {
            var package = new PackageManager()
                .FindPackagesForUser(string.Empty, family)
                .FirstOrDefault();

            var location = package?.InstalledLocation?.Path;
            if (string.IsNullOrEmpty(location))
                return null;

            return (location, ResolveAppId(package!, family));
        }
        catch (Exception ex)
        {
            Log.Write($"package lookup failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The application id half of the AUMID ("family!appId"). Falls back to the
    /// package name, which is what Amazon Music's manifest happens to use.
    /// </summary>
    private static string ResolveAppId(Windows.ApplicationModel.Package package, string family)
    {
        // GetAppListEntries needs 2004; the bridge still supports 1809, where the
        // name-derived guess below is the only option.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            try
            {
                foreach (var entry in package.GetAppListEntries())
                {
                    var aumid = entry.AppUserModelId;
                    var bang = aumid.IndexOf('!');
                    if (bang >= 0 && bang < aumid.Length - 1)
                        return aumid[(bang + 1)..];
                }
            }
            catch (Exception)
            {
                // Fall through to the name-derived guess.
            }
        }

        var underscore = family.LastIndexOf('_');
        return underscore > 0 ? family[..underscore] : family;
    }

    /// <summary>
    /// The port Amazon Music's CEF DevTools server last reported.
    /// </summary>
    /// <remarks>
    /// Chromium writes this file when the DevTools HTTP handler starts and deletes
    /// it on a clean shutdown, so a stale file survives every crash - one holding a
    /// long-dead port was found on the development machine. Callers must treat the
    /// result as a hint and confirm the port actually answers before using it.
    /// </remarks>
    public static int? FindDevToolsPort()
    {
        foreach (var data in DataDirectories())
        {
            var file = Path.Combine(data, "App Cache", "DevToolsActivePort");

            try
            {
                if (!File.Exists(file))
                    continue;

                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                if (int.TryParse(reader.ReadLine()?.Trim(), out var port) && port is > 0 and <= 65535)
                    return port;
            }
            catch (Exception)
            {
                // Unreadable is the same as absent here.
            }
        }

        return null;
    }

    /// <summary>
    /// The notification artwork file. Amazon reuses ONE filename and overwrites it
    /// on every track change, so callers must copy it immediately on notification
    /// arrival - reading it later yields a different track's cover.
    /// </summary>
    public static string? FindNotificationArtwork()
    {
        foreach (var data in DataDirectories())
        {
            var cache = Path.Combine(data, "Artwork Cache");
            if (!Directory.Exists(cache))
                continue;

            string[] files;
            try
            {
                files = Directory.GetFiles(cache, "notification*.jpg");
            }
            catch (Exception)
            {
                continue;
            }

            if (files.Length == 0)
                continue;

            return files
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .First()
                .FullName;
        }

        return null;
    }
}
