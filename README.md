# AmazonMusic SMTC Bridge

[English](README.md) | [日本語](README.ja.md)

The Windows version of Amazon Music exposes only part of its information through SMTC (System Media Transport Controls), so SMTC-based scrobblers such as [Pano Scrobbler](https://github.com/kawaiiDango/pano-scrobbler) do not work correctly.

This app compensates for that by **publishing a separate SMTC session with complete metadata**.

---

## What is the problem? (measured)

Target: Amazon Music 9.5.2.2478 (Microsoft Store version)

What Amazon Music exposes through SMTC during playback:

| Field | Amazon Music | This app |
|---|---|---|
| Title | ❌ Usually empty (only some tracks have it) | ✅ Retrieved |
| Artist | ✅ | ✅ |
| AlbumTitle | ❌ Always empty | ✅ |
| AlbumArtist | ❌ Always empty | ✅ |
| Thumbnail (artwork) | ❌ Always missing | ✅ |
| Timeline (track length) | ❌ Always `00:00:00` | ✅ |

With an empty Title, a scrobbler cannot identify the track and therefore cannot scrobble it.

> **About Title**: It is usually empty. A very small number of tracks do have it, but in a 3-minute slow-playback test (skipping tracks every 25–30 seconds) it was **never populated once**.
> AlbumTitle / AlbumArtist / artwork / track length were **missing in every sample**.

## How it works

Amazon Music's session **cannot** be modified. SMTC metadata belongs to the publishing process, and `GlobalSystemMediaTransportControlsSessionManager` only provides read access and playback controls. This app therefore creates its own session.

There are two metadata sources, and by default the one that is available is selected automatically.

**CDP (default, recommended)** — Amazon Music is built with CEF (Chromium 79), so if it is restarted with `--remote-debugging-port`, the renderer can be read directly. The source is the internal Vuex store.

```
Amazon Music renderer (CDP)  ──> title / artist / album / artwork
                                  / playback position / track length (all at millisecond precision)
Amazon Music SMTC session    ──> play / pause state
                                        │
                                        ▼
                          this app's SMTC session (complete)
                                        │
                          control buttons are forwarded to Amazon Music
```

**Notifications (fallback)** — the previous method. It does not require restarting Amazon Music, but tracks are not updated while Amazon Music has focus (see below).

```
Amazon Music track-change toast ──> title / artist / album / artwork
Amazon Music SMTC session       ──> play / pause state
Amazon Music Local Storage      ──> track length (best effort)
```

> **About the album name**: With CDP, the app reads `track.album.name` from the store. The second link shown at the bottom of the player is the **playback context (playlist name)**, not the album, so it is intentionally not read.

### Session priority

Windows chooses the "current" SMTC session (the one used by media keys and most SMTC consumers); there is no API to pin a session. If Windows switches to Amazon Music's own session, the bridge briefly unregisters and re-registers its session so it becomes the most recent source again. This is best effort: the media UI may flicker briefly, and Amazon Music can take over again on a later playback event. You can turn this behavior off with **Session priority** in the tray menu.

---

## Requirements

- Windows 10 1809 or later / Windows 11
- Amazon Music (either the Microsoft Store version or the exe version)

If you use the notification fallback (the default `Automatic` and `Notifications only`), you additionally need:

- **Amazon Music Windows notifications enabled** (Settings > System > Notifications > Amazon Music)
- Permission for "access to notifications" on first launch

If you set the source to `Debug port only`, notifications are never used and the permission above is not requested.

## Installation

Download **only `AmazonMusic-SMTC-Bridge_v<version>_install.cmd`** from Releases and double-click it. It downloads the package, trusts the certificate (one administrator confirmation), installs the app, and starts it.

If you already downloaded the `.msix`, place it in the same folder and it will be used instead (the download is skipped). This only works for the `.msix` of the same version.

The installer is in English by default. On Japanese Windows it switches to Japanese automatically; you can force a language with `install.cmd -Lang ja` or `install.cmd -Lang en`.

### Manual installation

Because the package is self-signed, the certificate must be trusted first. In **an administrator PowerShell**:

```powershell
Import-Certificate -FilePath .\AmazonMusic-SMTC-Bridge_v1.0.0.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

Then in **a normal (non-administrator) PowerShell**:

```powershell
Add-AppxPackage .\AmazonMusic-SMTC-Bridge_v1.0.0.msix
```

Avoid double-clicking the `.cer`. The wizard defaults to importing the certificate into the current user's store, but MSIX signature validation only checks the local machine store, so installation fails with `0x800B0109` (root certificate not trusted). If you use the wizard, explicitly choose "Local Machine" → "Trusted Publishers".

## Using it alongside other media apps (important)

After installation, **two** SMTC sessions appear side by side. The native Amazon Music session remains empty, so in the tool that reads SMTC, **disable/block Amazon Music itself and enable this bridge**.

### Pano Scrobbler

Otherwise you will scrobble twice.

1. In Pano Scrobbler's app list, **disable Amazon Music**
2. **Enable AmazonMusic SMTC Bridge**

Pano Scrobbler shows a notification when it detects an unknown app, and you can enable the bridge from there.

### FluentFlyout

If you do not block Amazon Music itself in "App filtering", the empty session appears in the flyout and taskbar widget. The string to add to the blocklist is not `Amazon Music` (which you can select from the list) but **`AmazonMobileLLC`, typed manually**.

When Amazon Music exits, the bridge's session also disappears within about one second (the same applies to force-killing it). The only exception is while switching to a debug port; in that case the display is kept across the restart.

FluentFlyout matches app names and AUMIDs by **substring** (`IsSessionAllowed`). `AmazonMobileLLC` matches only Amazon Music's AUMID `AmazonMobileLLC.AmazonMusic_...!AmazonMobileLLC.AmazonMusic`, not this bridge (`AmazonMusicSmtc_...!App` / display name `AmazonMusic SMTC Bridge`).

> **If you are using v1.1.0 or earlier**
> The old version's display name was `Amazon Music SMTC Bridge`. Because FluentFlyout v2.14.0 now resolves session names from `shell:AppsFolder` (a side effect of adding Firefox-based browser support), the blocklist entry `Amazon Music` also matches the bridge and it disappears from the widget. Update this app to v1.2.0 or later, or change the blocklist entry to `AmazonMobileLLC` as described above.
> Note that FluentFlyout caches app names statically, so **FluentFlyout must be restarted** for either workaround to take effect.

## Settings

Right-click the taskbar tray icon:

- **Show log** — retrieval log (for bug reports)
- **Metadata source**
  - `Automatic` (default) — CDP if the debug port is available, otherwise notifications
  - `Debug port only` — CDP only. Never uses the notification listener
  - `Notifications only` — previous method. Does not restart Amazon Music or open a debug port
- **Restart with debug port** — switch manually
- **Always run with debug port** — default ON. If Amazon Music is found running without a debug port, it is restarted (including when you started it yourself)
- **Hide Amazon Music track-change notifications** — default OFF. Amazon Music notifications are not removed and keep accumulating in the notification center, so enable this to remove them automatically after processing
- **Save artwork for offline use** — default OFF. When ON, downloaded artwork is stored locally so it can be displayed offline (up to 100 items)
- **Language** — `English` / `日本語`. Default English; the change takes effect immediately

Auto-start is enabled by default. It can be toggled in Settings > Apps > Startup.

### About the debug port (important)

With a CDP setting, Amazon Music runs with `--remote-debugging-port`. In that state, **any process on the same PC can connect to Amazon Music's renderer without authentication and run arbitrary JavaScript in the signed-in session.**

As a mitigation, the app chooses a random port from the dynamic range on first run rather than the conventional 9222, and stores it in `settings.json`. This is **a mitigation, not a solution** (it can still be found by port scanning and `/json/version`).
If that is not acceptable, choose `Notifications only`.

---

## Limitations

CDP method (default):

- **Amazon Music is restarted.** If it is running without a debug port, it is replaced, so playback is interrupted and the playback position returns to the start of the track. This also applies immediately after you start it yourself (measured 13 seconds from detection to connection). If this is annoying, turn **Always run with debug port** OFF
- **There is a security trade-off with the debug port** (see above)
- **This depends on Amazon Music's internal implementation.** It currently reads the Vuex store of CEF 79 / Vue 2. If an app update makes the store unavailable, it falls back to reading the DOM, but in that case **the album name becomes empty** (the DOM only contains the playlist name, so the app returns empty rather than an incorrect value). If that also fails, it falls back to notifications
- **Track changes can be detected up to 2 seconds late.** Enabling protocol domains or keeping a script resident in the running app prevents Amazon Music from starting, so the app deliberately uses read-only polling

Notifications method:

- **It does not work if Amazon Music notifications are OFF**
- **Tracks are not updated while the Amazon Music window is in the foreground.** When Amazon Music has focus, it **does not publish** track-change toasts (they are not merely hidden; they are never created). In that state Amazon Music's own SMTC also does not expose the title, so there is no information to retrieve. **This is the reason the CDP method exists**
- **Track length is best effort.** If the track is not in Amazon Music's catalog cache, no length is available
- **Playback position is estimated.** It is calculated from the time elapsed since the track change, so seeking in Amazon Music causes the position to be wrong until the next track change

Common:

- **Nothing is published while Amazon Music is not running.** The app watches the process lifecycle directly, so the session does not remain after a normal exit or a force-kill. The only exception is while switching to a debug port, when the display is kept for 30–45 seconds. This grace period is purely time-based, so if you exit Amazon Music immediately after the switch, the session disappears when the grace period ends (the same applies if the switch fails)
- **Seek operations are not accepted.** With CDP the app can accurately *display* the playback position, but it does not forward seek requests from scrobblers to Amazon Music
- Behavior with Focus Assist (concentration mode) enabled has not been verified

## Verification status

Confirmed on real hardware (Amazon Music 9.5.2.0 / CEF 79.1.38 / Store version):

- **The bridge follows track changes even while Amazon Music is pinned in the foreground.** It was also confirmed that the notification artwork file's modification time did not change at all during the same period (i.e. no toast was being produced)
- The album name is the actual album, not the playlist name
- Playback position and track length from CDP appear in the SMTC timeline
- Artwork retrieval and display (fetched from the CDN, no authentication required)
- **Automatic replacement when Amazon Music is started normally** (13 seconds from detection to connection)
- Discovering an already-running Amazon Music on another port through `DevToolsActivePort` and reusing it (no unnecessary restart)
- Giving up after 3 failed start attempts and leaving a log entry
- `KeepArtworkCache` writes nothing to disk when it is OFF (the default)
- The bridge does not start on its own while Amazon Music is not running
- Retrieving title / artist / album / artwork from notifications
- Catch-up at startup (immediately reflecting the currently playing track)
- Synchronizing play / pause state
- Pause / Play from the bridge's session being forwarded to Amazon Music
- Retrieving track length and reflecting it in the SMTC timeline
- Generating a signed `.msix`
- The app stays resident after closing the log window (tested via both × and Alt+F4)

Not yet verified:

- **The bridge session disappearing when Amazon Music exits** (normal exit, force-kill, notifications-only mode). Implemented but not verified on real hardware
- **The display being kept during a restart caused by replacement.** Implemented but not verified on real hardware
- Installing the signed `.msix` in a clean environment (verified on the development machine using a registered layout)
- Actual scrobbling with Pano Scrobbler
- The non-Store (exe) version of Amazon Music — path resolution is implemented but not verified on real hardware. The CDP startup path is Store (MSIX) only
- Metadata during podcast / video playback
- Stability during long continuous operation (several hours)

---

## Building from source

Requirements: .NET 10 SDK, Developer Mode ON

```powershell
.\tools\build-dev.ps1
```

Because it uses `Add-AppxPackage -Register`, which provides a package identity without signing, no certificate is needed for iterative development (the `userNotificationListener` capability cannot be declared without a package identity, so an unpackaged exe does not work).

Signed package for distribution:

```powershell
.\tools\pack-release.ps1 -Version 1.0.0 -OutputName AmazonMusic-SMTC-Bridge_v1.0.0
```

### Releases (GitHub Actions)

Run the workflow manually from GitHub under **Actions > Release > Run workflow**. There are two ways to specify the version:

- **Create a new tag**: choose a branch and enter `v1.0.0` in "Version" (entering `1.0.0` without the `v` is equivalent; the tag will always be `v1.0.0`)
- **Select an existing tag**: choose an existing tag under "Use workflow from" and leave "Version" empty

It can also be run from the command line (specify the file name, not the workflow name):

```powershell
gh workflow run release.yml -f version=1.0.0
```

The workflow builds, signs, and packages the app, then attaches `AmazonMusic-SMTC-Bridge_v1.0.0.msix`, `AmazonMusic-SMTC-Bridge_v1.0.0.cer`, and `AmazonMusic-SMTC-Bridge_v1.0.0_install.cmd` to the release.

#### Pre-releases (beta)

You can add a pre-release identifier such as `v1.2.0-beta1`. A version specified in this form is always published as a pre-release, regardless of the "Publish as pre-release" checkbox.

The MSIX `Identity/Version` is **numeric only and has four parts**, so it cannot contain a string like `-beta1`. The identifier is therefore **folded entirely into a single revision number**. The only allowed identifiers are **`alpha` / `beta` / `rc` followed by a number from 1 to 9999** (an identifier without a number such as `-beta`, one with a separator such as `-rc.1`, or an undefined identifier such as `-preview1` produces an error).

Revisions are allocated **per series in blocks of ten thousand, plus the number** (`alpha` → 10000s, `beta` → 20000s, `rc` → 30000s). If only the number were used, `-alpha1` and `-beta1` would have the same package version, and installing one on a machine that already has the other would fail with `0x80073CFB` (the same version cannot be replaced). `install.cmd` cannot recover from this error either, and uninstalling (which loses settings and cache) would be required, so the series is included in the version number.

| Input | Tag / asset name | Package version |
|---|---|---|
| `v1.2.0-alpha1` | `v1.2.0-alpha1` | `1.2.0.10001` |
| `v1.2.0-beta1` | `v1.2.0-beta1` | `1.2.0.20001` |
| `v1.2.0-beta2` | `v1.2.0-beta2` | `1.2.0.20002` |
| `v1.2.0-rc1` | `v1.2.0-rc1` | `1.2.0.30001` |
| `v1.2.0` | `v1.2.0` | `1.2.0.0` |

> **Updating from beta to the final release**
> The final release's revision is `0`, so it is numerically lower than beta. `install.cmd` automatically retries with `-ForceUpdateFromAnyVersion`, so it can update in place, but **if you use `Add-AppxPackage` manually you need the same switch**:
>
> ```powershell
> Add-AppxPackage .\AmazonMusic-SMTC-Bridge_v1.2.0.msix -ForceUpdateFromAnyVersion
> ```

The installer is based on `pkg\install.cmd` and embeds the release's package download URL, file name, and signing certificate thumbprint. An `.msix` that does not match the embedded thumbprint will not be installed. **Each release gets its own file**, so the file name includes the version (generated automatically by `pack-release.ps1`).

Signing uses a fixed certificate registered in the following Secrets (`CN=AmazonMusicSmtc`, valid until 2036-08-01):

- `SIGNING_PFX_BASE64` — the `.pfx` encoded as Base64
- `SIGNING_PFX_PASSWORD` — the password for that `.pfx`

If these are not set, a new self-signed certificate is created on every run, and users would have to trust a different certificate on every update. If you replace the certificate, update both values together:

```powershell
$pw  = '<new password>'
$pfx = "$env:TEMP\signing.pfx"
Export-PfxCertificate -Cert Cert:\CurrentUser\My\<thumbprint> -FilePath $pfx `
  -Password (ConvertTo-SecureString $pw -Force -AsPlainText)
gh secret set SIGNING_PFX_BASE64   --body ([Convert]::ToBase64String([IO.File]::ReadAllBytes($pfx)))
gh secret set SIGNING_PFX_PASSWORD --body $pw
Remove-Item $pfx
```

A diagnostic script for inspecting the contents of SMTC:

```powershell
.\tools\dump-smtc.ps1
```
