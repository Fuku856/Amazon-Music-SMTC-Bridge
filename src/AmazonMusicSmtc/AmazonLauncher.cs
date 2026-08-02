using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AmazonMusicSmtc;

/// <summary>
/// Restarts Amazon Music with CEF's remote debugging port open.
/// </summary>
/// <remarks>
/// Chromium can only start its DevTools server at process launch, so there is no
/// way to enable this on a running instance - it has to be replaced.
///
/// Passing arguments is the awkward part. Amazon Music is a Desktop Bridge package
/// (Executable="amazon music.exe", EntryPoint="Windows.FullTrustApplication") that
/// declares neither an AppExecutionAlias nor a protocol, and MSIX shortcuts cannot
/// carry arguments, so "shell:appsFolder\..." is a dead end. Invoke-CommandInDesktopPackage
/// is the documented way in: it creates the process with the package identity and
/// its virtualised filesystem, which is what keeps Amazon Music's sign-in, library
/// and - importantly - its SMTC AUMID unchanged.
///
/// It shells out to PowerShell rather than calling the underlying
/// IDesktopAppXActivator COM interface directly. That interface is undocumented
/// (its IID and vtable layout would have to be guessed), and a relaunch is a rare
/// enough event that one process spawn does not matter.
/// </remarks>
internal static class AmazonLauncher
{
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>True when Amazon Music's browser process is running.</summary>
    public static bool IsRunning() => GetProcesses().Length > 0;

    /// <summary>
    /// Picks a debug port from the dynamic range, to be remembered in settings.
    /// </summary>
    /// <remarks>
    /// CEF 79 reads --remote-debugging-port=0 as "disabled" rather than "choose one
    /// for me" - it was measured writing no DevToolsActivePort and never answering -
    /// so the bridge has to name a real port. Choosing one at random beats the
    /// conventional 9222: anything that reaches this port can run script in Amazon
    /// Music's signed-in renderer, and an unguessable port is at least not the first
    /// thing another local process would try.
    /// </remarks>
    public static int PickFreePort()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = Random.Shared.Next(49152, 65536);
            if (IsFree(candidate))
                return candidate;
        }

        return 9222;
    }

    private static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static Process[] GetProcesses()
    {
        try
        {
            // Exact name match, so "Amazon Music Helper" is left alone - it is a
            // separate background process and killing it is neither needed nor kind.
            return Process.GetProcessesByName(AmazonPaths.MainProcessName);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Asks Amazon Music to close, then kills whatever is left.
    /// </summary>
    public static void Stop(Action<string> log)
    {
        var processes = GetProcesses();
        if (processes.Length == 0)
            return;

        foreach (var process in processes)
        {
            try
            {
                process.CloseMainWindow();
            }
            catch (Exception)
            {
                // No window, or already gone.
            }
        }

        var deadline = DateTime.UtcNow + CloseGrace;
        foreach (var process in processes)
        {
            try
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero)
                    process.WaitForExit((int)remaining.TotalMilliseconds);

                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Racing with a process that exited on its own is fine.
            }
            finally
            {
                process.Dispose();
            }
        }

        log($"stopped Amazon Music ({processes.Length} process(es))");
    }

    /// <summary>
    /// Stops Amazon Music and starts it again with the debug port open.
    /// </summary>
    /// <param name="port">0 asks Chromium for an ephemeral port.</param>
    /// <returns>False when the package could not be resolved or PowerShell failed.</returns>
    public static bool Relaunch(int port, Action<string> log)
    {
        var package = AmazonPaths.ResolveInstalledPackage();
        if (package is not { } resolved)
        {
            log("!! cannot relaunch: Amazon Music is not installed as a package");
            return false;
        }

        var exe = Path.Combine(resolved.InstallLocation, AmazonPaths.SessionIdExe);
        var family = AmazonPaths.PackageFamilyName();
        if (family is null)
            return false;

        Stop(log);

        var command =
            $"Invoke-CommandInDesktopPackage -PackageFamilyName {Quote(family)} " +
            $"-AppId {Quote(resolved.AppId)} -Command {Quote(exe)} " +
            $"-Args {Quote($"--remote-debugging-port={port}")}";

        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList =
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy", "Bypass",
                    "-Command", command,
                },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });

            if (process is null)
            {
                log("!! relaunch failed: PowerShell did not start");
                return false;
            }

            if (!process.WaitForExit((int)LaunchTimeout.TotalMilliseconds))
            {
                log("!! relaunch timed out");
                return false;
            }

            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd().Trim();
                log($"!! relaunch failed (exit {process.ExitCode}): {Truncate(error)}");
                return false;
            }

            log($"relaunched Amazon Music with --remote-debugging-port={port}");
            return true;
        }
        catch (Exception ex)
        {
            log($"!! relaunch failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Single-quoted PowerShell literal; inner quotes are doubled.</summary>
    private static string Quote(string value) => $"'{value.Replace("'", "''")}'";

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "...";
}
