using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Sony {
    // Based on Robert Hasson's PR #13 readback/retry workaround. Keep the
    // decision independent of the DLL so stalled shutter changes are testable.
    internal static class ExposureTimeController {
        private const uint NotSupported = 50;

        private static bool IsNumeric(uint raw) => raw != uint.MaxValue && (raw >> 16) > 0 && (raw & 0xFFFF) > 0;
        private static float Seconds(uint raw) => (float)(raw >> 16) / (raw & 0xFFFF);

        internal static uint? SelectShutter(double requested, IEnumerable<uint> shutterSpeeds) {
            if (!double.IsFinite(requested) || requested < 0 || requested > float.MaxValue) {
                throw new ArgumentOutOfRangeException(nameof(requested));
            }
            var numeric = shutterSpeeds?.Where(IsNumeric).OrderByDescending(Seconds).ToArray();
            if (numeric == null || numeric.Length == 0) return null;

            // Match SetExposureTime's float arithmetic and five-decimal rounding.
            // Equal distances choose the shorter step in the descending ladder.
            float target = MathF.Round((float)requested * 100000f, MidpointRounding.AwayFromZero) / 100000f;
            if (requested == 0 || target > Seconds(numeric[0])) return 0;
            uint nearest = numeric[0];
            float distance = MathF.Abs(target - Seconds(nearest));
            foreach (uint raw in numeric.Skip(1)) {
                float candidate = MathF.Abs(target - Seconds(raw));
                if (candidate <= distance) {
                    nearest = raw;
                    distance = candidate;
                }
            }
            return nearest;
        }

        internal static bool Apply(double requested, IEnumerable<uint> shutterSpeeds,
            Func<uint> setExposure, Func<uint> readShutter, int timeoutMs = 12000, Action pause = null) {
            uint? expected = SelectShutter(requested, shutterSpeeds);
            pause ??= () => Thread.Sleep(300);
            Stopwatch elapsed = Stopwatch.StartNew();
            uint actual = uint.MaxValue;
            do {
                uint result = setExposure();
                if (result == NotSupported) return false;
                if (result != 0) throw new SonyException($"Unable to set exposure time to {requested}s, error {result}.", result);
                if (!expected.HasValue) throw new InvalidOperationException("The learnt shutter-speed list is unavailable; the requested shutter cannot be verified.");

                actual = readShutter();
                // Zero is valid Bulb, including a temporarily unchanged old mode.
                if (actual == 0 && expected == 0) return true;
                if (actual != 0 && !IsNumeric(actual)) {
                    throw new InvalidOperationException("The camera did not report a verifiable shutter speed.");
                }
                if (actual != 0 && expected != 0 &&
                    (ulong)(actual >> 16) * (expected.Value & 0xFFFF) ==
                    (ulong)(expected.Value >> 16) * (actual & 0xFFFF)) return true;
                if (elapsed.ElapsedMilliseconds >= timeoutMs) break;
                pause();
            } while (elapsed.ElapsedMilliseconds < timeoutMs);
            throw new InvalidOperationException($"Exposure time was not applied: requested {requested}s, expected shutter 0x{expected:X8}, camera reports 0x{actual:X8}. Capture was not started.");
        }
    }
}
