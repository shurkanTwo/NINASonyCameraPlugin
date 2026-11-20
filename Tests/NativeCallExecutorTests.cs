using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sony;
using Xunit;

public class NativeCallExecutorTests {
    [Fact]
    public async Task NativeCallsNeverOverlap() {
        // Dedicated threads for the callers, not Task.Run. Invoke blocks its
        // caller while the work it queued needs a pool thread of its own, so
        // twelve pool callers starve the pool they are waiting on: the pool
        // injects threads about once a second, and under a loaded suite that
        // reached Invoke's 30s timeout and failed the run. The thing being
        // asserted is that concurrent callers never overlap inside the DLL,
        // which does not depend on where the callers come from.
        var executor = new NativeCallExecutor();
        int active = 0;
        var calls = Enumerable.Range(0, 12).Select(_ => Task.Factory.StartNew(() => executor.Invoke(() => {
            Assert.Equal(1, Interlocked.Increment(ref active));
            Thread.Sleep(2);
            Assert.Equal(0, Interlocked.Decrement(ref active));
            return 1;
        }), TaskCreationOptions.LongRunning));
        Assert.Equal(12, (await Task.WhenAll(calls)).Sum());
    }

    [Fact]
    public async Task TimeoutBlocksAllLaterNativeCallsEvenAfterTheWorkerReturns() {
        var executor = new NativeCallExecutor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(() => Assert.Throws<TimeoutException>(() => executor.Invoke(() => {
            entered.Set();
            release.Wait();
            finished.SetResult(true);
            return 1;
        }, timeoutMs: 200)));
        try {
            Assert.True(entered.Wait(5000));
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(executor.IsUnavailable);
            int calls = 0;
            Assert.Throws<InvalidOperationException>(() => executor.Invoke(() => ++calls));
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<InvalidOperationException>(() => executor.Invoke(() => ++calls));
            Assert.Equal(0, calls);
        } finally {
            release.Set();
        }
    }

    [Fact]
    public async Task WaitingForABusyDriverNeverExecutesTheRejectedCall() {
        var executor = new NativeCallExecutor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => executor.Invoke(() => {
            entered.Set();
            release.Wait();
            return 1;
        }, timeoutMs: 5000));
        try {
            Assert.True(entered.Wait(5000));
            int calls = 0;
            Assert.Throws<TimeoutException>(() => executor.Invoke(() => ++calls, lockTimeoutMs: 10));
            Assert.Equal(0, calls);
            Assert.False(executor.IsUnavailable);
        } finally {
            release.Set();
            Assert.Equal(1, await first);
        }
        Assert.Equal(2, executor.Invoke(() => 2));
    }

    [Fact]
    public void AStopWithNothingAbandonedIsClearedByAResume() {
        // The driver also stops itself when the camera answers several native
        // calls with errors. Nothing was abandoned there - every call returned -
        // so a reconnect can clear it at once. That stop used to be permanent,
        // and the only way back was a NINA restart.
        var executor = new NativeCallExecutor();
        executor.Stop();
        Assert.True(executor.IsUnavailable);
        Assert.Throws<InvalidOperationException>(() => executor.Invoke(() => 1));

        Assert.True(executor.TryResume());

        Assert.False(executor.IsUnavailable);
        Assert.Equal(7, executor.Invoke(() => 7));
    }

    [Fact]
    public void AResumeOnAWorkingExecutorChangesNothing() {
        var executor = new NativeCallExecutor();
        Assert.True(executor.TryResume());
        Assert.False(executor.IsUnavailable);
        Assert.Equal(3, executor.Invoke(() => 3));
    }

    [Fact]
    public async Task ATimedOutCallKeepsTheExecutorShutUntilItHasReturned() {
        // Why the stop exists at all: a call that timed out was abandoned, not
        // cancelled, and it is still inside the DLL. Resuming while it is in
        // there would let a second caller race it, so the resume is refused
        // until it returns - and granted afterwards, because by then nothing is
        // inside the DLL any more.
        var executor = new NativeCallExecutor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandoned = Task.Run(() => Assert.Throws<TimeoutException>(() => executor.Invoke(() => {
            entered.Set();
            release.Wait();
            finished.SetResult(true);
            return 1;
        }, timeoutMs: 200)));
        try {
            Assert.True(entered.Wait(5000));
            await abandoned.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(executor.IsUnavailable);

            // Still in the DLL: refused, and a refusal must not half-clear it.
            Assert.False(executor.TryResume());
            Assert.True(executor.IsUnavailable);
            int calls = 0;
            Assert.Throws<InvalidOperationException>(() => executor.Invoke(() => ++calls));
            Assert.Equal(0, calls);

            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Out of the DLL: granted. Spun because a task is marked complete
            // just after its body returns, not at the same instant.
            Assert.True(SpinWait.SpinUntil(() => executor.TryResume(), TimeSpan.FromSeconds(5)));
            Assert.False(executor.IsUnavailable);
            Assert.Equal(9, executor.Invoke(() => 9));
        } finally {
            release.Set();
        }
    }

    [Fact]
    public void NativeErrorsAreUnwrappedAndDoNotPoisonTheExecutor() {
        var executor = new NativeCallExecutor();
        var failure = new InvalidOperationException("native error");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => executor.Invoke<int>(() => throw failure)));
        Assert.False(executor.IsUnavailable);
        Assert.Equal(42, executor.Invoke(() => 42));
    }
}
