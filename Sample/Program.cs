using System.Diagnostics;
using Karpik.Jobs;

namespace Sample;

class Program
{
    const int count = 12_000_000;
    const int batchSize = 1024;

    static void Main(string[] args)
    {
        var scheduler = new JobSystem(Environment.ProcessorCount);
        Console.WriteLine($"System has {Environment.ProcessorCount} threads.");
        Stopwatch sw = new Stopwatch();
        long time = 0;
        const int iterations = 100;
        for (int i = 0; i < iterations; i++)
        {
            using var arrayA = new SimpleNativeArray<int>(count);
            using var arrayB = new SimpleNativeArray<int>(count);
            using var arrayC_Result = new SimpleNativeArray<long>(count);
            var finalSum = new long();

            var was = GC.GetTotalMemory(false);
            sw.Restart();

            Console.WriteLine("Iteration " + i);

            var initAHandle = scheduler.EnqueueParallel(index => { arrayA[index] = 10 + index; }, count, batchSize);

            var initBHandle = scheduler.EnqueueParallel(index => { arrayB[index] = 100 + index; }, count, batchSize);

            var stage1Handle = JobSystem.Combine(initAHandle, initBHandle);

            var stage2Handle = scheduler.EnqueueParallel(
                index => { arrayC_Result[index] = arrayA[index] + arrayB[index]; }, count, batchSize, stage1Handle);



            var finalHandle = scheduler.Enqueue(() =>
            {
                long sum = 0;
                for (int i = 0; i < arrayC_Result.Length; i++)
                {
                    sum += arrayC_Result[i];
                }

                finalSum = sum;
            }, stage2Handle);


            Console.WriteLine("All tasks enqueued. Waiting for completion...");
            finalHandle.Wait();

            int testIndex1 = 0;
            Console.WriteLine($"arrayC[{testIndex1}] = {arrayC_Result[testIndex1]} (Expected: 110)");

            int testIndex2 = count / 2;
            long expected2 = 110 + 2L * testIndex2;
            Console.WriteLine($"arrayC[{testIndex2}] = {arrayC_Result[testIndex2]} (Expected: {expected2:N0})");

            long n = count;
            long expectedSum = n * (2 * 110 + 2 * (n - 1)) / 2;
            Console.WriteLine($"Final sum: {finalSum:N0} (Expected: {expectedSum:N0})");

            if (finalSum == expectedSum)
            {
                var color = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Correct!");
                Console.ForegroundColor = color;
            }
            else
            {
                var color = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.DarkMagenta;
                Console.WriteLine("Incorrect!");
                Console.ForegroundColor = color;
            }

            sw.Stop();
            time += sw.ElapsedMilliseconds;
            Console.WriteLine($"Time elapsed: {sw.ElapsedMilliseconds} мс.");
            Console.WriteLine($"{(GC.GetTotalMemory(false) - was) / 1024f / 1024:N2} Mb used.\n");
        }

        Console.WriteLine("Win!");
        Console.WriteLine(1.0 * time / iterations);
        scheduler.Dispose();
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }
}