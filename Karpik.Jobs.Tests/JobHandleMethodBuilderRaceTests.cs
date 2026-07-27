using System.Runtime.CompilerServices;
using Xunit;

namespace Karpik.Jobs.Tests;

public sealed class JobHandleMethodBuilderRaceTests
{
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
