using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.RetroKiwi.Plugin.SonyCamera.Drivers {
    // Keep capture coordination independent of NINA and the native DLL so races can be tested without a camera.
    internal sealed class CaptureController {
        internal const uint StatusUnknown = 0xFFFFFFFF;
        private const uint Created = 0x0000;
        private const uint Capturing = 0x0001;
        private const uint Failed = 0x0002;
        private const uint Cancelled = 0x0003;
        private const uint Complete = 0x0004;
        private const uint Starting = 0x8001;
        private const uint Reading = 0x8002;
        private const uint Processing = 0x8003;

        internal enum AbortResult { AlreadyRequested, NativeCancelRequested, SoftCancelRequested, NativeCancelDeferred }

        private readonly object captureLock = new object();
        private readonly object requestLock = new object();
        private readonly Func<uint> getStatus;
        private readonly Action<float> startCapture;
        private readonly Func<bool> cancelCapture;
        private readonly Func<bool> nativeCancelEnabled;
        private readonly int cancelLockTimeoutMs;
        // Back-to-back short exposures wedge the camera: it reports capturing (1)
        // and never leaves, so every later start is refused until a reconnect.
        // Observed on a7S flat runs at 0.01174s; the exposure search just before
        // them succeeded only because its steps happened to be ~9s apart.
        private readonly Func<int> minCaptureIntervalMs;
        // Clears a wedged capture without a reconnect. A brief LiveView does it;
        // the native cancel that would otherwise be needed is off by default
        // because it crashes the driver.
        private readonly Func<bool> recoverStuckCapture;
        private readonly TimeProvider clock;
        private readonly Action<int> sleep;
        // Reports a wait that actually happened, so the guard is visible in the
        // log instead of having to be inferred from capture timestamps.
        private readonly Action<int> onWaited;
        // Reports why an abort took the path it did. Abort has six exits and the
        // log said only "native cancel unavailable" for all of them, which left
        // a disabled setting and a contended lock indistinguishable - and a
        // capture that nothing ever cancels is what ends a night.
        private readonly Action<string> onAbortDecision;
        // Reports a wait for an aborted exposure to finish on the camera. Its
        // own line, not the quiet gap's: two different reasons to wait, and a
        // log that cannot tell them apart is what made this fault take a day.
        private readonly Action<int> onWaitedForAbortedCapture;
        // Closes and reopens the camera in place. The only recovery that has ever
        // worked on this body, and the host never sees it disconnected.
        private readonly Func<bool> recoverCamera;
        // After this many exposures in a row deliver nothing, stop touching the
        // camera at all. See RefuseIfTooManyFailures for why that is the point.
        private readonly Func<int> maxConsecutiveFailures;
        private readonly Action<string> onRecovery;
        private readonly Func<bool> recoveryEnabled;
        private int consecutiveFailures;
        // How long a capture this thread has just started may take to show up
        // in the status before the deferred cancel gives up on it.
        private readonly int captureAnnounceTimeoutMs = 10000;
        private long lastCaptureEndTimestamp;
        // When the shutter opened, and for how long it was asked to stay open.
        // Taken after the native start returns, because that call does the
        // exposure-time change first: walking the camera's shutter ladder from
        // 1/80 to 20s is about thirty seconds of stepping, and the exposure only
        // begins once it is done.
        private long captureStartTimestamp;
        private double captureSeconds;
        private long abortedCaptureStartTimestamp;
        private double abortedCaptureSeconds;
        private bool haveAbortedCapture;
        private bool haveCaptureEnd;
        private sealed class CaptureRequest {
            internal int CancelRequested;
        }
        private CaptureRequest currentRequest = new CaptureRequest();
        private CaptureRequest pendingRequest;

        internal CaptureController(Func<uint> getStatus, Action<float> startCapture,
            Func<bool> cancelCapture, Func<bool> nativeCancelEnabled, int cancelLockTimeoutMs = 1000,
            Func<int> minCaptureIntervalMs = null, Func<bool> recoverStuckCapture = null,
            TimeProvider clock = null, Action<int> sleep = null, Action<int> onWaited = null,
            Action<string> onAbortDecision = null, Action<int> onWaitedForAbortedCapture = null,
            Func<bool> recoverCamera = null, Func<int> maxConsecutiveFailures = null,
            Action<string> onRecovery = null, Func<bool> recoveryEnabled = null) {
            this.getStatus = getStatus;
            this.startCapture = startCapture;
            this.cancelCapture = cancelCapture;
            this.nativeCancelEnabled = nativeCancelEnabled;
            this.cancelLockTimeoutMs = cancelLockTimeoutMs;
            this.minCaptureIntervalMs = minCaptureIntervalMs ?? (() => 0);
            this.recoverStuckCapture = recoverStuckCapture;
            this.clock = clock ?? TimeProvider.System;
            this.sleep = sleep ?? Thread.Sleep;
            this.onWaited = onWaited;
            this.onAbortDecision = onAbortDecision;
            this.onWaitedForAbortedCapture = onWaitedForAbortedCapture;
            this.recoverCamera = recoverCamera;
            this.maxConsecutiveFailures = maxConsecutiveFailures ?? (() => 0);
            this.onRecovery = onRecovery;
            this.recoveryEnabled = recoveryEnabled ?? (() => recoverCamera != null);
        }

        private void ReportAbort(string why) {
            onAbortDecision?.Invoke(why);
        }

        /// <summary>
        /// Holds a start off until the camera has had a quiet gap since the last
        /// image came off it - see <see cref="MarkImageRetrieved"/> for why that
        /// is the moment that matters rather than the end of the exposure.
        /// <para>
        /// Only exposures shorter than the gap wait. A frame that already takes
        /// longer than the gap has given the camera that much time anyway, and
        /// charging it again would cost real sky: 4s added to every 30s sub is a
        /// seventh of the night. The wedge is a short-exposure failure - flats and
        /// bias - so that is where the wait belongs.
        /// </para>
        /// <para>
        /// Deliberately outside <see cref="captureLock"/>: holding that here would
        /// make every concurrent <see cref="Abort"/> fall back to a soft cancel for
        /// the length of the wait.
        /// </para>
        /// </summary>
        private void WaitForQuietGap(float exposureTime) {
            int required = Math.Max(0, minCaptureIntervalMs());
            if (required == 0 || !haveCaptureEnd) {
                return;
            }
            if (exposureTime * 1000.0 >= required) {
                return;
            }
            int slept = 0;
            while (true) {
                double waited = clock.GetElapsedTime(lastCaptureEndTimestamp).TotalMilliseconds;
                int remaining = required - (int)waited;
                if (remaining <= 0) {
                    break;
                }
                int slice = Math.Min(remaining, 50);
                sleep(slice);
                slept += slice;
            }
            if (slept > 0) {
                onWaited?.Invoke(slept);
            }
        }

        /// <summary>
        /// Waits, briefly, for a capture this thread has just started to appear
        /// in the status. Called only from the deferred cancel, with
        /// <see cref="captureLock"/> held.
        /// <para>
        /// The driver does not move a capture to Capturing until its worker picks
        /// it up, so a status read taken the instant the native start returns can
        /// still say Created while the shutter is already committed. Measured on
        /// the rig 2026-11-03: the read at 16:33:41.2 said 0x0000, and six
        /// seconds later the plugin found "a capture still running with nothing
        /// of ours in flight".
        /// </para>
        /// <para>
        /// Waiting is the only safe way to resolve that. Cancelling into a
        /// Created capture instead was tried on the rig the same afternoon and
        /// took NINA down with it: the cancel went out at 16:56:07.50 and the
        /// process died in the same second, SonyMTPCamera.dll 1.0.1.25,
        /// 0xc0000005 at offset 0x1fd79. The driver frees the capture task and
        /// its parameters when Cancel reports success, and in that window the
        /// task has not started reading them yet. So the status guards below
        /// stay exactly as they are; this only gives the truth time to arrive.
        /// </para>
        /// <para>
        /// Blocking here is acceptable: NINA has already abandoned this frame,
        /// and each status read is itself a queued native call, so the bound is
        /// generous rather than tight.
        /// </para>
        /// </summary>
        private uint AwaitAnnouncedCapture() {
            long started = clock.GetTimestamp();
            int reads = 0;
            uint status = Created;
            while (clock.GetElapsedTime(started).TotalMilliseconds < captureAnnounceTimeoutMs) {
                sleep(100);
                status = getStatus();
                reads++;
                if (status != Created) {
                    ReportAbort($"the capture took {(int)clock.GetElapsedTime(started).TotalMilliseconds}ms and {reads} status reads to appear after the native start returned, and now reads 0x{status:X4}");
                    return status;
                }
            }
            ReportAbort($"the driver still reported 0x0000 after {captureAnnounceTimeoutMs}ms and {reads} status reads, so nothing was sent; cancelling into a capture the driver has not announced crashes it");
            return status;
        }

        /// <summary>
        /// Records that an exposure delivered no image. Counted rather than acted
        /// on immediately, because the useful moment to recover is just before
        /// the next start, when nothing is in flight.
        /// </summary>
        internal void MarkCaptureFailed() {
            Interlocked.Increment(ref consecutiveFailures);
        }

        /// <summary>
        /// Records that an image came off the camera, which is the only evidence
        /// that clears the failure count. Deliberately separate from
        /// <see cref="MarkImageRetrieved"/>: that one is about when the traffic
        /// stopped and runs even when the retrieval threw.
        /// </summary>
        internal void MarkCaptureSucceeded() {
            Interlocked.Exchange(ref consecutiveFailures, 0);
        }

        /// <summary>
        /// Clears the failure count because the camera has been reconnected.
        /// <para>
        /// Without this the cap is a trap rather than a guard. Only an image
        /// coming off the camera clears the count, and once the cap refuses
        /// every start no image can ever be taken, so the camera stays refused
        /// until the host process restarts. Measured on the rig 2026-11-04: the
        /// cap tripped at 10:31:29, a full disconnect and reconnect followed at
        /// 10:34:44, and the next start was still refused.
        /// </para>
        /// <para>
        /// A reconnect is the operator - or a recovery trigger - saying try
        /// again, and it is also the thing that actually clears this body. So it
        /// gets a clean slate.
        /// </para>
        /// </summary>
        internal void ResetFailureCount() {
            Interlocked.Exchange(ref consecutiveFailures, 0);
        }

        /// <summary>
        /// Stops driving a camera that has failed too many times in a row.
        /// <para>
        /// This is the fault that ends nights. On 2026-11-03 at 23:23 an exposure
        /// stopped announcing its image; the sequence's error behaviour is
        /// ContinueOnError, so NINA started another, which failed the same way,
        /// every 50 seconds for twenty minutes. Around 23:44 a native call
        /// finally exceeded NativeCallExecutor's 30s timeout, which latches the
        /// driver dead for the rest of the process - after that the camera could
        /// not even be enumerated, so a reconnect could no longer have saved the
        /// session even once the body recovered.
        /// </para>
        /// <para>
        /// Refusing here makes no native calls at all, so the executor cannot
        /// latch. The frames are lost either way, but the driver stays usable and
        /// one reconnect at any later time brings the whole session back.
        /// </para>
        /// </summary>
        private void RefuseIfTooManyFailures() {
            int cap = maxConsecutiveFailures();
            if (cap <= 0) {
                return;
            }
            int failures = Volatile.Read(ref consecutiveFailures);
            if (failures < cap) {
                return;
            }
            throw new InvalidOperationException(
                $"Cannot start exposure: the last {failures} exposures delivered no image. " +
                "Not driving the camera further, so the driver stays usable - reconnect the camera to resume.");
        }

        /// <summary>
        /// Reopens the camera when the previous exposure delivered nothing.
        /// <para>
        /// A close and reopen is the only recovery that has ever cleared this
        /// body: it was done by hand three times on 2026-11-03 and worked every
        /// time, including on a capture the camera was holding open. Doing it
        /// here costs a few seconds on a path where a frame has already been
        /// lost, and it is invisible to the host - the handle is swapped under
        /// the connection lock, so the camera never reads as disconnected.
        /// </para>
        /// <para>
        /// Attempted once per failed exposure rather than in a loop. A camera
        /// that will not come back trips the cap above instead of being
        /// reopened indefinitely.
        /// </para>
        /// </summary>
        private void RecoverIfTheLastExposureFailed() {
            if (recoverCamera == null || Volatile.Read(ref consecutiveFailures) == 0) {
                return;
            }
            // Asked before invoking, so a disabled recovery reports nothing at
            // all. Letting the callback answer false instead logged "could not
            // reopen the camera", which reads as a failed attempt when nothing
            // was attempted - the same dishonest-status trap this whole plugin
            // has been fixing.
            if (!recoveryEnabled()) {
                return;
            }
            int failures = Volatile.Read(ref consecutiveFailures);
            bool recovered;
            try {
                recovered = recoverCamera();
            } catch (Exception ex) {
                onRecovery?.Invoke($"reopening the camera after {failures} failed exposure(s) threw: {ex.Message}");
                return;
            }
            onRecovery?.Invoke(recovered
                ? $"reopened the camera after {failures} failed exposure(s)"
                : $"could not reopen the camera after {failures} failed exposure(s); starting the exposure anyway");
        }

        /// <summary>
        /// Holds the next start off until an exposure that was cancelled has
        /// actually finished on the camera.
        /// <para>
        /// Cancelling does not close a shutter the camera has already opened, and
        /// the shutter speed cannot be changed while it is open. So the next
        /// capture's exposure-time change fails, the plugin refuses to shoot at
        /// the wrong shutter - correctly - and NINA is left believing an exposure
        /// is still running, after which it drops every later capture request
        /// without a word.
        /// </para>
        /// <para>
        /// Measured on the rig 2026-11-03 with driver 1.0.1.26: a 20s exposure
        /// was cancelled 105ms after the shutter opened at 17:28:30.3, the next
        /// frame was attempted at 17:28:38.6 and gave up at 17:28:51.5 reporting
        /// "camera reports 0x00C8000A" - 20s, the shutter still open. The body
        /// freed itself at about 17:28:50.3, one second before the attempt
        /// expired. The two frames after that were never even logged as starting.
        /// </para>
        /// <para>
        /// Waiting is the only correct answer while the camera finishes a frame
        /// it was told to take. The bound is the exposure's own length, so it
        /// costs nothing that was not already spent, and it is reported on its
        /// own log line.
        /// </para>
        /// </summary>
        private void WaitForAnAbortedExposureToFinish() {
            if (!haveAbortedCapture) {
                return;
            }
            haveAbortedCapture = false;
            int slept = 0;
            while (true) {
                double elapsed = clock.GetElapsedTime(abortedCaptureStartTimestamp).TotalMilliseconds;
                // A small margin: the camera has to put the frame down as well
                // as stop exposing, and the readback the next start verifies
                // against is not instant either.
                int remaining = (int)(abortedCaptureSeconds * 1000.0 + 500.0 - elapsed);
                if (remaining <= 0) {
                    break;
                }
                int slice = Math.Min(remaining, 100);
                sleep(slice);
                slept += slice;
            }
            if (slept > 0) {
                onWaitedForAbortedCapture?.Invoke(slept);
            }
        }

        private void MarkCaptureEnded() {
            lastCaptureEndTimestamp = clock.GetTimestamp();
            haveCaptureEnd = true;
        }

        /// <summary>
        /// Records that the image has been pulled off the camera, which is the
        /// point the quiet gap has to be measured from.
        /// <para>
        /// The exposure completing and the image leaving the camera are two
        /// different moments, and NINA drives them through two separate calls:
        /// the status reaches Complete in <see cref="WaitUntilReady"/>, and the
        /// 12.4MB retrieval only starts afterwards, in the driver's
        /// DownloadExposure. Timing the gap from Complete therefore spends it
        /// while the transfer is still running, so the next shutter release
        /// lands on top of a download the native driver has to clean up - after
        /// which the camera sometimes never announces the new image at all and
        /// only a reconnect clears it.
        /// </para>
        /// <para>
        /// Measured on an a7S at 0.0125s: the retrieval began at 13:08:38.495
        /// and was still being polled at 13:08:42.669, by which time the 4s gap
        /// timed from Complete had already released the next frame. One frame of
        /// ten survived.
        /// </para>
        /// <para>
        /// The Complete, Cancelled and Failed marks in <see cref="WaitUntilReady"/>
        /// stay as a floor, so a frame NINA abandons without downloading still
        /// records an end and the gap is never skipped altogether.
        /// </para>
        /// </summary>
        internal void MarkImageRetrieved() {
            MarkCaptureEnded();
        }

        internal void Start(float exposureTime) {
            RefuseIfTooManyFailures();
            WaitForAnAbortedExposureToFinish();
            RecoverIfTheLastExposureFailed();
            WaitForQuietGap(exposureTime);
            lock (captureLock) {
                var request = new CaptureRequest();
                lock (requestLock) {
                    pendingRequest = request;
                }
                try {
                    uint status = getStatus();
                    if (IsBusy(status) && recoverStuckCapture != null && recoverStuckCapture()) {
                        // Recovery is attempted once and then re-read: a capture
                        // that is genuinely still running stays busy and is
                        // rejected below, exactly as before.
                        status = getStatus();
                    }
                    if (IsBusy(status)) {
                        throw new InvalidOperationException("Cannot start exposure: camera is still busy with a previous exposure.");
                    }
                    if (status != Created && status != Cancelled && status != Complete && status != Failed) {
                        throw new InvalidOperationException($"Cannot start exposure: capture status unavailable or unexpected ({status}).");
                    }

                    lock (requestLock) {
                        Volatile.Write(ref currentRequest, request);
                        pendingRequest = null;
                    }
                    ThrowIfCancelled(request);
                    // Serialize the native start itself: it can replace the native capture object.
                    startCapture(exposureTime);
                    captureStartTimestamp = clock.GetTimestamp();
                    captureSeconds = exposureTime;
                    // An abort raised while we were inside the native start could
                    // not take captureLock, so it recorded its intent and left.
                    // Honour it here, where the lock is already ours and the start
                    // has returned.
                    //
                    // The native driver runs one operation at a time, and a start
                    // that waits for the camera to release the previous photo
                    // holds it for as long as that takes - which is exactly when
                    // an exposure most needs cancelling. Measured on the rig
                    // 2026-11-03: a 20s exposure aborted 6s in logged "the capture
                    // lock could not be taken within 1000ms" while the driver
                    // reported "busy with another operation" for the next 22
                    // seconds; the capture was never cancelled and the three
                    // frames after it were all refused as "camera is still busy".
                    //
                    // An abort that arrives after this check still has to wait for
                    // the lock, but only for the few lines left in this method.
                    if (Volatile.Read(ref request.CancelRequested) != 0 && nativeCancelEnabled()) {
                        CancelUnderHeldLock(request, captureJustStarted: true);
                    }
                } finally {
                    lock (requestLock) {
                        pendingRequest = null;
                    }
                }
            }
        }

        internal AbortResult Abort() {
            // Record cancellation before waiting for the lock, including while StartCapture is blocked.
            CaptureRequest request;
            lock (requestLock) {
                request = pendingRequest ?? currentRequest;
                int alreadyRequested = Interlocked.Exchange(ref request.CancelRequested, 1);
                // If preflight rejects the new request, the previous capture must
                // still retain this abort. No native call runs under requestLock.
                Interlocked.Exchange(ref currentRequest.CancelRequested, 1);
                if (alreadyRequested != 0) {
                    ReportAbort("an abort was already recorded for this exposure; nothing further sent");
                    return AbortResult.AlreadyRequested;
                }
            }

            if (!nativeCancelEnabled()) {
                ReportAbort("native cancel is turned off in the plugin options, so nothing was sent to the camera and the capture is left to finish on its own");
                return AbortResult.SoftCancelRequested;
            }
            if (!Monitor.TryEnter(captureLock, cancelLockTimeoutMs)) {
                // Not a dead end: the cancellation is already recorded on the
                // request, and whoever holds the lock sends it on the way out.
                ReportAbort($"native cancel is on, but the capture lock could not be taken within {cancelLockTimeoutMs}ms; the exposure starting on the other side of it holds the lock and will send the cancel itself when the native start returns");
                return AbortResult.NativeCancelDeferred;
            }

            try {
                return CancelUnderHeldLock(request);
            } finally {
                Monitor.Exit(captureLock);
            }
        }

        /// <summary>
        /// Decides and sends the native cancel. The caller must already hold
        /// <see cref="captureLock"/>; it is the one place that decision is made,
        /// so <see cref="Abort"/> and the deferred cancel at the end of
        /// <see cref="Start"/> cannot drift apart or log different things.
        /// </summary>
        /// <param name="captureJustStarted">
        /// True only for the deferred cancel at the end of <see cref="Start"/>,
        /// where this thread has just returned from the native start. It makes a
        /// Created status mean "not announced yet" rather than "nothing running".
        /// </param>
        private AbortResult CancelUnderHeldLock(CaptureRequest request, bool captureJustStarted = false) {
            // An abort queued for the previous exposure must never cancel a new one.
            if (!ReferenceEquals(request, currentRequest)) {
                ReportAbort("the abort belongs to an exposure that has already been superseded");
                return AbortResult.AlreadyRequested;
            }
            uint status = getStatus();
            if (captureJustStarted && status == Created) {
                status = AwaitAnnouncedCapture();
            }
            if (status == Reading || status == Processing) {
                ReportAbort($"capture status is 0x{status:X4}, an image transfer already in progress; cancelling into that is unsafe, so it is left to finish");
                return AbortResult.SoftCancelRequested;
            }
            if (status == StatusUnknown || (!IsBusy(status) && status != Created &&
                status != Cancelled && status != Complete && status != Failed)) {
                ReportAbort($"capture status 0x{status:X4} is unreadable or unrecognised, so nothing was sent");
                return AbortResult.SoftCancelRequested;
            }
            if (!IsBusy(status)) {
                ReportAbort($"capture status is 0x{status:X4}, which is not busy; there is nothing to cancel");
                return AbortResult.AlreadyRequested;
            }
            bool sent = cancelCapture();
            if (sent) {
                // The cancel ends the capture as far as the driver and NINA are
                // concerned, but it does not close the shutter the camera has
                // already opened. Remember when that exposure would finish.
                abortedCaptureStartTimestamp = captureStartTimestamp;
                abortedCaptureSeconds = captureSeconds;
                haveAbortedCapture = true;
            }
            ReportAbort(sent
                ? $"native cancel sent to the driver with capture status 0x{status:X4}"
                : $"native cancel call failed with capture status 0x{status:X4}; the capture is left to finish");
            return sent ? AbortResult.NativeCancelRequested : AbortResult.SoftCancelRequested;
        }

        internal async Task WaitUntilReady(CancellationToken token) {
            var request = Volatile.Read(ref currentRequest);
            while (true) {
                token.ThrowIfCancellationRequested();
                ThrowIfCancelled(request);
                // The status read is deliberately NOT inside captureLock. It used
                // to be, and that starved Abort: this loop reacquires the lock
                // ten times a second and each read goes through the native
                // driver's single worker thread, so during an exposure the lock
                // was held for most of the window in which a cancel needs it.
                // Measured on the rig 2026-11-03 - a native cancel got through
                // on one abort and timed out on the next, with the log reading
                // "the capture lock could not be taken within 1000ms".
                //
                // Dropping the lock is safe because the read is a read: the
                // native driver guards its own state, and what captureLock
                // protects here is the decision that the status belongs to OUR
                // exposure. That decision is still made under the lock, after
                // the read, so a capture that superseded ours while the status
                // was being fetched can never be mistaken for our own ending.
                uint status = getStatus();
                lock (captureLock) {
                    if (!ReferenceEquals(request, currentRequest)) {
                        throw new TaskCanceledException("Exposure superseded by a new capture.");
                    }
                }
                token.ThrowIfCancellationRequested();
                ThrowIfCancelled(request);

                switch (status) {
                    case Complete:
                        MarkCaptureEnded();
                        return;
                    case Cancelled:
                        MarkCaptureEnded();
                        throw new TaskCanceledException("Exposure cancelled by camera.");
                    case Failed:
                        MarkCaptureEnded();
                        throw new InvalidOperationException("Camera capture failed.");
                    case Created:
                    case Capturing:
                    case Starting:
                    case Reading:
                    case Processing:
                        await Task.Delay(100, token);
                        break;
                    default:
                        throw new InvalidOperationException($"Capture status unavailable or unexpected ({status}).");
                }
            }
        }

        private static void ThrowIfCancelled(CaptureRequest request) {
            if (Volatile.Read(ref request.CancelRequested) != 0) {
                throw new TaskCanceledException("Exposure cancelled by user; the camera may still be finishing it.");
            }
        }

        private static bool IsBusy(uint status) {
            return status == Capturing || status == Starting || status == Reading || status == Processing;
        }
    }
}
