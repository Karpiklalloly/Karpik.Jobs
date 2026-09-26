using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("Karpik.Engine.Core")]
namespace Karpik.Jobs;

[AllocatingCompatibility("Legacy delegate-based job system. Uses managed delegates, wrappers, continuations, CTS, semaphores, and ConcurrentQueue; do not use from no-GC frame hot paths.")]
public class JobSystem
{
    private const int CacheLineSize = 64;
    private const int MaxThreads = 64;
    private const int DefaultQueueCapacity = 100_000;
    private const int DefaultBatchSize = 64;

    private readonly ThreadState[] _threadStates;
    private readonly Thread[] _threads;
    private readonly int _workerCount;
    private volatile bool _isRunning;
    private readonly ObjectPool<JobWrapper> _jobWrapperPool;
    private readonly Action<Exception>? _onJobError;

    private int _enqueueIndex = 0;
    private int _outstandingJobs = 0;
    private int _admissionState; // Sign bit closes admission; remaining bits count active Enqueue calls.

    private readonly SemaphoreSlim _workSemaphore = new SemaphoreSlim(0);

    [AllocatingCompatibility("Creates managed worker threads, thread state arrays, semaphores, wrapper pool metadata, and ConcurrentQueue instances.")]
    public JobSystem(int workerCount = -1, string prefix = "JobWorker", Action<Exception>? onJobError = null)
    {
        _onJobError = onJobError;
        _workerCount = workerCount == -1
            ? Math.Min(Environment.ProcessorCount, MaxThreads)
            : Math.Min(workerCount, MaxThreads);

        _threadStates = new ThreadState[_workerCount];
        _threads = new Thread[_workerCount];
        _jobWrapperPool = new ObjectPool<JobWrapper>(static () => new JobWrapper(), 100_000);

        for (int i = 0; i < _workerCount; i++)
        {
            _threadStates[i] = new ThreadState(i);
            _threads[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = $"{prefix}-{i}"
            };
            _threads[i].Start(i);
        }

        _isRunning = true;
    }

    [StructLayout(LayoutKind.Explicit, Size = CacheLineSize * 2)]
    private class ThreadState
    {
        [FieldOffset(CacheLineSize)]
        public readonly int ThreadId;
        [FieldOffset(CacheLineSize * 2 - 16)]
        public readonly ConcurrentQueue<JobWrapper> Queue;
        [FieldOffset(CacheLineSize * 2 - 8)]
        public volatile bool IsWorking;

        public ThreadState(int threadId)
        {
            ThreadId = threadId;
            Queue = new ConcurrentQueue<JobWrapper>();
        }

        public void Dispose()
        {
            Queue.Clear();
        }
    }

    private void WorkerLoop(object? state)
    {
        int threadId = (int)state!;

        while (true)
        {
            _workSemaphore.Wait();

            if (!_isRunning)
            {
                break;
            }

            // Drain available work: process own queue and attempt to steal until no work found.
            while (true)
            {
                if (TryPopTask(threadId, out JobWrapper? wrapper) || TryStealTask(threadId, out wrapper))
                {
                    ExecuteJob(wrapper!, threadId);
                    break;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryPopTask(int threadId, out JobWrapper? wrapper)
    {
        return _threadStates[threadId].Queue.TryDequeue(out wrapper);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryStealTask(int thiefId, out JobWrapper? wrapper)
    {
        int victimId = (thiefId + 1 + ThreadLocalRandom.Next(0, _workerCount - 1)) % _workerCount;
        if (victimId == thiefId)
        {
            victimId = (thiefId + 1) % _workerCount;
        }

        return _threadStates[victimId].Queue.TryDequeue(out wrapper);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExecuteJob(JobWrapper wrapper, int threadId)
    {
        var state = _threadStates[threadId];
        state.IsWorking = true;

        try
        {
            if (!wrapper.Cts!.Token.IsCancellationRequested)
            {
                if (wrapper.IsParallel)
                    wrapper.ParallelAction!(wrapper.StartIndex, wrapper.EndIndex);
                else
                    wrapper.Action!();
            }
        }
        catch (Exception ex)
        {
            wrapper.Completion?.SetException(ex);
            ReportJobError(ex);
        }
        finally
        {
            state.IsWorking = false;

            wrapper.OnCompleted?.Invoke();
            wrapper.Completion?.Signal();
            wrapper.Reset();
            _jobWrapperPool.Return(wrapper);

            Interlocked.Decrement(ref _outstandingJobs);
        }
    }

    private JobHandle EnqueueInternal(Action job, Span<JobHandle> dependencies)
    {
        if (!TryEnterEnqueue()) return default;
        try
        {
            return EnqueueAccepted(job, dependencies);
        }
        finally
        {
            Interlocked.Decrement(ref _admissionState);
        }
    }

    private JobHandle EnqueueAccepted(Action job, Span<JobHandle> dependencies)
    {
        JobCompletion? dependenciesCompletion = RegisterDependencies(dependencies);
        var completion = new JobCompletion(1);
        var cts = new CancellationTokenSource();
        var wrapper = _jobWrapperPool.Rent();
        wrapper.Action = job;
        wrapper.Cts = cts;
        wrapper.IsParallel = false;
        wrapper.Completion = completion;

        Action enqueueAction = () =>
        {
            int targetThread = (Interlocked.Increment(ref _enqueueIndex) & int.MaxValue) % _workerCount;
            _threadStates[targetThread].Queue.Enqueue(wrapper);

            _workSemaphore.Release();
        };

        Interlocked.Increment(ref _outstandingJobs);
        if (dependenciesCompletion is null) enqueueAction();
        else dependenciesCompletion.AddContinuation(enqueueAction);

        return new JobHandle(completion, cts);
    }

    private JobHandle EnqueueParallelInternal(Action<int> action, int size, int batchSize, Span<JobHandle> dependencies)
    {
        if (size <= 0 || !TryEnterEnqueue()) return default;
        try
        {
            return EnqueueParallelAccepted(action, size, batchSize, dependencies);
        }
        finally
        {
            Interlocked.Decrement(ref _admissionState);
        }
    }

    private JobHandle EnqueueParallelAccepted(Action<int> action, int size, int batchSize, Span<JobHandle> dependencies)
    {
        JobCompletion? dependenciesCompletion = RegisterDependencies(dependencies);
        if (batchSize <= 0) batchSize = Math.Max(1, Math.Min(DefaultBatchSize, size / _workerCount));
        int batchCount = 1 + (size - 1) / batchSize;
        var cts = new CancellationTokenSource();
        var completion = new JobCompletion(batchCount);

        Action enqueueBatches = () =>
        {
            for (int i = 0; i < batchCount; i++)
            {
                int startIndex = i * batchSize;
                int endIndex = startIndex + Math.Min(batchSize, size - startIndex);
                var wrapper = _jobWrapperPool.Rent();
                wrapper.ParallelAction = (start, end) =>
                {
                    for (int j = start; j < end; j++)
                    {
                        if (wrapper.Cts!.Token.IsCancellationRequested) return;
                        action(j);
                    }
                };
                wrapper.Cts = cts;
                wrapper.IsParallel = true;
                wrapper.StartIndex = startIndex;
                wrapper.EndIndex = endIndex;
                wrapper.Completion = completion;

                int targetThread = (Interlocked.Increment(ref _enqueueIndex) & int.MaxValue) % _workerCount;
                _threadStates[targetThread].Queue.Enqueue(wrapper);
            }

            _workSemaphore.Release(batchCount);
        };

        Interlocked.Add(ref _outstandingJobs, batchCount);
        if (dependenciesCompletion is null) enqueueBatches();
        else dependenciesCompletion.AddContinuation(enqueueBatches);

        return new JobHandle(completion, cts);
    }

    [AllocatingCompatibility("Allocates managed completion/cancellation state and publishes a delegate wrapper.")]
    public JobHandle Enqueue(Action job) =>
        EnqueueInternal(job, Span<JobHandle>.Empty);

    [AllocatingCompatibility("Allocates managed completion/cancellation state, dependency continuations, and publishes a delegate wrapper.")]
    public JobHandle Enqueue(Action job, params Span<JobHandle> dependencies) =>
        EnqueueInternal(job, dependencies);

    [AllocatingCompatibility("Allocates managed completion/cancellation state and delegate batch wrappers.")]
    public JobHandle EnqueueParallel(Action<int> action, int size, int batchSize = -1) =>
        EnqueueParallelInternal(action, size, batchSize, Span<JobHandle>.Empty);

    [AllocatingCompatibility("Allocates managed completion/cancellation state, dependency continuations, and delegate batch wrappers.")]
    public JobHandle EnqueueParallel(Action<int> action, int size, int batchSize = -1,
        params Span<JobHandle> dependencies) =>
        EnqueueParallelInternal(action, size, batchSize, dependencies);
    
    [AllocatingCompatibility("Allocates managed typed completion/cancellation state and a delegate result wrapper.")]
    public JobHandle<T> Enqueue<T>(Func<T> job, params Span<JobHandle> dependencies)
    {
        if (!TryEnterEnqueue()) return default;
        try
        {
            return EnqueueAccepted(job, dependencies);
        }
        finally
        {
            Interlocked.Decrement(ref _admissionState);
        }
    }

    private JobHandle<T> EnqueueAccepted<T>(Func<T> job, Span<JobHandle> dependencies)
    {
        JobCompletion? dependenciesCompletion = RegisterDependencies(dependencies);
        var completion = new JobCompletion<T>(1);
        var cts = new CancellationTokenSource();
        
        var wrapper = _jobWrapperPool.Rent();
        
        wrapper.Action = () =>
        {
            try 
            {
                T result = job();
                completion.SetResult(result);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
                ReportJobError(ex);
            }
        };

        wrapper.Cts = cts;
        wrapper.IsParallel = false;
        wrapper.Completion = completion;

        Action enqueueAction = () =>
        {
            int targetThread = (Interlocked.Increment(ref _enqueueIndex) & int.MaxValue) % _workerCount;
            _threadStates[targetThread].Queue.Enqueue(wrapper);
            _workSemaphore.Release();
        };

        Interlocked.Increment(ref _outstandingJobs);
        if (dependenciesCompletion is null) enqueueAction();
        else dependenciesCompletion.AddContinuation(enqueueAction);

        return new JobHandle<T>(completion, cts);
    }

    [AllocatingCompatibility("Allocates managed completion state and continuation delegates for the combined handles.")]
    public static JobHandle Combine(params JobHandle[]? handles)
    {
        if (handles == null || handles.Length == 0) return default;
        var completion = new JobCompletion(handles.Length);
        foreach (var handle in handles)
        {
            if (handle.Completion is null)
            {
                completion.Signal();
            }
            else
            {
                handle.Completion.AddContinuation(() => completion.Signal());
            }
        }

        return new JobHandle(completion, null);
    }

    public void WaitForCompletion()
    {
        var spinWait = new SpinWait();
        while (Volatile.Read(ref _outstandingJobs) > 0)
        {
            spinWait.SpinOnce();
        }
    }

    private static JobCompletion? RegisterDependencies(Span<JobHandle> dependencies)
    {
        if (dependencies.IsEmpty) return null;
        var completion = new JobCompletion(dependencies.Length);
        foreach (var dependency in dependencies)
        {
            if (dependency.Completion is null) completion.Signal();
            else dependency.Completion.AddContinuation(() => completion.Signal());
        }
        return completion;
    }

    private bool TryEnterEnqueue()
    {
        if (!_isRunning) return false;
        if (Interlocked.Increment(ref _admissionState) >= 0) return true;
        Interlocked.Decrement(ref _admissionState);
        return false;
    }

    internal void DrainAndShutdown()
    {
        Interlocked.Or(ref _admissionState, int.MinValue);
        var spinWait = new SpinWait();
        while ((Volatile.Read(ref _admissionState) & int.MaxValue) != 0) spinWait.SpinOnce();
        WaitForCompletion();
        Shutdown();
    }

    public void Shutdown()
    {
        _isRunning = false;
        _workSemaphore.Release(_workerCount);
        foreach (var thread in _threads)
        {
            thread.Join();
        }

        foreach (var item in _threadStates)
        {
            item.Dispose();
        }

        Array.Clear(_threadStates, 0, _threadStates.Length);
        Array.Clear(_threads, 0, _threads.Length);
        _jobWrapperPool.Dispose();
    }

    public void Dispose() => Shutdown();

    private void ReportJobError(Exception exception)
    {
        if (_onJobError is not null)
        {
            try
            {
                _onJobError(exception);
            }
            catch
            {
                // A reporting failure must not replace the job failure or strand its completion.
            }
            return;
        }

        var color = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkMagenta;
        Console.WriteLine($"[ERROR] Job failed: {exception}");
        Console.ForegroundColor = color;
    }
}
