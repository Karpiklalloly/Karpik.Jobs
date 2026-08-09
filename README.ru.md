# Karpik.Jobs

[English](README.md) | [Русский](README.ru.md)

`Karpik.Jobs` — библиотека для запуска зависимых и пакетных задач в стиле Unity Job System. Она предоставляет два API:

- `JobScheduler` — основной API для `unmanaged`-задач. После создания планировщика постановка и выполнение задач могут обходиться без managed-аллокаций.
- `JobSystem` — более простой совместимый API на делегатах. Он удобен для прототипов, но создаёт managed-объекты и не предназначен для no-GC hot path.

Проекты рассчитаны на .NET 10.

## Подключение

Пока библиотека не публикуется как NuGet-пакет. Добавьте `Karpik.Jobs` и `Karpik.Memory` в solution, затем подключите `Karpik.Jobs` к своему проекту:

```xml
<ItemGroup>
  <ProjectReference Include="..\Karpik.Jobs\Karpik.Jobs.csproj" />
</ItemGroup>
```

Импортируйте пространства имён библиотеки:

```csharp
using Karpik.Jobs;
using Karpik.Memory;
```

## Быстрый старт с `JobScheduler`

Задача должна быть `unmanaged`-структурой и реализовывать `IJob`. Она не может содержать ссылочные поля, строки, managed-массивы или делегаты. Для передачи данных используйте контейнеры из `Karpik.Memory`, например `NativeResult<T>` и `NativeArray<T>`.

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

`Schedule` только регистрирует задачу. `Complete` выполняет её на вызывающем потоке и возвращает дескриптор планировщику.

## Индексные задачи

Для обработки диапазона индексов реализуйте `IJobFor`:

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

В текущей реализации один вызов `ScheduleParallel` создаёт один дескриптор. `Complete` последовательно вызывает `Execute(index)` для всего диапазона. `batchSize` сохраняется в метаданных задачи, но сам по себе не распределяет один диапазон между несколькими worker-потоками.

## Зависимости

Передавайте зависимости как `ReadOnlySpan<ValueJobHandle>`. Зависимая задача не может выполниться, пока не завершатся все её зависимости:

```csharp
ValueJobHandle load = scheduler.Schedule(in loadJob);
ValueJobHandle process = scheduler.Schedule(
    in processJob,
    stackalloc[] { load });

scheduler.Complete(load);
scheduler.Complete(process);
```

Пока зависимости не завершены, `TryComplete` возвращает `false`, а `Complete` выбрасывает `InvalidOperationException`:

```csharp
if (!scheduler.TryComplete(process))
{
    scheduler.Complete(load);
    scheduler.Complete(process);
}
```

Максимальное количество зависимостей одной задачи задаётся параметром `maxDependenciesPerJob`.

## Worker-потоки

Чтобы выполнять задачи в фоне, запустите worker runtime и публикуйте зарегистрированные задачи в очереди worker-ов:

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
    // Задача зарегистрирована, но не попала в очередь.
    // Её всё ещё можно выполнить на текущем потоке.
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

Worker-ы используют work stealing: свободный worker может забрать задачу из очереди другого. `workerQueueCapacity` должен быть положительной степенью двойки. После `StopWorkers` повторно запустить worker-ы того же экземпляра планировщика нельзя.

## Ограничения и ошибки

`Schedule` и `ScheduleParallel` выбрасывают `InvalidOperationException`, если задачу невозможно зарегистрировать. В игровом цикле при ожидаемом исчерпании ресурсов удобнее использовать `TrySchedule` и `TryScheduleParallel`:

```csharp
if (!scheduler.TrySchedule(in job, out ValueJobHandle handle))
{
    JobRuntimeDiagnostics diagnostics = scheduler.GetDiagnostics();
    Console.WriteLine($"Закончились дескрипторы: {diagnostics.DescriptorExhaustionCount}");
    Console.WriteLine($"Payload слишком велик: {diagnostics.PayloadTooLargeCount}");
}
```

Если `Execute` выбрасывает исключение, `Complete` пробрасывает его вызывающему коду. После этого состояние можно проверить через `HasException(handle)` и `GetException(handle)`.

Основные параметры конструктора `JobScheduler`:

| Параметр | Назначение |
| --- | --- |
| `capacity` | Максимальное число одновременно зарегистрированных задач |
| `maxPayloadByteLength` | Максимальный размер структуры задачи в байтах |
| `payloadAlignment` | Выравнивание payload; должно быть степенью двойки |
| `maxDependenciesPerJob` | Максимальное число зависимостей одной задачи |
| `workerCount` | Число worker-потоков |
| `workerQueueCapacity` | Размер очереди каждого worker-а; должен быть степенью двойки |

Все `NativeArray<T>`, `NativeResult<T>` и другие владельцы native-памяти должны жить дольше задач, использующих их handle или slice. Освобождайте их только после завершения этих задач.

## Простой API: `JobSystem`

Если managed-аллокации допустимы, ставьте в очередь обычные делегаты:

```csharp
using JobSystem jobs = new(workerCount: Environment.ProcessorCount);

int value = 0;

JobHandle first = jobs.Enqueue(() => value = 10);
JobHandle second = jobs.Enqueue(() => value *= 2, first);

second.Wait();
Console.WriteLine(value); // 20
```

Параллельная обработка диапазона:

```csharp
int[] values = new int[10_000];

JobHandle handle = jobs.EnqueueParallel(
    index => values[index] = index * 2,
    size: values.Length,
    batchSize: 128);

handle.Wait();
```

Объединение нескольких зависимостей перед следующим этапом:

```csharp
JobHandle a = jobs.Enqueue(LoadA);
JobHandle b = jobs.Enqueue(LoadB);
JobHandle both = JobSystem.Combine(a, b);
JobHandle final = jobs.Enqueue(ProcessResults, both);

final.Wait();
```

`JobHandle` поддерживает `Wait()`, `IsCompleted`, `Cancel()` и `await`:

```csharp
static async Task RunAsync(JobSystem jobs)
{
    JobHandle handle = jobs.Enqueue(DoWork);
    await handle;
}
```

Для ожидания всех опубликованных задач используйте `jobs.WaitForCompletion()`. После работы вызовите `Dispose()` или `Shutdown()`; после остановки новые задачи не принимаются.

## Какой API выбрать

| | `JobScheduler` | `JobSystem` |
| --- | --- | --- |
| Представление задачи | `unmanaged struct` | `Action` / `Func<T>` |
| Managed-аллокации в steady state | Можно избежать | Есть |
| Удобство | Требует native-контейнеров | Использует обычные C#-делегаты |
| Основной сценарий | Hot path и игровой цикл | Прототипы и совместимый код |

Для нового производительного кода начинайте с `JobScheduler`. Используйте `JobSystem`, если простота интеграции важнее отсутствия сборки мусора.

## Примеры и тесты

- Полный delegate-based пример находится в [`Sample/Program.cs`](Sample/Program.cs).
- Примеры value-type задач находятся в [`Karpik.Jobs.Tests`](Karpik.Jobs.Tests).

Запуск:

```powershell
dotnet run --project Sample/Sample.csproj
dotnet test Karpik.Jobs.sln
```
