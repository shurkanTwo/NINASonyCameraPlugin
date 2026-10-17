using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sony {
    // A timed-out P/Invoke keeps running. Never let a later call enter the DLL,
    // even if the abandoned call eventually returns; recovery requires a restart.
    internal sealed class NativeCallExecutor {
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private int unavailable;

        internal bool IsUnavailable => Volatile.Read(ref unavailable) != 0;

        internal void Stop() => Volatile.Write(ref unavailable, 1);

        internal T Invoke<T>(Func<T> action, int timeoutMs = 30000, int lockTimeoutMs = 30000) {
            if (IsUnavailable) throw new InvalidOperationException("The Sony driver stopped responding. Restart NINA and power-cycle the camera before reconnecting.");
            if (!gate.Wait(lockTimeoutMs)) throw new TimeoutException("The Sony driver is busy with another operation.");
            try {
                if (IsUnavailable) throw new InvalidOperationException("The Sony driver stopped responding. Restart NINA and power-cycle the camera before reconnecting.");
                Task<T> call = Task.Run(action);
                try {
                    if (!call.Wait(timeoutMs)) {
                        Stop();
                        _ = call.ContinueWith(failed => { _ = failed.Exception; },
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                        throw new TimeoutException("The Sony driver stopped responding. Restart NINA and power-cycle the camera before reconnecting.");
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
