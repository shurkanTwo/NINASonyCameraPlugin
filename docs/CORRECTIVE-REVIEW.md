# Corrective review of the upstream integration

The corrections at [`0341e8f`](https://github.com/shurkanTwo/NINASonyCameraPlugin/tree/0341e8f9cf6e5e1c5b13352d485285e00300df0e) address all four findings from the follow-up audit of the initial integration at `413290f`. They are included in the consolidated 1.0.0.5 release, retaining the original contributor history and adapted work. @dougforpres remains the original author; @totoantibes's PR #13 remains credited as the source of the native stability and shutter retry work. The original MPL-2.0 license is unchanged.

## Corrections

1. **Abort during preflight:** a pending capture request is registered before the potentially blocking native status read. Abort cancels that request and the preceding capture's wait. If preflight succeeds, the cancelled pending request cannot start an exposure; if it rejects a busy camera, cancellation of the preceding capture remains intact. A delayed abort for a previous capture still cannot native-cancel a newer capture.
2. **Expected shutter step:** verification selects from the learned numeric shutter list using the native driver's float arithmetic, five-decimal rounding of both requests and learned values, absolute distance and shorter-step tie behavior. It compares the actual readback against the selected step, accepting equivalent fractions. The old global ratio could accept the wrong adjacent speed. The audit's 0.011439-second and 0.88-second examples now retry until they reach 1/80 and 0.8 seconds respectively.
3. **Old Bulb mode:** zero is a valid Bulb readback, including a temporary previous mode while switching to a numeric shutter. Such a mismatch now retries within the existing 12-second budget. Invalid nonzero encodings still fail immediately. A persistent mismatch fails without starting capture. Explicit native unsupported-exposure control retains the documented manual-shutter fallback; a successful native adjustment without a usable learned list cannot be verified and fails.
4. **Camera-loss guard:** three consecutive qualifying returned native errors stop the shared native executor, hide the camera from camera/focuser connection state, and block subsequent native access, including cleanup, enumeration and reopen, until NINA restarts. Successful camera operations reset the count. Property errors count only after that property has worked; explicit unsupported results are excluded. Managed validation/verification failures and lock waits are excluded. This restores the previously omitted protection from @totoantibes's PR #13 while retaining the stricter recovery policy: no automatic close/retry of a failed handle and no revival of a timed-out worker.

## Verification

The portable tests now compile SonyDriver and SonyMTPCameraDll directly. They replace only P/Invoke delegates, logging and notifications; the capture, verification, error classification, failure counting and executor logic under test are production code.

Regression cases cover blocked preflight with native cancel both enabled and disabled, busy rejection preserving cancellation, stale adjacent shutter steps, temporary and persistent Bulb mode, malformed readback, nearest-step rounding, equivalent fractions, unsupported exposure control, repeated status/start/exposure-setting errors, successful-operation reset, unknown/known property failures, explicit unsupported results, stale handles and blocking native access after loss.

The Windows workflow runs the portable capture/native-policy suite, ARW temperature suite, full-plugin simulator integration suite and release build before packaging the manual-install DLL/ZIP and SHA256 checksums. SOURCE.txt records the exact source commit and build run.

These checks do not establish physical Sony camera, USB/MTP, lens, LiveView or complete Sky Flat compatibility. The [session review](SESSION-REVIEW.md) subsequently corrected the exposure-failure classification, metadata and documentation. All of these fixes are included in the consolidated release; physical-camera validation is still required. Experimental USB reconnection, retries of still-running timed-out calls, forced cancellation of busy captures and closed WIP #12 video intervals remain deferred.

## Installation and history

Use [INSTALLATION.md](../INSTALLATION.md) and install one copy of the maintained plugin. This fork is not an automatic update source in NINA's plugin manager. Earlier build sources remain available through the immutable commit links above. Intermediate release entries and tags are retired in favor of one combined package; users of any earlier maintenance build should download the consolidated package again and replace the DLL manually.
