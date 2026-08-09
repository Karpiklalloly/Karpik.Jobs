# Karpik.Jobs

[English](README.md) | [Русский](README.ru.md)

`Karpik.Jobs` is a library for running dependent and batched jobs in the style of Unity's Job System. It provides two APIs:

- `JobScheduler` is the primary API for `unmanaged` jobs. After initialization, scheduling and execution can run without managed allocations.
- `JobSystem` is a simpler delegate-based compatibility API. It is convenient for prototypes, but allocates managed objects and is not intended for no-GC hot paths.

The projects target .NET 10.

## Installation

The library is not currently published as a NuGet package. Add `Karpik.Jobs` and `Karpik.Memory` to your solution, then reference `Karpik.Jobs` from your project:

```xml
<ItemGroup>
  <ProjectReference Include="..\Karpik.Jobs\Karpik.Jobs.csproj" />
</ItemGroup>
```

Import the library namespaces:

```csharp
using Karpik.Jobs;
using Karpik.Memory;
```

## Quick start with `JobScheduler`

A job must be an `unmanaged` struct that implements `IJob`. It cannot contain reference-type fields, strings, managed arrays, or delegates. Use containers from `Karpik.Memory`, such as `NativeResult<T>` and `NativeArray<T>`, to pass data in and out of jobs.

```csharp
using Karpik.Jobs;
using Karpik.Memory;

using NativeResult<int> result = new();
using JobScheduler scheduler = new(
    capacity: 16,
    maxPayloadByteLength: 64);

WriteResultJob job = new()
{
    Result = result.AsHandle(),
    Value = 42
};

ValueJobHandle handle = scheduler.Schedule(in job);
scheduler.Complete(handle);

Console.WriteLine(result.Value); // 42

struct WriteResultJob : IJob
{
    public NativeResultHandle<int> Result;
    public int Value;

    public void Execute()
    {
        Result.Value = Value;
    }
}
```

`Schedule` only registers the job. `Complete` executes it on the calling thread and returns its descriptor to the scheduler.

## Indexed jobs

Implement `IJobFor` to process a range of indices:

```csharp
using NativeArray<int> values = new(1_000);
using JobScheduler scheduler = new(
    capacity: 16,
    maxPayloadByteLength: 64);

IncrementJob job = new()
{
    Values = values.AsSlice()
};

ValueJobHandle handle = scheduler.ScheduleParallel(
    in job,
    length: values.Length,
    batchSize: 64);

scheduler.Complete(handle);

struct IncrementJob : IJobFor
{
    public NativeSlice<int> Values;

    public void Execute(int index)
    {
        Values[index]++;
    }
}
```

In the current implementation, one `ScheduleParallel` call creates one descriptor. `Complete` invokes `Execute(index)` sequentially for the entire range. `batchSize` is stored in the job metadata, but does not by itself distribute one range across several worker threads.

## Dependencies

Pass dependencies as a `ReadOnlySpan<ValueJobHandle>`. A dependent job cannot execute until all its dependencies have completed:

```csharp
ValueJobHandle load = scheduler.Schedule(in loadJob);
ValueJobHandle process = scheduler.Schedule(
    in processJob,
    stackalloc[] { load });

scheduler.Complete(load);
scheduler.Complete(process);
```

`TryComplete` returns `false` while dependencies are pending. `Complete` throws `InvalidOperationException` in the same situation:

```csharp
if (!scheduler.TryComplete(process))
{
    scheduler.Complete(load);
    scheduler.Complete(process);
}
```

The maximum number of dependencies per job is configured with `maxDependenciesPerJob`.

## Worker threads

Start the worker runtime and publish scheduled jobs to worker queues to execute them in the background:

```csharp
using JobScheduler scheduler = new(
    capacity: 64,
    maxPayloadByteLength: 64,
    workerCount: Environment.ProcessorCount,
    workerQueueCapacity: 128);

scheduler.StartWorkers();

ValueJobHandle handle = scheduler.Schedule(in job);
if (!scheduler.TryPublish(handle, workerIndex: 0))
{
    // The job is registered but was not added to the queue.
    // It can still be executed on the current thread.
    scheduler.Complete(handle);
}
else
{
    SpinWait spin = default;
    while (!scheduler.IsCompleted(handle))
    {
        spin.SpinOnce();
    }
}

scheduler.StopWorkers();
```

Workers use work stealing: an idle worker can take a job from another worker's queue. `workerQueueCapacity` must be a positive power of two. Once `StopWorkers` has been called, the same scheduler instance cannot start its workers again.

## Limits and errors

`Schedule` and `ScheduleParallel` throw `InvalidOperationException` when the job cannot be registered. In a game loop, prefer `TrySchedule` and `TryScheduleParallel` when capacity exhaustion is expected:

```csharp
if (!scheduler.TrySchedule(in job, out ValueJobHandle handle))
{
    JobRuntimeDiagnostics diagnostics = scheduler.GetDiagnostics();
    Console.WriteLine($"Descriptor exhaustion: {diagnostics.DescriptorExhaustionCount}");
    Console.WriteLine($"Payload too large: {diagnostics.PayloadTooLargeCount}");
}
```

If `Execute` throws, `Complete` rethrows the exception. You can then inspect the state with `HasException(handle)` and `GetException(handle)`.

Key `JobScheduler` constructor parameters:

| Parameter | Purpose |
| --- | --- |
| `capacity` | Maximum number of jobs registered at the same time |
| `maxPayloadByteLength` | Maximum job struct size in bytes |
| `payloadAlignment` | Payload alignment; must be a power of two |
| `maxDependenciesPerJob` | Maximum number of dependencies for one job |
| `workerCount` | Number of worker threads |
| `workerQueueCapacity` | Queue capacity per worker; must be a power of two |

All `NativeArray<T>`, `NativeResult<T>`, and other native-memory owners must outlive every job that uses their handles or slices. Dispose them only after those jobs have completed.

## Simple API: `JobSystem`

When managed allocations are acceptable, enqueue ordinary delegates:

```csharp
using JobSystem jobs = new(workerCount: Environment.ProcessorCount);

int value = 0;

JobHandle first = jobs.Enqueue(() => value = 10);
JobHandle second = jobs.Enqueue(() => value *= 2, first);

second.Wait();
Console.WriteLine(value); // 20
```

Process an index range in parallel:

```csharp
int[] values = new int[10_000];

JobHandle handle = jobs.EnqueueParallel(
    index => values[index] = index * 2,
    size: values.Length,
    batchSize: 128);

handle.Wait();
```

Combine several dependencies before scheduling the next stage:

```csharp
JobHandle a = jobs.Enqueue(LoadA);
JobHandle b = jobs.Enqueue(LoadB);
JobHandle both = JobSystem.Combine(a, b);
JobHandle final = jobs.Enqueue(ProcessResults, both);

final.Wait();
```

`JobHandle` supports `Wait()`, `IsCompleted`, `Cancel()`, and `await`:

```csharp
static async Task RunAsync(JobSystem jobs)
{
    JobHandle handle = jobs.Enqueue(DoWork);
    await handle;
}
```

Use `jobs.WaitForCompletion()` to wait for all published jobs. Call `Dispose()` or `Shutdown()` when finished; no new jobs are accepted after shutdown.

## Choosing an API

| | `JobScheduler` | `JobSystem` |
| --- | --- | --- |
| Job representation | `unmanaged struct` | `Action` / `Func<T>` |
| Managed allocations in steady state | Can be avoided | Yes |
| Convenience | Requires native containers | Uses ordinary C# delegates |
| Primary use case | Hot paths and game loops | Prototypes and compatibility code |

For new performance-sensitive code, start with `JobScheduler`. Use `JobSystem` when ease of integration matters more than avoiding garbage collection.

## Samples and tests

- The complete delegate-based sample is in [`Sample/Program.cs`](Sample/Program.cs).
- Value-type job examples are available in [`Karpik.Jobs.Tests`](Karpik.Jobs.Tests).

Run them with:

```powershell
dotnet run --project Sample/Sample.csproj
dotnet test Karpik.Jobs.sln
```
