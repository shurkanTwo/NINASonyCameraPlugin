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

        internal enum AbortResult { AlreadyRequested, NativeCancelRequested, SoftCancelRequested }

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
        private long lastCaptureEndTimestamp;
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
            Action<string> onAbortDecision = null) {
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
                ReportAbort($"native cancel is on, but the capture lock could not be taken within {cancelLockTimeoutMs}ms, so nothing was sent; the status poller holds that lock while it reads the driver");
                return AbortResult.SoftCancelRequested;
            }

            try {
                // An abort queued for the previous exposure must never cancel a new one.
                if (!ReferenceEquals(request, currentRequest)) {
                    ReportAbort("the abort belongs to an exposure that has already been superseded");
                    return AbortResult.AlreadyRequested;
                }
                uint status = getStatus();
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
                ReportAbort(sent
                    ? $"native cancel sent to the driver with capture status 0x{status:X4}"
                    : $"native cancel call failed with capture status 0x{status:X4}; the capture is left to finish");
                return sent ? AbortResult.NativeCancelRequested : AbortResult.SoftCancelRequested;
            } finally {
                Monitor.Exit(captureLock);
            }
        }

        internal async Task WaitUntilReady(CancellationToken token) {
            var request = Volatile.Read(ref currentRequest);
            while (true) {
                token.ThrowIfCancellationRequested();
                ThrowIfCancelled(request);
                uint status;
                lock (captureLock) {
                    if (!ReferenceEquals(request, currentRequest)) {
                        throw new TaskCanceledException("Exposure superseded by a new capture.");
                    }
                    status = getStatus();
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
