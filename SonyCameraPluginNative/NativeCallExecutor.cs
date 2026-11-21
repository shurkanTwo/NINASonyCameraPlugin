using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sony {
    // A timed-out P/Invoke keeps running. Never let a later call enter the DLL
    // while the abandoned one might still be inside it.
    internal sealed class NativeCallExecutor {
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private int unavailable;
        private Task abandoned;

        internal bool IsUnavailable => Volatile.Read(ref unavailable) != 0;

        internal void Stop() => Volatile.Write(ref unavailable, 1);

        /// <summary>
        /// Lets calls into the DLL again, if nothing can still be inside it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A latch nobody can clear is a trap. This one made every call throw
        /// "the Sony driver stopped responding" for the rest of the process,
        /// so a camera that came back - the body power-cycled, the USB
        /// replugged, the device enumerating normally again - still could not
        /// be reached, and the night ended on a NINA restart. The capture
        /// failure cap had the same shape until a reconnect was taught to
        /// clear it.
        /// </para>
        /// <para>
        /// The reason for the latch is real, though, so it is not simply
        /// dropped. A call that timed out was abandoned, not cancelled: it is
        /// still running, and it is still holding whatever the DLL gave it.
        /// Letting a second caller in while the first is in there is how the
        /// driver corrupts itself. So the abandoned call is tracked, and the
        /// gate stays shut until it has actually returned.
        /// </para>
        /// <para>
        /// A caller that stopped the executor for its own reasons - a camera
        /// that answered three native calls with errors, say - abandoned
        /// nothing. There is nothing to wait for, and this succeeds at once.
        /// </para>
        /// </remarks>
        /// <returns>
        /// True when calls are allowed again. False when an abandoned call has
        /// still not returned, which only a restart can resolve.
        /// </returns>
        internal bool TryResume() {
            Task stillRunning = Volatile.Read(ref abandoned);
            if (stillRunning != null && !stillRunning.IsCompleted) return false;
            Volatile.Write(ref abandoned, null);
            Volatile.Write(ref unavailable, 0);
            return true;
        }

        internal T Invoke<T>(Func<T> action, int timeoutMs = 30000, int lockTimeoutMs = 30000) {
            if (IsUnavailable) throw new InvalidOperationException("The Sony driver stopped responding. Reconnect the camera, or restart NINA if that does not clear it.");
            if (!gate.Wait(lockTimeoutMs)) throw new TimeoutException("The Sony driver is busy with another operation.");
            try {
                if (IsUnavailable) throw new InvalidOperationException("The Sony driver stopped responding. Reconnect the camera, or restart NINA if that does not clear it.");
                Task<T> call = Task.Run(action);
                try {
                    if (!call.Wait(timeoutMs)) {
                        // Recorded before the gate is shut, so a resume that
                        // sees the stop also sees what it has to wait for.
                        Volatile.Write(ref abandoned, call);
                        Stop();
                        _ = call.ContinueWith(failed => { _ = failed.Exception; },
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                        throw new TimeoutException("The Sony driver stopped responding. Reconnect the camera, or restart NINA if that does not clear it.");
                    }
                } catch (AggregateException) {
                    // Preserve the native operation's original exception.
                    return call.GetAwaiter().GetResult();
                }
                return call.GetAwaiter().GetResult();
            } finally {
                gate.Release();
            }
        }
    }
}
