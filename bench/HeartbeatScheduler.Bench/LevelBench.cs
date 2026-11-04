using System.Collections.Concurrent;
using System.Diagnostics;
using CommunityToolkit.HighPerformance.Helpers;
using HeartbeatScheduling;
using Schedulers;

/// <summary>
/// Level-by-level evaluation of a leveled DAG of micro-tasks: each level is one parallel loop over its nodes, and the
/// loop's join is the barrier before the next level. Two graphs of ~110,000 nodes: 300 levels of widths log-uniform from
/// 1 to 3,000 (most levels narrow, a few wide), and 3,000 narrow levels of 1 to 200 (most below a heartbeat). Node costs
/// of ~1.5 µs, ~100 ns, or mixed (log-uniform from ~10 ns to ~1.5 µs), as measured. Against a single thread (1.0x): Parallel.For, fixed-size chunks (Partitioner), ParallelHelper and ZeroAllocJobScheduler (an
/// IJobParallelFor, batch size 16) per level, the heartbeat scheduler without an estimate, and with the
/// host's estimate (its measured mean node cost), which runs levels predicted under a heartbeat as plain loops on the
/// calling thread and splits those predicted over one up front. Best of 5 whole evaluations.
/// </summary>
static class LevelBench
{
    const double NsPerUnit = 0.73;                // the body's cost per inner-loop step (measured: 200 steps ~ 146 ns)

    static double[] s_data = [], s_out = [];
    static int[] s_cost = [];

    static double Node(int i)
    {
        var x = s_data[i];
        for (var k = s_cost[i]; k > 0; k--) x = x * 1.0000001 + 1e-9;
        return x;
    }

    struct Level : IHeartbeatLoop
    {
        public readonly void Execute(int index, int worker) => s_out[index] = Node(index);
    }

    /// <summary>A level as one IJobParallelFor (which indexes from 0): reused for every level, its offset set first.</summary>
    sealed class LevelJob : IJobParallelFor
    {
        public int Offset;
        public int BatchSize => 16;
        public int ThreadCount => 0;
        public void Execute(int i) => s_out[Offset + i] = Node(Offset + i);
        public void Finish() { }
    }

    readonly struct HelperLevel : IAction
    {
        public void Invoke(int i) => s_out[i] = Node(i);
    }

    public static void Run(HeartbeatIdlePolicy idle, HeartbeatThreadQuality quality)
    {
        // One job scheduler for the run: disposing one while its workers wind down throws on a worker thread (1.1.2).
        var jobs = new JobScheduler(new JobScheduler.Config { ThreadPrefixName = "Jobs", ThreadCount = 0, MaxExpectedConcurrentJobs = 256 });
        foreach (var (levels, maxWidth) in new[] { (300, 3_000), (3_000, 200) }) Run(idle, quality, jobs, levels, maxWidth);
        Thread.Sleep(200);
        jobs.Dispose();
    }

    static void Run(HeartbeatIdlePolicy idle, HeartbeatThreadQuality quality, JobScheduler jobs, int Levels, int MaxWidth)
    {
        var rnd = new Random(42);
        var offsets = new int[Levels + 1];
        for (var l = 0; l < Levels; l++)
            offsets[l + 1] = offsets[l] + (int)Math.Round(Math.Pow(MaxWidth, rnd.NextDouble()));   // log-uniform 1..MaxWidth
        var nodes = offsets[Levels];
        s_data = Enumerable.Range(0, nodes).Select(i => i * 0.5).ToArray();
        s_out = new double[nodes];
        var workers = Math.Max(1, Environment.ProcessorCount - 1);
        var options = new HeartbeatOptions { Workers = workers, IdlePolicy = idle, StartWorker = HeartbeatThreads.Background(quality: quality) };
        Console.WriteLine($"--- {Environment.ProcessorCount} cores: {Levels} levels, {nodes:N0} nodes (widths 1..{MaxWidth:N0}, log-uniform); heartbeat {options.HeartbeatMicroseconds} µs, caller + {workers} workers, idle policy {idle}, thread quality {quality}");
        Console.WriteLine($"  {"node cost",-26} {"1 thread",10}" + string.Concat(Columns.Select(c => "  " + c.Name.PadLeft(c.Width))) + "  levels bypassed (share of the work)");
        foreach (var (label, cost) in new (string, Func<int, int>)[]
        {
            ("~1.5 µs", _ => (int)(1000 / NsPerUnit)),
            ("~100 ns", _ => (int)(100 / NsPerUnit)),
            ("mixed ~10 ns .. ~1.5 µs", _ => (int)(Math.Pow(100, rnd.NextDouble()) * 10 / NsPerUnit)),
        })
        {
            s_cost = Enumerable.Range(0, nodes).Select(cost).ToArray();
            var single = Evaluate(() => { for (var i = 0; i < nodes; i++) s_out[i] = Node(i); });
            var estimate = single.Nanoseconds / nodes * Stopwatch.Frequency / 1e9;      // the host's mean node cost, in ticks
            // Capped at the core count: DOTNET_PROCESSOR_COUNT sizes the thread pool, but the pool may add threads beyond it.
            var cap = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
            var pfor = Evaluate(() => { for (var l = 0; l < Levels; l++) Parallel.For(offsets[l], offsets[l + 1], cap, i => s_out[i] = Node(i)); });
            var chunks = Evaluate(() =>
            {
                for (var l = 0; l < Levels; l++)
                {
                    int lo = offsets[l], hi = offsets[l + 1];
                    Parallel.ForEach(Partitioner.Create(lo, hi, Math.Max(1, Math.Min(16_384, (hi - lo) / Environment.ProcessorCount))), cap, r =>
                    {
                        for (var i = r.Item1; i < r.Item2; i++) s_out[i] = Node(i);
                    });
                }
            });
            var helper = Evaluate(() => { for (var l = 0; l < Levels; l++) ParallelHelper.For(offsets[l], offsets[l + 1], default(HelperLevel)); });
            var job = new LevelJob();
            var zeroAlloc = Evaluate(() =>
            {
                for (var l = 0; l < Levels; l++)
                {
                    job.Offset = offsets[l];
                    var handle = jobs.Schedule(job, offsets[l + 1] - offsets[l]);
                    jobs.Flush();
                    handle.Complete();
                }
            });
            Measurement heart, heartEst;
            using (var hb = new HeartbeatScheduler(options))
            {
                heart = Evaluate(() => { for (var l = 0; l < Levels; l++) hb.For(offsets[l], offsets[l + 1], new Level()); });
                heartEst = Evaluate(() => { for (var l = 0; l < Levels; l++) hb.ForTicks(offsets[l], offsets[l + 1], new Level(), estimate); });
            }
            var beat = options.HeartbeatMicroseconds * Stopwatch.Frequency / 1e6;
            var bypassed = 0;
            long bypassedNodes = 0;
            for (var l = 0; l < Levels; l++)
            {
                var width = offsets[l + 1] - offsets[l];
                if (width * estimate < beat) { bypassed++; bypassedNodes += width; }
            }
            var t = single.Nanoseconds;
            Measurement[] all = [pfor, chunks, helper, zeroAlloc, heart, heartEst];
            string Row(Func<Measurement, int, string> cell) => string.Concat(all.Select((m, k) => "  " + cell(m, k).PadLeft(Columns[k].Width)));
            Console.WriteLine($"  {label,-26} {t / 1e6,8:F2}ms" + Row((m, _) => $"{t / m.Nanoseconds:F2}x") + $"  {bypassed} of {Levels} ({(double)bypassedNodes / nodes:P0})");
            Console.WriteLine($"  {"  B/evaluation",-26} {single.Bytes,10}" + Row((m, _) => m.Bytes));
            Console.WriteLine($"  {"  GC/1k evaluations",-26} {single.Collections,10}" + Row((m, _) => m.Collections));
        }
    }

    static readonly (string Name, int Width)[] Columns =
        [("Parallel.For", 12), ("chunks", 9), ("ParallelHelper", 14), ("ZeroAlloc", 9), ("hb", 6), ("hb + estimate", 13)];

    /// <summary>Five whole evaluations after a warm-up: the best one's time, and the allocations and collections of all five.</summary>
    static Measurement Evaluate(Action evaluate) => Measure.Time(evaluate, warmMilliseconds: 300, roundMilliseconds: 1);
}
