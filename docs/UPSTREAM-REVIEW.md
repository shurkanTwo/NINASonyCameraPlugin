# Upstream contribution review

> The [corrective review](CORRECTIVE-REVIEW.md) records follow-up findings in cancellation, shutter verification and repeated-failure detection. It supersedes the affected assessment below, including the shutter-ratio check. All corrections are included in the combined 1.0.0.5 release.

Reviewed against upstream `master`, commit
`8bd9042bcd477fd028c2aa2adfe34260192de936` (upstream release 1.0.0.4).
This fork preserves @dougforpres's authorship, copyright notices, MPL-2.0,
and original commit history. Maintenance does not imply @dougforpres has reviewed or
endorsed this combined build. Upstream issues/PRs remain under upstream control.

## Included work

- [#10](https://github.com/dougforpres/NINASonyCameraPlugin/pull/10), @shurkanTwo: current capture coordination, idempotent cancellation, and per-profile native cancel opt-in. The prerequisite is included through #15, retaining its original commits. Additional regression checks now keep Reading/Processing soft-cancelled even when native cancel is enabled.
- [#11](https://github.com/dougforpres/NINASonyCameraPlugin/pull/11), @christian-photo: focuser capability checks and unsupported temperature compensation no longer throw.
- [#14](https://github.com/dougforpres/NINASonyCameraPlugin/pull/14), @totoantibes: release native image/metadata buffers in `finally` after copying, including errors. Native `DLL.cpp` was checked: `exportBytes` allocates with `CoTaskMemAlloc`, transferring the exported buffer to the caller. Capture COMPLETE exports a frame and cleans up the native capture; copying inline on the first COMPLETE result is required.
- [#15](https://github.com/dougforpres/NINASonyCameraPlugin/pull/15), @shurkanTwo: opt-in managed camera simulator, portable capture tests, and actual-plugin Windows integration tests.
- [#16](https://github.com/dougforpres/NINASonyCameraPlugin/pull/16), @shurkanTwo: obtaining and launching a configured lens-learning tool.
- [#17](https://github.com/dougforpres/NINASonyCameraPlugin/pull/17), @shurkanTwo: keeping RAW copies on the camera memory card.
- [#18](https://github.com/dougforpres/NINASonyCameraPlugin/pull/18), @shurkanTwo: bounded ARW camera-temperature extraction with independent metadata fixtures; merged with the simulator's connection/download paths. Integration checks verify that NINA's camera property and image metadata receive the same value and clear unavailable readings.

## Adapted stability work from @totoantibes's #13

[#13](https://github.com/dougforpres/NINASonyCameraPlugin/pull/13) includes older
copies of #10/#11. It was reviewed in full and adapted rather than merged over
the newer capture controller. @totoantibes retains credit for the exposure readback,
ISO cleanup, native serialization/timeout, and focuser hardening work.

Included:

- Serialize all native camera and focuser calls through a shared executor; bound native operations and lock waits. A native execution timeout permanently blocks further native calls, including close, rescan and reconnect, until NINA restarts. A late result cannot make `IsOpen` or `CameraInfo` report a usable camera.
- Keep the COMPLETE status/image copy inline. Consume the managed image once and discard leftover frames before the next capture and at close/open.
- Read back and retry shutter changes with a 12-second retry budget, within the surrounding 60-second native-start watchdog. The native driver picks by absolute shutter-time distance; a 1.17 ratio accepts the arithmetic midpoint of the measured 4/3 shutter gaps (worst half-step ratio 7/6) while rejecting the measured smallest full step, 1.20. Bulb selection uses the longest numeric shutter from the native driver’s learnt `0xFFFF` property (30 seconds as compatibility fallback), rather than the camera’s exposure maximum, which includes Bulb durations. A stale numeric setting cannot satisfy a Bulb request. A readable mismatch, failed retry, or invalid readback fails before starting capture. A missing learnt shutter list retains the previous manual-setting behavior with a warning.
- Offer sorted, distinct manual ISO values, excluding zero, AUTO (`0x00FFFFFF`), and Multi Frame NR values. Gain bounds use the same filtered options.
- Correct native DLL version component order and remove the redundant unresolved ReachFramework reference.
- Preserve focuser state notifications, reset moving state on failures, and avoid queueing polled reads indefinitely behind another operation.

Deferred after review:

- Clearing the “driver wedged” flag to retry while a prior timed-out native worker is still running. That permits overlapping native operations and defeats serialization.
- Automatic forced native cancellation after two busy starts. A long capture/readout may be valid; forced recovery bypassed the native-cancel preference and readout guard.
- Experimental USB removal notifications and automatic reconnection. The PR itself reports remaining abnormal exits, requires related native-driver work, and has not established compatibility with the published driver.
- Treating any intermediate shutter movement as proof that the requested exposure reached its nearest supported step, even after a later error or timeout.

## Issue coverage

- [#1 temperature](https://github.com/dougforpres/NINASonyCameraPlugin/issues/1): implemented from #18; temperature is available after a supported ARW is downloaded, rather than continuously.
- [#2 A6600 connection/empty ISO list](https://github.com/dougforpres/NINASonyCameraPlugin/issues/2): already fixed in 1.0.0.4; retained. Native DLL version checking and ISO filtering are improved here. No new camera model support is claimed.
- [#3 SD card copies](https://github.com/dougforpres/NINASonyCameraPlugin/issues/3): documented camera-side setting.
- [#4 cancel crashes](https://github.com/dougforpres/NINASonyCameraPlugin/issues/4): mitigations and regression checks included; native DLL/MTP access violations cannot be claimed fixed by managed tests.
- [#5 flat exposure changes](https://github.com/dougforpres/NINASonyCameraPlugin/issues/5): shutter application is checked/retried, and AUTO ISO is excluded. NINA Sky Flat behavior remains unverified; this release does not claim a complete Sky Flat fix.
- [#6 NINA 3.2 interface](https://github.com/dougforpres/NINASonyCameraPlugin/issues/6): already resolved by merged #7/#8 in 1.0.0.4, retained here. Original contributors include @naixx and @shurkanTwo.
- [#9 lens learning](https://github.com/dougforpres/NINASonyCameraPlugin/issues/9): tool location and launch requirements documented; the installer does not include a ready-to-use lens-learning executable.

Closed WIP [#12 video intervals](https://github.com/dougforpres/NINASonyCameraPlugin/pull/12)
is recorded but not revived as a finished feature in this maintenance release.

## Standards

The initial independent standards review found no non-tooling hard violations of
`.editorconfig`. It identified timeout/retry overlap, unbounded native cleanup,
unsafe forced cancel, old prerequisite regressions, and accepting failed shutter
application as release blockers. Those paths were replaced or deferred as above.

## Spec

The independent spec review compared each open PR to its body and linked issue.
The same native-call and shutter paths contradicted the PR's safety/correctness
promises. The release keeps the tested current capture controller and publishes
explicit limits instead of adopting those paths.

## Validation and limits

The repository's GitHub Actions build runs portable capture/simulator/native-call
policy tests, temperature metadata tests, actual-plugin integration tests on
Windows, and a Release build before packaging a manual-install ZIP, DLL and
SHA-256 checksums. The release links its successful build run and exact source
commit. Existing NU1701 warnings from legacy dependencies remain.

Automated native-call policy tests use controlled callbacks; simulator tests do
not execute the Sony DLL, USB/MTP, LiveView, or physical lenses. The metadata fixture
is not a complete decodable ARW. No physical-camera validation of this combined
release has been performed. @totoantibes's reported ILCE-7S field results apply to his
build, not automatically to this adapted integration.
