using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sony;
using Xunit;

public class NativeCallExecutorTests {
    [Fact]
    public async Task NativeCallsNeverOverlap() {
        var executor = new NativeCallExecutor();
        int active = 0;
        var calls = Enumerable.Range(0, 12).Select(_ => Task.Run(() => executor.Invoke(() => {
            Assert.Equal(1, Interlocked.Increment(ref active));
            Thread.Sleep(2);
            Assert.Equal(0, Interlocked.Decrement(ref active));
            return 1;
        })));
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
    public void NativeErrorsAreUnwrappedAndDoNotPoisonTheExecutor() {
        var executor = new NativeCallExecutor();
        var failure = new InvalidOperationException("native error");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => executor.Invoke<int>(() => throw failure)));
        Assert.False(executor.IsUnavailable);
        Assert.Equal(42, executor.Invoke(() => 42));
    }
}
