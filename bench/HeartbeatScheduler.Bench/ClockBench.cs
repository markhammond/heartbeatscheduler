using System.Diagnostics;
using HeartbeatScheduling;

/// <summary>
/// The beat on a native clock and on clocks made slower (the scheduler's internal test mode), as on hosts whose clock is
/// slow to read: one idle thread reads it for all, the rest only watch their mailboxes. Nodes
/// of ~100 ns throughout, with the host's estimate: level-by-level evaluation of the two leveled graphs and flat loops.
/// Speed-up over a single thread, best of 5; and the beats idle workers keep (mean interval over 100 ms).
/// </summary>
static class ClockBench
{
    const double NsPerUnit = 0.73;
    static double[] s_data = [], s_out = [];
    static int[] s_cost = [];

    struct Node : IHeartbeatLoop
    {
        public readonly void Execute(int i, int worker)
        {
            var x = s_data[i];
            for (var k = s_cost[i]; k > 0; k--) x = x * 1.0000001 + 1e-9;
            s_out[i] = x;
        }
    }

    static int[] Offsets(int levels, int maxWidth)
    {
        var rnd = new Random(42);
        var offsets = new int[levels + 1];
        for (var l = 0; l < levels; l++) offsets[l + 1] = offsets[l] + (int)Math.Round(Math.Pow(maxWidth, rnd.NextDouble()));
        return offsets;
    }

    public static void Run(double slowClockNanoseconds)
    {
        var wide = Offsets(300, 3_000);
        var narrow = Offsets(3_000, 200);
        var nodes = Math.Max(wide[^1], narrow[^1]);
        s_data = Enumerable.Range(0, nodes).Select(i => i * 0.5).ToArray();
        s_out = new double[nodes];
        s_cost = Enumerable.Repeat((int)(100 / NsPerUnit), nodes).ToArray();
        var node = new Node();
        var single = Time(() => { for (var i = 0; i < nodes; i++) node.Execute(i, 0); }) / nodes;   // ns per node
        var estimate = single * Stopwatch.Frequency / 1e9;
        var workloads = new (string Label, Action<HeartbeatScheduler> Run, long Nodes)[]
        {
            ("levels: 300 of 1..3,000", hb => Levels(hb, wide, estimate), wide[^1]),
            ("levels: 3,000 of 1..200", hb => Levels(hb, narrow, estimate), narrow[^1]),
            ("flat 100 nodes", hb => hb.ForTicks(0, 100, node, estimate), 100),
            ("flat 1,000 nodes", hb => hb.ForTicks(0, 1_000, node, estimate), 1_000),
            ("flat 10,000 nodes", hb => hb.ForTicks(0, 10_000, node, estimate), 10_000),
            ("flat 100,000 nodes", hb => hb.ForTicks(0, 100_000, node, estimate), 100_000),
        };
        var workers = Math.Max(1, Environment.ProcessorCount - 1);
        var basis = new HeartbeatOptions { Workers = workers, IdlePolicy = HeartbeatIdlePolicy.BusySpin, SlowClockNanoseconds = slowClockNanoseconds };
        var clock = slowClockNanoseconds > 0 ? $"slowed to ~{slowClockNanoseconds:N0} ns a read" : "native";
        Console.WriteLine($"--- {Environment.ProcessorCount} cores, caller + {workers} workers, BusySpin, {basis.HeartbeatMicroseconds} µs heartbeat; clock {clock}; nodes {single:F0} ns");
        var results = new List<(string Name, double[] Speedups, double BeatUs)>();
        foreach (var name in new[] { "one beater" })
        {
            using var hb = new HeartbeatScheduler(basis);
            Thread.Sleep(20);
            var b0 = hb.Beats;
            var sw = Stopwatch.StartNew();
            Thread.Sleep(100);
            var beatUs = sw.Elapsed.TotalMicroseconds / Math.Max(1, hb.Beats - b0);
            var speedups = workloads.Select(w => single * w.Nodes / Time(() => w.Run(hb))).ToArray();
            results.Add((name, speedups, beatUs));
        }
        Console.WriteLine($"  {"",-26}" + string.Concat(results.Select(r => $"  {r.Name,25}")));
        for (var w = 0; w < workloads.Length; w++)
            Console.WriteLine($"  {workloads[w].Label,-26}" + string.Concat(results.Select(r => $"  {r.Speedups[w],24:F2}x")));
        Console.WriteLine($"  {"idle beat interval",-26}" + string.Concat(results.Select(r => $"  {r.BeatUs,22:F1} µs")));
    }

    static void Levels(HeartbeatScheduler hb, int[] offsets, double estimate)
    {
        for (var l = 0; l + 1 < offsets.Length; l++) hb.ForTicks(offsets[l], offsets[l + 1], new Node(), estimate);
    }

    static double Time(Action run)
    {
        Thread.Sleep(20);
        var warm = Stopwatch.StartNew();
        var calls = 0;
        while (warm.ElapsedMilliseconds < 300) { run(); calls++; }
        var reps = Math.Max(1, (int)(calls * 40.0 / warm.ElapsedMilliseconds));
        var best = double.MaxValue;
        for (var t = 0; t < 5; t++)
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < reps; i++) run();
            best = Math.Min(best, sw.Elapsed.TotalNanoseconds / reps);
        }
        return best;
    }
}
