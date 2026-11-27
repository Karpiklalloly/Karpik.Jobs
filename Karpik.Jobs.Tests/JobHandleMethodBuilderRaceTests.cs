using System.Runtime.CompilerServices;
using Xunit;

namespace Karpik.Jobs.Tests;

public sealed class JobHandleMethodBuilderRaceTests
{
    [Fact]
    public void AwaitRegistrationCannotRunBeforeTheWaitEventIsPublished()
    {
        var source = new JobHandleCompletionSource<int>();
        var awaiter = source.JobHandle.GetAwaiter();
        // Force registration into the interval between the final counter decrement and publication under the lock.
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object completion = typeof(JobHandleCompletionSource<int>).GetField("_jobCompletion", fields)!.GetValue(source)!;
        Type completionType = completion.GetType().BaseType!;
        var gate = (Lock)completionType.GetField("_lock", fields)!.GetValue(completion)!;
        var signal = (ManualResetEventSlim)completionType.GetField("_event", fields)!.GetValue(completion)!;
        int observed = 0;
        Thread producer = new(() => source.SetResult(42)) { IsBackground = true };
        Assert.False(awaiter.IsCompleted);
        using (gate.EnterScope())
        {
            producer.Start();
            Assert.True(SpinWait.SpinUntil(() => awaiter.IsCompleted, TimeSpan.FromSeconds(5)));
            Assert.False(signal.IsSet);
            // Avoid making a failing regression hang: GetResult would block here under the producer's lock.
            awaiter.OnCompleted(() => observed = signal.IsSet ? awaiter.GetResult() : -1);
        }
        Assert.True(producer.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(42, observed);
    }

    [Fact]
    public void AsyncJobHandle_CompletesAfterSuspendedContinuationRuns()
    {
        using var releaseContinuation = new ManualResetEventSlim();
        JobHandle<int> handle = AwaitCompletion(releaseContinuation);

        Assert.False(handle.IsCompleted);
        releaseContinuation.Set();
        Assert.True(
            SpinWait.SpinUntil(() => handle.IsCompleted, TimeSpan.FromSeconds(5)),
            "The handle returned before suspension was not completed by the resumed state machine.");
        Assert.Equal(42, handle.GetAwaiter().GetResult());
    }

    [Fact]
    public void AsyncNonGenericJobHandle_CompletesAfterSuspendedContinuationRuns()
    {
        using var releaseContinuation = new ManualResetEventSlim();
        JobHandle handle = AwaitCompletionWithoutResult(releaseContinuation);

        Assert.False(handle.IsCompleted);
        releaseContinuation.Set();
        Assert.True(
            SpinWait.SpinUntil(() => handle.IsCompleted, TimeSpan.FromSeconds(5)),
            "The handle returned before suspension was not completed by the resumed state machine.");
        handle.GetAwaiter().GetResult();
    }

    private static async JobHandle<int> AwaitCompletion(ManualResetEventSlim releaseContinuation)
    {
        return await new SuspendedCompletionAwaitable(releaseContinuation);
    }

    private static async JobHandle AwaitCompletionWithoutResult(ManualResetEventSlim releaseContinuation)
    {
        await new SuspendedCompletionAwaitable(releaseContinuation);
    }

    private readonly struct SuspendedCompletionAwaitable
    {
        private readonly ManualResetEventSlim _releaseContinuation;

        public SuspendedCompletionAwaitable(ManualResetEventSlim releaseContinuation)
        {
            _releaseContinuation = releaseContinuation;
        }

        public SuspendedCompletionAwaiter GetAwaiter() => new(_releaseContinuation);
    }

    private readonly struct SuspendedCompletionAwaiter : INotifyCompletion
    {
        private readonly ManualResetEventSlim _releaseContinuation;

        public SuspendedCompletionAwaiter(ManualResetEventSlim releaseContinuation)
        {
            _releaseContinuation = releaseContinuation;
        }

        public bool IsCompleted => false;
        public int GetResult() => 42;
        public void OnCompleted(Action continuation)
        {
            ManualResetEventSlim releaseContinuation = _releaseContinuation;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                releaseContinuation.Wait();
                continuation();
            });
        }
    }
}
