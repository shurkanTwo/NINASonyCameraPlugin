using System;
using System.Diagnostics;
using System.Threading;

namespace Sony {
    // Based on Robert Hasson's PR #13 readback/retry workaround. Keep the
    // decision independent of the DLL so stalled shutter changes are testable.
    internal static class ExposureTimeController {
        private const uint NotSupported = 50;

        internal static bool Apply(double requested, Func<uint> setExposure, Func<uint> readShutter,
            int timeoutMs = 12000, Action pause = null) {
            if (!double.IsFinite(requested) || requested < 0) throw new ArgumentOutOfRangeException(nameof(requested));
            pause ??= () => Thread.Sleep(300);
            Stopwatch elapsed = Stopwatch.StartNew();
            double actual = double.NaN;
            do {
                uint result = setExposure();
                if (result == NotSupported) return false;
                if (result != 0) throw new InvalidOperationException($"Unable to set exposure time to {requested}s, error {result}.");

                uint raw = readShutter();
                // Sony uses zero for Bulb. Zero-duration captures retain the
                // native driver's existing Bulb behavior.
                if (raw == 0 && (requested == 0 || requested > 30)) return true;
                uint numerator = raw >> 16;
                uint denominator = raw & 0xFFFF;
                if (numerator == 0 || denominator == 0 || raw == uint.MaxValue) {
                    throw new InvalidOperationException("The camera did not report a verifiable shutter speed.");
                }
                actual = (double)numerator / denominator;
                // Sony's irregular ladder includes a 4/3 gap. The native
                // driver chooses by absolute distance: its worst half-step
                // ratio is 7/6, below 1.17 and the smallest whole step (1.20).
                if (requested > 0 && Math.Max(actual / requested, requested / actual) <= 1.17) return true;
                if (elapsed.ElapsedMilliseconds >= timeoutMs) break;
                pause();
            } while (elapsed.ElapsedMilliseconds < timeoutMs);
            throw new InvalidOperationException($"Exposure time was not applied: requested {requested}s, camera reports {actual}s. Capture was not started.");
        }
    }
}
