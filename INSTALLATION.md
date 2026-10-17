# Manual installation of the maintained Sony Camera Plugin

These instructions install the community-maintained build from
[shurkanTwo/NINASonyCameraPlugin](https://github.com/shurkanTwo/NINASonyCameraPlugin/releases).
The original plugin was developed by **Doug Henderson**. This fork is maintained
by **Lucas Lepski and contributors** while shared upstream maintenance is being discussed.

The first community release, **1.0.0.5**, is a preview for manual testing. Windows
builds and automated tests are checked; physical Sony camera/USB operation has
not been verified for this combined release. See the release notes for scope.

## Requirements

- Windows 10 1809 or newer and N.I.N.A. 3.2 or newer (minimum 3.2.0.3001).
- Install the [Sony ASCOM driver package](https://github.com/dougforpres/ASCOMSonyCameraDriver/releases), version 1.0.1.17 or newer; 1.0.1.21 is the currently documented package. The native `SonyMTPCamera.dll` is a separate dependency and is not bundled here. You select the direct **Sony** camera in NINA; the package supplies the shared native components.
- Set the camera to PC Remote/MTP, manual exposure and focus, and RAW output. Close other applications that control it.

## Install or replace an existing version

1. Download **`SonyCameraPlugin-1.0.0.5.zip`** from the [1.0.0.5 release](https://github.com/shurkanTwo/NINASonyCameraPlugin/releases/tag/1.0.0.5). Choose the release asset, rather than GitHub's automatic “Source code” archives. Extract it to a temporary folder. The plugin is `NINA.RetroKiwi.Plugin.SonyCamera.dll`.
2. Disconnect the camera and focuser in NINA, then **close every NINA window** before changing plugin files.
3. Press **Win+R**, enter `%LOCALAPPDATA%\NINA\Plugins`, and open the plugin-version folder used by your NINA installation. NINA 3.x installations commonly use **`3.0.0`**: this folder tracks plugin compatibility, so it need not match the NINA version in About. If several version folders exist, use the one in NINA's startup log (`%LOCALAPPDATA%\NINA\Logs`); look for the path from which the Sony plugin was loaded.
4. If Sony Camera Plugin is already installed, locate its existing `NINA.RetroKiwi.Plugin.SonyCamera.dll` and copy the entire containing plugin folder **outside `%LOCALAPPDATA%\NINA\Plugins`** as a backup. Replace the DLL **in that existing folder**, even if the folder name differs from the example. Do not create another plugin folder containing a second copy. Keep the rest of the existing folder's files.
5. For a fresh installation, create a folder named **`Sony Camera Plugin`** in the active plugin-version folder. The typical complete path is:

   ```text
   %LOCALAPPDATA%\NINA\Plugins\3.0.0\Sony Camera Plugin\NINA.RetroKiwi.Plugin.SonyCamera.dll
   ```

6. Copy the extracted DLL into the existing or newly created folder. The ZIP also contains the license, source reference, and these instructions; you can keep those files alongside it. It contains no replacement NINA runtime libraries or native Sony driver.
7. If Windows shows an **Unblock** checkbox in the downloaded ZIP or DLL's Properties, unblock the file. Alternatively run `Unblock-File` in PowerShell on that downloaded ZIP/DLL before copying/extracting it.
8. Restart NINA. In its plugin manager, confirm **Sony Camera Plugin 1.0.0.5** is loaded; the description identifies the community-maintained build. Select the Sony camera in Equipment and connect it.
9. Leave **Enable native cancel** disabled initially. Try one short exposure and check the downloaded image, ISO and exposure before using the build for a sequence. Temperature appears only after downloading an ARW that contains a supported Sony temperature reading.

This is a replacement for the upstream plugin, with the same identifier so existing
profile settings can be retained. **Use only one copy in the active plugin folder.**
This fork is currently distributed manually. The NINA plugin manager still uses the
upstream release source; it will not automatically install updates from this fork.
Check this fork's releases for newer builds. If you install an upstream version
through the plugin manager later, check which version is loaded afterwards.

## Verify the download

The release includes **`SHA256SUMS.txt`** for its ZIP and standalone DLL. In
PowerShell, calculate a downloaded file's hash and compare it to the matching entry:

```powershell
Get-FileHash "$env:USERPROFILE\Downloads\SonyCameraPlugin-1.0.0.5.zip" -Algorithm SHA256
```

## Roll back

Disconnect equipment and close NINA. Replace the maintained DLL with the DLL from
your backup in the same plugin folder, or remove this fork's copy and reinstall
Sony Camera Plugin through NINA's plugin manager. Keep the backup outside the
plugins directory to avoid duplicate loading. Restart NINA and confirm the loaded
version. The separate Sony driver installation and your images do not need changing.

## Troubleshooting

- **Plugin does not load:** check the version requirement, native driver installation, Windows file blocking, duplicate DLL copies, and NINA's startup log. Verify the folder used by this specific NINA installation.
- **Missing ISO choices:** use the native driver's `SonyCameraInfo64.exe /s` learning/scanning procedure. This release offers manual ISO choices; AUTO and Multi Frame NR modes are excluded.
- **Cannot set the requested exposure:** the shutter change is retried before capture. If it cannot be verified, the exposure fails rather than being silently captured at the previous shutter speed. A camera without learnt shutter settings still uses its manually selected speed; learn its settings before using automatic flats.
- **Abort appears to leave the camera busy:** soft cancellation stops NINA's wait while the camera finishes its current exposure. Wait for it to finish before starting another. Reading/processing is always soft-cancelled.
- **Driver stopped responding:** close NINA, power-cycle the camera, and restart NINA before reconnecting. The plugin deliberately blocks further native calls after an execution timeout. USB unplugging and power-off while connected can still cause native crashes; disconnect in NINA first.

Report problems with this build in [this fork's issue tracker](https://github.com/shurkanTwo/NINASonyCameraPlugin/issues).
Include camera/lens model, NINA/plugin/native-driver versions, and relevant logs
with private paths and device identifiers removed.
