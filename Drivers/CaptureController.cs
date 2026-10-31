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
            TimeProvider clock = null, Action<int> sleep = null) {
            this.getStatus = getStatus;
            this.startCapture = startCapture;
            this.cancelCapture = cancelCapture;
            this.nativeCancelEnabled = nativeCancelEnabled;
            this.cancelLockTimeoutMs = cancelLockTimeoutMs;
            this.minCaptureIntervalMs = minCaptureIntervalMs ?? (() => 0);
            this.recoverStuckCapture = recoverStuckCapture;
            this.clock = clock ?? TimeProvider.System;
            this.sleep = sleep ?? Thread.Sleep;
        }

        /// <summary>
        /// Holds a start off until the camera has had a quiet gap since the last
        /// capture ended. Deliberately outside <see cref="captureLock"/>: holding
        /// that here would make every concurrent <see cref="Abort"/> fall back to
        /// a soft cancel for the length of the wait.
        /// </summary>
        private void WaitForQuietGap() {
            int required = Math.Max(0, minCaptureIntervalMs());
            if (required == 0 || !haveCaptureEnd) {
                return;
            }
            while (true) {
                double waited = clock.GetElapsedTime(lastCaptureEndTimestamp).TotalMilliseconds;
                int remaining = required - (int)waited;
                if (remaining <= 0) {
                    return;
                }
                sleep(Math.Min(remaining, 50));
            }
        }

        private void MarkCaptureEnded() {
            lastCaptureEndTimestamp = clock.GetTimestamp();
            haveCaptureEnd = true;
        }

        internal void Start(float exposureTime) {
            WaitForQuietGap();
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
                if (alreadyRequested != 0) return AbortResult.AlreadyRequested;
            }
            if (!nativeCancelEnabled() || !Monitor.TryEnter(captureLock, cancelLockTimeoutMs)) {
                return AbortResult.SoftCancelRequested;
            }

            try {
                // An abort queued for the previous exposure must never cancel a new one.
                if (!ReferenceEquals(request, currentRequest)) {
                    return AbortResult.AlreadyRequested;
                }
                uint status = getStatus();
                if (status == Reading || status == Processing) {
                    return AbortResult.SoftCancelRequested;
                }
                if (status == StatusUnknown || (!IsBusy(status) && status != Created &&
                    status != Cancelled && status != Complete && status != Failed)) {
                    return AbortResult.SoftCancelRequested;
                }
                if (!IsBusy(status)) {
                    return AbortResult.AlreadyRequested;
                }
                return cancelCapture() ? AbortResult.NativeCancelRequested : AbortResult.SoftCancelRequested;
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
