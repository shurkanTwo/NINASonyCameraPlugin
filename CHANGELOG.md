# Sony Camera Plugin

## 1.0.0.7 — exposure error classification and release maintenance

- Use GitHub handles in plugin descriptions, author/contributor display metadata and the packaged source notice. Preserve the original copyright notice and commit authorship.
- Correct README version, release status and installation links; retain the explicit limits of automated testing and manual distribution.
- Report a rejected busy capture-start through NINA's CameraExposureFailedException path, preserving the original message and cause. This avoids the duplicate generic-error notification path in NINA 3.2 while retaining capture rejection, prior cancellation and the existing soft-abort warning. Actual UI confirmation is still needed for the full popup report in this fork's issue #5.
- Publish a fresh versioned build; older release assets and checksums remain intact.

See [the session review](docs/SESSION-REVIEW-1.0.0.7.md) and [manual installation/rollback](INSTALLATION.md). Physical camera/USB/MTP, LiveView, lens operation and complete Sky Flat behavior remain unverified. NINA's plugin manager still uses the upstream release source.

## 1.0.0.6 — corrective community release

Follow-up to the 1.0.0.5 audit, retaining all previously integrated contributions and @dougforpres's original authorship. Install manually using [INSTALLATION.md](INSTALLATION.md).

- Preserve aborts received during capture-start preflight. An aborted pending start cannot launch an exposure after its status check returns; a rejected busy start preserves cancellation of the preceding capture.
- Verify the specific nearest learnt shutter step using the native driver's float rounding and absolute-distance selection, replacing the broad ratio that accepted a stalled adjacent speed.
- Retry a valid previous Bulb readback when switching to a short numeric exposure. Malformed readbacks still fail, and a persistent mismatch cannot start capture.
- Restore the repeated-failure camera-loss protection adapted from @totoantibes's upstream #13: three consecutive qualifying native errors mark the camera disconnected and block all further native access until NINA restarts. Never-working properties, explicit unsupported results, lock contention and shutter-verification mismatches do not count. A successful camera operation resets the count. No native close, retry or automatic reconnection is attempted after the guard trips.
- Add regression coverage using the production capture controller and managed native wrappers, replacing only native delegates and NINA logging/notifications for portable tests.

See [the corrective review](docs/RELEASE-REVIEW-1.0.0.6.md). Physical camera/USB operation remains unverified. This was published first as a preview and subsequently marked regular/Latest without changing the binaries; updates are installed manually.

## 1.0.0.5 — community preview

First maintained release from [@shurkanTwo’s fork](https://github.com/shurkanTwo/NINASonyCameraPlugin), preserving @dougforpres’s original authorship and MPL-2.0. Install manually using [INSTALLATION.md](INSTALLATION.md). This preview is not distributed through NINA’s plugin manager.

- Integrate current capture start/abort coordination and an opt-in managed Sony camera simulator with full-plugin integration tests (@shurkanTwo, upstream #10/#15).
- Report the latest downloaded ARW’s Sony camera temperature in NINA and that image’s metadata; missing readings remain unavailable (@shurkanTwo, #18 / issue #1).
- Release native image and metadata allocations after copying, including error paths, avoiding a per-frame unmanaged memory leak (@totoantibes, #14).
- Retry and verify shutter changes for back-to-back exposures. A readable shutter mismatch now fails before capture rather than silently using the previous exposure. Exclude AUTO/Multi Frame NR from gain choices and fix native driver version comparisons (adapted from @totoantibes’s #13 / issue #5).
- Serialize native camera/lens calls with bounded waits. Execution timeout blocks further native access until NINA restarts. Keep native cancellation off by default and always soft-cancel during image readout/processing (adapted #13, preserving current #10 safeguards / issue #4).
- Return focuser capability information without throwing (@christian-photo, #11), improve focuser state cleanup and notifications (@totoantibes, #13).
- Document lens-learning tools and camera SD-card saving (@shurkanTwo, #16/#17 / issues #9/#3).
- Add Windows-tested manual-install ZIP/DLL packaging, checksums, source/build provenance, and installation/rollback instructions.

Reviewed but deferred: experimental USB auto-reconnection, retrying a still-running timed-out native call, forced cancellation after repeated busy starts, and closed WIP #12 video intervals. Existing NINA 3.2/empty-ISO fixes from 1.0.0.4 (including @naixx’s contribution) remain included. See the [full review and issue coverage](docs/UPSTREAM-REVIEW-1.0.0.5.md).

Validation uses automated tests and Windows CI. Physical camera/USB operation for this combined release has not been verified. Native access violations, newer camera support, and complete NINA Sky Flat behavior are not claimed fixed. Existing legacy-package NU1701 restore warnings remain.

## 1.0.0.4
* Added the `UpdateSubSampleArea` implementation and bumped the minimum NINA version so the plugin loads in 3.2.
* Restored the ISO/Gain UI by notifying NINA whenever the camera connection updates ISO data and by handling cameras without ISO option lists.
* Probed the registry ISO property (`0xFFFE`) so older Sony bodies still populate the gain dropdown.
* Logged clearer errors when gain min/max cannot be determined and improved the GitHub workflow to publish the release DLL artifact.

## 1.0.0.3
Updated to support new device property "DisplayName" required in NINA 3

## 1.0.0.2
Rebuilt using .NET Core 8 for NINA 3

## 1.0.0.1
Initial release - Supports all functionality provided by the ASCOM version, plus:
* LiveView support
* Actual ISO values displayed in Gain drop-down vs a numeric index (1, 2, 3, etc)

Note that NINA saves the raw ARW files and does not generate FITS files for DSLRs (per the note in NINA: Options.Imaging).
