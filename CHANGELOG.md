# Sony Camera Plugin

## 1.0.0.5 — combined community maintenance release

First consolidated maintained release from [@shurkanTwo's fork](https://github.com/shurkanTwo/NINASonyCameraPlugin), combining the reviewed work since upstream 1.0.0.4. @dougforpres's original authorship, MPL-2.0 and the original contributor commits are preserved. Install manually using [INSTALLATION.md](INSTALLATION.md); NINA's plugin manager still uses the upstream release source.

- Integrate current capture start/abort coordination and the opt-in managed Sony camera simulator with full-plugin integration tests (@shurkanTwo, upstream #10/#15). Preserve aborts received during capture-start preflight and cancellation of a preceding capture when a busy start is rejected.
- Report the latest downloaded ARW's Sony camera temperature in NINA and that image's metadata; missing readings remain unavailable (@shurkanTwo, #18 / issue #1).
- Release native image and metadata allocations after copying, including error paths, avoiding a per-frame unmanaged memory leak (@totoantibes, #14).
- Retry and verify the specific nearest learned shutter step using the native driver's rounding and absolute-distance selection. Handle numeric/Bulb transitions and fail persistent mismatches before capture. Exclude AUTO/Multi Frame NR from gain choices and correct native driver version comparisons (adapted from @totoantibes's #13 / issue #5).
- Serialize native camera/lens calls with bounded waits. A native execution timeout or three consecutive qualifying native errors blocks further native access until NINA restarts. Unsupported/never-working properties, lock waits and managed verification failures are excluded from the error count. A successful camera operation resets the count. Keep native cancellation off by default and always soft-cancel during readout/processing (adapted #13, preserving current #10 safeguards / issue #4).
- Return focuser capability information without throwing (@christian-photo, #11), and improve focuser state cleanup and notifications (@totoantibes, #13).
- Report rejected busy capture starts through NINA's CameraExposureFailedException path, preserving the original message and cause. This avoids the duplicate generic-error notification path identified in NINA 3.2 while retaining capture rejection, prior cancellation and the existing soft-abort warning. The complete UI popup report in this fork's issue #5 still needs confirmation.
- Document lens-learning tools and camera SD-card saving (@shurkanTwo, #16/#17 / issues #9/#3).
- Provide Windows-tested manual-install ZIP/DLL packaging, checksums, exact source/build provenance, and installation/rollback instructions. Authored display metadata and documentation use GitHub handles; original legal notices remain unchanged.

Existing NINA 3.2/empty-ISO fixes from 1.0.0.4, including @naixx's contribution, remain included. Experimental USB auto-reconnection, retries of still-running timed-out native calls, forced cancellation after repeated busy starts, and closed WIP #12 video intervals remain deferred.

This combined package replaces the earlier maintenance builds labelled 1.0.0.5, 1.0.0.6 and 1.0.0.7. Their release entries and tags are retired; the original commits remain in history. If you installed an earlier build, download this package again and replace the DLL manually. The lower consolidated version includes all reviewed fixes.

See the [session review](docs/SESSION-REVIEW.md), [corrective review](docs/CORRECTIVE-REVIEW.md) and [upstream review and issue coverage](docs/UPSTREAM-REVIEW.md). Validation uses automated tests and Windows CI. Physical Sony camera/USB/MTP, LiveView, lens operation and complete Sky Flat behavior remain unverified. Native access violations and newer camera support are not claimed fixed. Existing legacy-package NU1701 restore warnings remain.

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
