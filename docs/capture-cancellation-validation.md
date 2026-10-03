# Capture cancellation validation

Run the regression tests with .NET 8:

```sh
dotnet test Tests/CaptureController.Tests.csproj --configuration Release
```

The Windows plugin can also be cross-compiled on Linux with `dotnet build NINASonyCameraPlugin.csproj --configuration Release -p:EnableWindowsTargeting=true`. The local NINA install step runs only on Windows. Cross-compilation does not allow running the plugin or native driver on Linux.

These tests compile the production capture controller directly and replace native camera calls with controlled callbacks. They cover busy and unknown states, native cancellation being opt-in, cancelled and failed captures never reporting an image ready, cancellation during status reads, repeated aborts, abort during a blocked start, concurrent starts, and an old abort arriving after a new exposure starts. CI runs these checks before building the Windows plugin DLL.

The tests do not establish the stability of SonyMTPCamera.dll or the Windows MTP stack. Complete the following checks in NINA with a real camera before releasing 1.0.0.5. Record camera model, native driver version, Windows version, NINA version, plugin build commit, and relevant log timestamps with the results.

| Check | Expected result | Result |
| --- | --- | --- |
| Fresh profile | Enable native cancel is off. | Pending |
| Change setting, reconnect, restart NINA, switch profiles | Setting persists per profile and takes effect on the next abort without recreating the camera driver. | Pending |
| Native cancel off: take several short exposures | Every exposure returns its own ARW image; the next capture starts normally. | Pending |
| Native cancel off: abort a long Bulb exposure | NINA stops waiting. The camera finishes the exposure naturally. Starting again while busy reports an error; after completion, the next exposure succeeds. | Pending |
| Native cancel off: repeatedly abort and rapidly request another exposure | One soft-cancel warning per exposure, no overlapping native start/cancel calls, and no NINA crash. | Pending |
| Native cancel off: stop a looping sequence and restart after the camera finishes | No cancelled frame is delivered as the new exposure. | Pending |
| Native cancel on: abort short and Bulb exposures, including repeated aborts | At most one native cancel request per exposure. NINA reports cancellation and does not return a cancelled image. Verify stability on the specific camera/driver combination. | Pending |
| Switch native cancel off again while connected | Abort returns to letting the camera finish naturally. | Pending |
| Normal LiveView, ISO selection, and reconnect | Existing behavior remains functional. | Pending |

A successful native cancel call only means the request was issued; it does not prove the camera stopped. The next start always checks the reported state. Managed exception handling cannot recover from a native access violation.

The maintainer's previous release prerequisite is satisfied in the published NINA manifest: [version 1.0.0.4 was added on December 12, 2025](https://github.com/isbeorn/nina.plugin.manifests/commit/d0ba770de8a26a5146b9ee8af7800c3293925d00). Merge and release remain maintainer decisions.
