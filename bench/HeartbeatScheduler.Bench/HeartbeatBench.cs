using System.Collections.Concurrent;
using System.Diagnostics;
using CommunityToolkit.HighPerformance.Helpers;
using HeartbeatScheduling;
using Schedulers;

/// <summary>
/// Flat parallel loops of micro-tasks, each a map (<c>Out[i] = Body(i)</c>, no shared writes), against a plain loop:
/// Parallel.For (a delegate per iteration on the thread pool), fixed-size morsels (Partitioner chunks, as a
/// morsel-driven scheduler would), CommunityToolkit's ParallelHelper (an inlined struct body, the range split up
/// front into one piece per core), ZeroAllocJobScheduler's IJobParallelFor (its own workers, work stealing, a fixed
/// batch size) and the heartbeat scheduler (class and struct bodies, per iteration and batched, with and without the
/// iteration-cost estimate). Uniform bodies of
/// ~0.5 ns, ~6 ns and ~150 ns (as measured), then irregular ones, where fixed up-front splits leave threads idle. Reported as ns per
/// iteration for the plain loop and speed-up over it for the rest (wall clock of the whole loop, best of 5).
/// </summary>
static class HeartbeatBench
{
    const int MaxN = 4_000_000;
    static readonly double[] Data = Enumerable.Range(0, MaxN).Select(i => i * 0.5).ToArray();
    static readonly double[] Out = new double[MaxN];
    static int[] s_cost = [];                    // the body's inner-loop count per index: the workload's shape

    static double Body(int i)
    {
        var x = Data[i];
        for (var k = s_cost[i]; k > 0; k--) x = x * 1.0000001 + 1e-9;
        return x;
    }

    sealed class Loop : IHeartbeatLoop
    {
        public void Execute(int index, int worker) => Out[index] = Body(index);
    }

    struct StructLoop : IHeartbeatLoop
    {
        public readonly void Execute(int index, int worker) => Out[index] = Body(index);
    }

    struct StructBatchLoop : IHeartbeatBatchLoop
    {
        public readonly void Execute(int start, int end, int worker) { for (var i = start; i < end; i++) Out[i] = Body(i); }
    }

    readonly struct HelperAction : IAction
    {
        public void Invoke(int i) => Out[i] = Body(i);
    }

    sealed class Job(int batch) : IJobParallelFor
    {
        public int BatchSize => batch;
        public int ThreadCount => 0;
        public void Execute(int i) => Out[i] = Body(i);
        public void Finish() { }
    }

    /// <param name="idle">The heartbeat scheduler's idle policy between loops.</param>
    /// <param name="quality">How the operating system schedules its workers.</param>
    public static void Run(HeartbeatIdlePolicy idle, HeartbeatThreadQuality quality)
    {
        var workers = Math.Max(1, Environment.ProcessorCount - 1);
        Console.WriteLine($"--- {Environment.ProcessorCount} cores (DOTNET_PROCESSOR_COUNT sets it for every contender): heartbeat uses the caller + {workers} workers, {new HeartbeatOptions().HeartbeatMicroseconds} µs heartbeat, idle policy {idle}, thread quality {quality}; ZeroAllocJobScheduler {Environment.ProcessorCount} workers");
        Console.WriteLine("    hb = heartbeat; est = given the mean iteration cost, measured from the plain loop, as up-front splits need");
        Console.WriteLine("    per n: speed-up over the plain loop; B/loop = bytes allocated per loop, on every thread; GC/1k = collections per 1,000 loops (gen0/gen1/gen2)");
        // One job scheduler for the run: its idle workers block (measured: no CPU), and disposing one while its workers
        // wind down throws on a worker thread (1.1.2), so it is disposed once, after they have settled.
        var jobs = new JobScheduler(new JobScheduler.Config { ThreadPrefixName = "Jobs", ThreadCount = 0, MaxExpectedConcurrentJobs = 256 });
        foreach (var (label, cost, sizes, batch) in new (string, Func<int, int, int>, int[], int)[]
        {
            ("~0.5 ns body", (_, _) => 0, [1_000, 100_000, 4_000_000], 256),
            ("~6 ns body", (_, _) => 20, [100, 1_000, 100_000], 16),
            ("~150 ns body", (_, _) => 200, [16, 100, 10_000], 1),
            ("ramp: ~0.5 ns rising to ~30 ns", (i, n) => (int)(40L * i / n), [1_000, 100_000], 16),
            ("hot spot: the first 1/16 at ~150 ns, the rest ~0.5 ns", (i, n) => i < n / 16 ? 200 : 0, [1_000, 100_000], 16),
        })
        {
            Console.WriteLine($"=== {label} (batch {batch})");
            Console.WriteLine($"  {"n",9}  {"plain ns",8}  {"Parallel.For",12}  {"morsels",7}  {"ParallelHelper",14}  {"ZeroAlloc",9}  {"hb class",8}  {"hb struct",9}  {"hb struct batch",15}  {"hb struct est",13}  {"hb struct batch est",19}  {"splits",6}");
            foreach (var n in sizes)
            {
                s_cost = Enumerable.Range(0, n).Select(i => cost(i, n)).ToArray();
                var seq = Measure.Time(() => { for (var i = 0; i < n; i++) Out[i] = Body(i); });
                // Capped at the core count: DOTNET_PROCESSOR_COUNT sizes the thread pool, but the pool may add threads beyond it.
                var cap = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
                var pfor = Measure.Time(() => Parallel.For(0, n, cap, i => Out[i] = Body(i)));
                var morsel = Measure.Time(() =>
                    Parallel.ForEach(Partitioner.Create(0, n, Math.Max(1, Math.Min(16_384, n / Environment.ProcessorCount))), cap, r =>
                    {
                        for (var i = r.Item1; i < r.Item2; i++) Out[i] = Body(i);
                    }));
                var helper = Measure.Time(() => ParallelHelper.For(0, n, default(HelperAction)));
                var job = new Job(batch);
                var zeroAlloc = Measure.Time(() =>
                {
                    var handle = jobs.Schedule(job, n);
                    jobs.Flush();
                    handle.Complete();
                });
                // A heartbeat scheduler per measurement, gone before the next contender: its idle workers spin for a
                // while before they park, and would take cores from it.
                var estimate = seq.Nanoseconds / n * Stopwatch.Frequency / 1e9;     // mean ticks per iteration
                Measurement heart, heartStruct, heartStructBatch, heartEst, heartBatchEst;
                double splits;
                using (var hb = new HeartbeatScheduler(new HeartbeatOptions { Workers = workers, IdlePolicy = idle, StartWorker = HeartbeatThreads.Background(quality: quality) }))
                {
                    var loop = new Loop();
                    heart = Measure.Time(() => hb.For(0, n, loop));
                    heartStruct = Measure.Time(() => hb.For(0, n, new StructLoop()));
                    heartStructBatch = Measure.Time(() => hb.For(0, n, new StructBatchLoop(), batch));
                    heartEst = Measure.Time(() => hb.ForTicks(0, n, new StructLoop(), estimate));
                    var splits0 = hb.Splits;
                    var runs = 0;
                    heartBatchEst = Measure.Time(() => { hb.ForTicks(0, n, new StructBatchLoop(), batch, estimate); runs++; });
                    splits = (double)(hb.Splits - splits0) / runs;
                }
                Measurement[] all = [pfor, morsel, helper, zeroAlloc, heart, heartStruct, heartStructBatch, heartEst, heartBatchEst];
                int[] widths = [12, 7, 14, 9, 8, 9, 15, 13, 19];
                var speed = string.Concat(all.Select((m, k) => "  " + $"{seq.Nanoseconds / m.Nanoseconds:F1}x".PadLeft(widths[k])));
                Console.WriteLine($"  {n,9:N0}  {seq.Nanoseconds / n,8:F2}{speed}  {splits,6:F1}");
                Console.WriteLine($"  {"B/loop",9}  {seq.Bytes,8}" + string.Concat(all.Select((m, k) => "  " + m.Bytes.PadLeft(widths[k]))));
                Console.WriteLine($"  {"GC/1k",9}  {seq.Collections,8}" + string.Concat(all.Select((m, k) => "  " + m.Collections.PadLeft(widths[k]))));
            }
        }
        Thread.Sleep(200);
        jobs.Dispose();
    }
}
