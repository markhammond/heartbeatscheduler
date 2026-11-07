using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace HeartbeatScheduling.Tests;

public class SchedulerTests
{
    /// <summary>Bytes the calling thread allocates in <paramref name="body"/>, at steady state: after enough warm-up
    /// for tiered compilation to promote the scheduler's methods (its bookkeeping can be charged to this thread), the
    /// least over several trials.</summary>
    private static long SteadyAllocation(Action body, int warmup = 50, int trials = 5)
    {
        for (var i = 0; i < warmup; i++) body();
        var least = long.MaxValue;
        for (var t = 0; t < trials; t++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            body();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return least;
    }

    // No more workers than spare cores: with more spinning threads than cores, a worker holding a range waits out
    // other threads' time slices (on Windows, tens of milliseconds each), and timing-sensitive tests starve. A CI
    // runner may have two cores.
    private static readonly int SpareCores = Math.Max(1, Environment.ProcessorCount - 1);

    private static int Fit(int workers) => Math.Min(workers, SpareCores);

    private static readonly HeartbeatOptions Aggressive = new()
    {
        Workers = Fit(3), HeartbeatMicroseconds = 1, MinChunk = 1, SpinBeforeParkMicroseconds = 2000,
    };

    private static HeartbeatOptions AggressiveWith(HeartbeatIdlePolicy idle) => Aggressive with { IdlePolicy = idle };

    public static TheoryData<HeartbeatIdlePolicy> Policies => [HeartbeatIdlePolicy.SpinThenPark, HeartbeatIdlePolicy.BusySpin];

    public static TheoryData<int, HeartbeatIdlePolicy> SizesByPolicy()
    {
        var d = new TheoryData<int, HeartbeatIdlePolicy>();
        foreach (var idle in new[] { HeartbeatIdlePolicy.SpinThenPark, HeartbeatIdlePolicy.BusySpin })
            foreach (var size in new[] { 0, 1, 7, 64, 1_000, 200_000 }) d.Add(size, idle);
        return d;
    }

    private sealed class Recorder(int size, Func<int, int>? spin = null) : IHeartbeatLoop
    {
        public readonly int[] Count = new int[size];
        public readonly int[] Worker = new int[size];
        public readonly long[] Plain = new long[size];       // plain writes: visible after the join
        public int CallerDone, CallerDoneThread;

        public void Execute(int index, int worker)
        {
            Interlocked.Increment(ref Count[index]);
            Worker[index] = worker;
            Plain[index] = index * 3L + 1;
            var n = spin?.Invoke(index) ?? 0;
            for (var i = 0; i < n; i++) Thread.SpinWait(1);
        }

        public void CallerRangeDone()
        {
            CallerDone++;
            CallerDoneThread = Environment.CurrentManagedThreadId;
        }
    }

    [Theory]
    [MemberData(nameof(SizesByPolicy))]
    public void EveryIndex_RunsExactlyOnce_WritesVisibleAfterReturn(int size, HeartbeatIdlePolicy idle)
    {
        using var s = new HeartbeatScheduler(AggressiveWith(idle));
        var rnd = new Random(size);
        var costs = Enumerable.Range(0, size).Select(_ => rnd.Next(4) == 0 ? rnd.Next(50) : 0).ToArray();
        for (var round = 0; round < 5; round++)
        {
            var r = new Recorder(size, i => costs[i]);
            s.For(0, size, r);
            Assert.All(r.Count, c => Assert.Equal(1, c));
            Assert.All(r.Worker, w => Assert.InRange(w, 0, s.Workers));
            for (var i = 0; i < size; i++) Assert.Equal(i * 3L + 1, r.Plain[i]);
            Assert.Equal(1, r.CallerDone);
            Assert.Equal(Environment.CurrentManagedThreadId, r.CallerDoneThread);
        }
    }

    [Fact]
    public void LongLoops_Split_ShortLoops_DoNot()
    {
        using var s = new HeartbeatScheduler(Aggressive with { MinChunk = 8 });
        s.For(0, 15, new Recorder(15, _ => 200));       // below 2 x MinChunk: never split
        Assert.Equal(0, s.Splits);
        var beats = s.Beats;
        var r = new Recorder(50_000, _ => 5);
        var sw = Stopwatch.StartNew();
        s.For(0, 50_000, r);
        var state = $"{s.Splits} splits, workers used [{string.Join(", ", r.Worker.Distinct().Order())}], " +
                    $"{s.Beats - beats} beats in {sw.Elapsed.TotalMilliseconds:F2} ms, parked {s.Parked}, beater {s.Beater}, pending {s.Pending}";
        // At least one split and more than one thread: not more than three splits, as this once asked. Three is the least
        // that gives each of the three workers a range, and when the halving comes out even (half to one worker, then a
        // quarter each from the caller and that worker) all four threads stay busy to the end, so no thread is idle to
        // move the beat on and nothing more splits. That is ideal, and after a gap (parked workers waking together) it
        // happened about once in 250 runs.
        Assert.True(s.Splits > 0, state);
        Assert.True(r.Worker.Distinct().Count() > 1, state);
    }

    [Fact]
    public void Estimate_SplitsUpFront_WithoutWaitingForABeat()
    {
        using var s = new HeartbeatScheduler(Aggressive with { HeartbeatMicroseconds = 100_000 });   // beats effectively never
        Thread.Sleep(5);                                        // let workers reach their idle spin
        var microsecond = Stopwatch.Frequency / 1e6;
        // Claim 100 µs per iteration: 10,000 iterations predict 1 s, far above a 100 ms heartbeat, so the range is
        // split before the first iteration even though no beat will arrive during the loop.
        // Iterations really take a few µs, so workers have time to come online even on a loaded machine.
        s.ForTicks(0, 10_000, new Recorder(10_000, _ => 20), estimatedTicksPerIteration: 100 * microsecond);
        Assert.True(s.Splits > 0);
    }

    private sealed class Thrower(int at, Exception ex) : IHeartbeatLoop
    {
        public int Executed;

        public void Execute(int index, int worker)
        {
            Interlocked.Increment(ref Executed);
            if (index == at) throw ex;
            Thread.SpinWait(20);
        }
    }

    [Theory]
    [InlineData(0, HeartbeatIdlePolicy.SpinThenPark)]          // caller's share
    [InlineData(99_999, HeartbeatIdlePolicy.SpinThenPark)]     // likely a worker's share
    [InlineData(0, HeartbeatIdlePolicy.BusySpin)]
    [InlineData(99_999, HeartbeatIdlePolicy.BusySpin)]
    public void Exceptions_AreRethrownAfterTheJoin_WithTheirType_AndTheSchedulerRecovers(int at, HeartbeatIdlePolicy idle)
    {
        using var s = new HeartbeatScheduler(AggressiveWith(idle));
        var thrown = Assert.Throws<FormatException>(() => s.For(0, 100_000, new Thrower(at, new FormatException("bad row"))));
        Assert.Equal("bad row", thrown.Message);
        var r = new Recorder(10_000);
        s.For(0, 10_000, r);                                    // still fully usable
        Assert.All(r.Count, c => Assert.Equal(1, c));
    }

    private sealed class Nested(HeartbeatScheduler s) : IHeartbeatLoop
    {
        public void Execute(int index, int worker)
        {
            if (index == 0) s.For(0, 10, new Recorder(10));
        }
    }

#if DEBUG || HEARTBEAT_REENTRANCY_GUARD
    [Fact]
    public async Task NestedOrConcurrentLoops_AreRejected_WhenTheGuardIsCompiledIn()
    {
        using var s = new HeartbeatScheduler(Aggressive);
        var ex = Assert.Throws<InvalidOperationException>(() => s.For(0, 100, new Nested(s)));
        Assert.Contains("one loop at a time", ex.Message);

        using var gate = new ManualResetEventSlim();
        var slow = new Recorder(1, _ => { gate.Wait(); return 0; });
        var first = Task.Run(() => s.For(0, 1, slow));
        SpinWait.SpinUntil(() => slow.Count[0] == 1 || first.IsCompleted, 5000);
        Assert.Throws<InvalidOperationException>(() => s.For(0, 1, new Recorder(1)));
        gate.Set();
        await first;
    }
#endif

    [Fact]
    public void ParkedWorkers_WakeForTheNextLoop()
    {
        using var s = new HeartbeatScheduler(Aggressive with { SpinBeforeParkMicroseconds = 50 });
        Thread.Sleep(50);                                       // well past the spin window: workers are parked
        var r = new Recorder(100_000, _ => 3);
        s.For(0, 100_000, r);
        Assert.All(r.Count, c => Assert.Equal(1, c));
        Assert.True(r.Worker.Distinct().Count() > 1, "parked workers never joined");
    }

    [Fact]
    public void NoWorkers_IsAPlainLoop()
    {
        using var s = new HeartbeatScheduler(new HeartbeatOptions { Workers = 0 });
        var r = new Recorder(1000);
        s.For(0, 1000, r);
        Assert.All(r.Worker, w => Assert.Equal(0, w));
        Assert.Equal(1, r.CallerDone);
    }

    private sealed class Sum(int workers) : IHeartbeatLoop
    {
        public readonly long[] Partial = new long[(workers + 1) * 16];   // one cache line per worker
        public void Execute(int index, int worker) => Partial[worker * 16] += index;
        public long Total() { long t = 0; for (var i = 0; i < Partial.Length; i += 16) t += Partial[i]; return t; }
    }

    [Fact]
    public void PerWorkerState_NeedsNoSynchronization_AndLoopsAllocateNothing()
    {
        using var s = new HeartbeatScheduler(Aggressive);
        var sum = new Sum(s.Workers);
        const int N = 100_000;
        var loops = 0;
        Assert.Equal(0, SteadyAllocation(() => { s.For(0, N, sum); loops++; }));
        Assert.Equal((long)loops * N * (N - 1) / 2, sum.Total());
    }

    private sealed class BatchRecorder(int size) : IHeartbeatBatchLoop
    {
        public readonly int[] Count = new int[size];
        public int MaxBatch, CallerDone;

        public void Execute(int start, int end, int worker)
        {
            var n = end - start;
            int max;
            while (n > (max = Volatile.Read(ref MaxBatch)) && Interlocked.CompareExchange(ref MaxBatch, n, max) != max) { }
            for (var i = start; i < end; i++) Interlocked.Increment(ref Count[i]);
            Thread.SpinWait(n);
        }

        public void CallerRangeDone() => CallerDone++;
    }

    public static TheoryData<int, int, HeartbeatIdlePolicy> BatchCases()
    {
        var d = new TheoryData<int, int, HeartbeatIdlePolicy>();
        foreach (var idle in new[] { HeartbeatIdlePolicy.SpinThenPark, HeartbeatIdlePolicy.BusySpin })
            foreach (var (size, batch) in new[] { (0, 64), (1, 64), (63, 64), (1_000, 1), (1_000, 7), (250_000, 64), (250_000, 1000) })
                d.Add(size, batch, idle);
        return d;
    }

    [Theory]
    [MemberData(nameof(BatchCases))]
    public void Batches_CoverEveryIndexExactlyOnce_NeverExceedingTheBatchSize(int size, int batch, HeartbeatIdlePolicy idle)
    {
        using var s = new HeartbeatScheduler(AggressiveWith(idle));
        for (var round = 0; round < 3; round++)
        {
            var r = new BatchRecorder(size);
            s.For(0, size, r, batch);
            Assert.All(r.Count, c => Assert.Equal(1, c));
            Assert.InRange(r.MaxBatch, 0, batch);
            Assert.Equal(1, r.CallerDone);
        }
        if (size >= 250_000) Assert.True(s.Splits > 0);
    }

    private sealed class Offset(int start, int size) : IHeartbeatLoop
    {
        public readonly int[] Count = new int[size];
        public void Execute(int index, int worker)
        {
            Interlocked.Increment(ref Count[index - start]);
            for (var k = 0; k < 200; k++) Thread.SpinWait(1);
        }
    }

    [Theory]
    [InlineData(-2, 0, HeartbeatIdlePolicy.SpinThenPark)]   // the split [-1, 0) once encoded as the empty mailbox: a join that never ended
    [InlineData(-1_000, 0, HeartbeatIdlePolicy.SpinThenPark)]
    [InlineData(-500, 500, HeartbeatIdlePolicy.SpinThenPark)]
    [InlineData(int.MinValue, int.MinValue + 300, HeartbeatIdlePolicy.SpinThenPark)]
    [InlineData(int.MaxValue - 300, int.MaxValue, HeartbeatIdlePolicy.SpinThenPark)]
    [InlineData(-2, 0, HeartbeatIdlePolicy.BusySpin)]
    [InlineData(-500, 500, HeartbeatIdlePolicy.BusySpin)]
    [InlineData(int.MaxValue - 300, int.MaxValue, HeartbeatIdlePolicy.BusySpin)]
    public async Task AnyRange_Splits_AndEveryIndexRunsOnce(int start, int end, HeartbeatIdlePolicy idle)
    {
        var s = new HeartbeatScheduler(AggressiveWith(idle));
        var run = Task.Run(() =>
        {
            try
            {
                // At least 200 rounds, and on until a split has happened (the splits are what exercise the hand-offs this
                // guards, and on a loaded machine the workers may be descheduled through the first few hundred), for at
                // most 20 seconds.
                var sw = Stopwatch.StartNew();
                for (var round = 0; (round < 200 || s.Splits == 0) && sw.Elapsed < TimeSpan.FromSeconds(20); round++)
                {
                    var r = new Offset(start, end - start);
                    s.ForTicks(start, end, r, estimatedTicksPerIteration: 1e9);   // eager: split whenever a worker is idle
                    Assert.All(r.Count, c => Assert.Equal(1, c));
                }
            }
            finally
            {
                s.Dispose();                             // here, never under a running loop: a timeout leaves none spinning
            }
        });
        await run.WaitAsync(TimeSpan.FromSeconds(60));   // a TimeoutException: a loop never returned
        Assert.True(s.Splits > 0, "no split in 20 seconds");
    }

    private sealed class Spans : IHeartbeatBatchLoop
    {
        public readonly List<(int Start, int End)> All = [];
        public void Execute(int start, int end, int worker)
        {
            lock (All) All.Add((start, end));
            Thread.Sleep(1);                      // long enough for idle workers to beat and take the rest
        }
    }

    [Theory]
    [InlineData(0, int.MaxValue - 5, int.MaxValue, 4)]
    [InlineData(3, int.MaxValue - 5, int.MaxValue, 4)]
    [InlineData(0, int.MinValue, int.MaxValue, 1 << 27)]   // longer than int.MaxValue
    [InlineData(3, int.MinValue, int.MaxValue, 1 << 27)]
    public async Task Batches_NearTheEndsOfInt_CoverTheRangeOnce_WithinTheBatchSize(int workers, int start, int end, int batch)
    {
        var s = new HeartbeatScheduler(Aggressive with { Workers = Fit(workers) });
        var spans = new Spans();
        var run = Task.Run(() =>
        {
            try { s.For(start, end, spans, batch); }
            finally { s.Dispose(); }                     // never under a running loop: a timeout leaves none spinning
        });
        await run.WaitAsync(TimeSpan.FromSeconds(60));   // a TimeoutException: the loop never returned
        var sorted = spans.All.OrderBy(b => b.Start).ToArray();
        Assert.Equal(start, sorted[0].Start);
        Assert.Equal(end, sorted[^1].End);
        for (var k = 0; k < sorted.Length; k++)
        {
            Assert.InRange((long)sorted[k].End - sorted[k].Start, 1, batch);
            if (k > 0) Assert.Equal(sorted[k - 1].End, sorted[k].Start);   // contiguous: no gap, no overlap
        }
    }

    private sealed class Busy : IHeartbeatLoop
    {
        public void Execute(int index, int worker) => Thread.SpinWait(20);
    }

    private sealed class Nothing : IHeartbeatLoop
    {
        public int Workers;
        public void Execute(int index, int worker) => Workers |= 1 << worker;
    }

    [Fact]
    public void ALoopShorterThanAHeartbeat_RunsSequentially_EvenRightAfterALongOne()
    {
        // The beat a thread last saw once carried over from the previous loop, so a short loop could split at once
        // (82 of 200 such loops did, at these settings).
        using var s = new HeartbeatScheduler(Aggressive with { HeartbeatMicroseconds = 500, MinChunk = 1 });
        for (var round = 0; round < 100; round++)
        {
            s.For(0, 20_000, new Busy());                   // many heartbeats: idle workers move the beat on
            var before = s.Splits;
            var tiny = new Nothing();
            s.For(0, 4, tiny);                              // four empty iterations: far shorter than a heartbeat
            Assert.Equal(before, s.Splits);
            Assert.Equal(1, tiny.Workers);                  // the calling thread (worker 0) alone
        }
    }

    // ------------------------------------------------------------------------------------- the calling thread joins in

    private sealed class LaneCost(int size) : IHeartbeatLoop
    {
        public readonly int[] Worker = new int[size];
        public int CallerDone, AfterDone;

        public void Execute(int index, int worker)
        {
            Worker[index] = worker;
            if (worker == 0 && CallerDone == 1) AfterDone++;
            Thread.SpinWait(worker == 0 ? 2 : 400);              // the calling thread is far faster than its worker
        }

        public void CallerRangeDone() => CallerDone++;
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void TheCallingThread_TakesWorkFromBusyWorkers_OnceItsOwnShareIsDone(HeartbeatIdlePolicy idle)
    {
        // One worker, some 200 times slower per iteration: the caller's share is done long before the worker's, and the
        // caller then takes most of the rest. It once spun at the join instead, having run only the half it kept at
        // the first split, on every try. The loop lasts about a millisecond, and a loaded machine may deschedule the
        // worker for all of it (the caller then runs the loop alone, never past its own share), so it gets a few tries.
        using var s = new HeartbeatScheduler(AggressiveWith(idle) with { Workers = 1 });
        s.For(0, 50_000, new Recorder(50_000, _ => 5));          // the worker is up and spinning, not still starting
        const int N = 20_000;
        var tries = new List<string>();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var r = new LaneCost(N);
            s.For(0, N, r);
            Assert.Equal(1, r.CallerDone);
            var byCaller = r.Worker.Count(w => w == 0);
            if (byCaller > N * 3 / 4 && r.AfterDone > 0) return;
            tries.Add($"{byCaller:N0} by the calling thread, {r.AfterDone:N0} of them after CallerRangeDone");
        }
        Assert.Fail($"the calling thread never took most of the worker's share after its own, in ten tries of {N:N0} iterations: {string.Join("; ", tries)}");
    }

    // ------------------------------------------------------------------------------------- layout

    private static void Isolated<T>(string field, int fieldSize) where T : struct
    {
        int line = HeartbeatScheduler.CacheLine, size = Unsafe.SizeOf<T>(), offset = (int)Marshal.OffsetOf<T>(field);
        Assert.True(offset >= line, $"{typeof(T).Name}.{field} has {offset} bytes before it");
        Assert.True(size - offset - fieldSize >= line, $"{typeof(T).Name}.{field} has {size - offset - fieldSize} bytes after it");
    }

    [Fact]
    public void EachLane_AndTheBeat_HaveAFullCacheLineOfPaddingOnEachSide()
    {
        Isolated<HeartbeatScheduler.Lane>(nameof(HeartbeatScheduler.Lane.Mailbox), 8);
        Isolated<HeartbeatScheduler.Lane>(nameof(HeartbeatScheduler.Lane.Idle), 4);
        Isolated<HeartbeatScheduler.Clock>(nameof(HeartbeatScheduler.Clock.Beat), 8);
        Isolated<HeartbeatScheduler.Clock>(nameof(HeartbeatScheduler.Clock.LastBeat), 8);
        Isolated<HeartbeatScheduler.SplitCount>(nameof(HeartbeatScheduler.SplitCount.Value), 8);
    }

    // ------------------------------------------------------------------------------------- the busy mailbox

    [Fact]
    public void ALaneRunningARange_TakesNoOffer_UntilItFinishes()
    {
        using var s = new HeartbeatScheduler(new HeartbeatOptions { Workers = 0 });   // lane 0 alone: no thread races us
        s.SetIdle(0, true);
        Assert.True(s.TryOffer(0, 10, 20));
        Assert.True(s.TryTake(0, out var lo, out var hi));
        Assert.Equal((10, 20), (lo, hi));
        // An offerer could once see a lane that had just claimed a range as still idle; its offer then waited in the
        // mailbox behind the range the lane was running. A claimed mailbox stays busy, so the offer goes elsewhere.
        s.SetIdle(0, true);
        Assert.False(s.TryOffer(0, 30, 40));
        s.Finish(0);
        Assert.True(s.TryOffer(0, 30, 40));                       // open again once the range is done
        Assert.True(s.TryTake(0, out lo, out hi));
        Assert.Equal((30, 40), (lo, hi));
        s.Finish(0);
        s.SetIdle(0, false);
        Assert.Equal(0, s.Pending);
    }

    // ------------------------------------------------------------------------------------- disposal

    [Fact]
    public void ForAfterDispose_Throws()
    {
        var s = new HeartbeatScheduler(Aggressive);
        s.For(0, 1_000, new Recorder(1_000));
        s.Dispose();
        s.Dispose();                                              // idempotent
        Assert.Throws<ObjectDisposedException>(() => s.For(0, 1_000, new Recorder(1_000)));
        Assert.Throws<ObjectDisposedException>(() => s.For(0, 1_000, new BatchRecorder(1_000), 8));
        Assert.Throws<ObjectDisposedException>(() => s.For(0, 1_000, new StructRecorder(new int[1_000], new int[1_000])));
        Assert.Throws<ObjectDisposedException>(() => s.For(0, 0, new Recorder(0)));   // even an empty range
    }

    // ------------------------------------------------------------------------------------- struct bodies

    private struct StructRecorder(int[] count, int[] worker) : IHeartbeatLoop
    {
        public void Execute(int index, int w)
        {
            Interlocked.Increment(ref count[index]);
            worker[index] = w;
        }
    }

    private struct StructBatchRecorder(int[] count, int[] maxBatch) : IHeartbeatBatchLoop
    {
        public void Execute(int start, int end, int worker)
        {
            int n = end - start, max;
            while (n > (max = Volatile.Read(ref maxBatch[0])) && Interlocked.CompareExchange(ref maxBatch[0], n, max) != max) { }
            for (var i = start; i < end; i++) Interlocked.Increment(ref count[i]);
            Thread.SpinWait(n);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1_000)]
    [InlineData(200_000)]
    public void StructBodies_RunEveryIndexExactlyOnce(int size)
    {
        using var s = new HeartbeatScheduler(Aggressive);
        for (var round = 0; round < 3; round++)
        {
            int[] count = new int[size], worker = new int[size];
            s.For(0, size, new StructRecorder(count, worker));
            Assert.All(count, c => Assert.Equal(1, c));
            Assert.All(worker, w => Assert.InRange(w, 0, s.Workers));

            int[] batchCount = new int[size], maxBatch = new int[1];
            s.For(0, size, new StructBatchRecorder(batchCount, maxBatch), 64);
            Assert.All(batchCount, c => Assert.Equal(1, c));
            Assert.InRange(maxBatch[0], 0, 64);
        }
    }

    private struct StructSum(long[] partial) : IHeartbeatLoop
    {
        public void Execute(int index, int worker) => partial[worker * 16] += index;
    }

    private struct StructBatchSum(long[] partial) : IHeartbeatBatchLoop
    {
        public void Execute(int start, int end, int worker)
        {
            long sum = 0;
            for (var i = start; i < end; i++) sum += i;
            partial[worker * 16] += sum;
        }
    }

    private static long Total(long[] partial)
    {
        long t = 0;
        for (var i = 0; i < partial.Length; i += 16) t += partial[i];
        return t;
    }

    [Fact]
    public void StructBodies_AllocateNothing_TheDefaultCallerRangeDoneIncluded()
    {
        // A struct passed to the interface overload would box on every call: zero bytes also shows the generic
        // overload was chosen. Neither body implements CallerRangeDone, so the interface's default runs through a struct.
        using var s = new HeartbeatScheduler(Aggressive);
        long[] items = new long[(s.Workers + 1) * 16], batches = new long[(s.Workers + 1) * 16];
        const int N = 100_000;
        var loops = 0;
        Assert.Equal(0, SteadyAllocation(() =>                    // a scheduler's first loop of a type allocates
        {
            s.For(0, N, new StructSum(items));
            s.For(0, N, new StructBatchSum(batches), 64);
            loops++;
        }));
        Assert.Equal((long)loops * N * (N - 1) / 2, Total(items));
        Assert.Equal((long)loops * N * (N - 1) / 2, Total(batches));
    }

    private struct CallerDoneCounter(int[] calls) : IHeartbeatLoop
    {
        public void Execute(int index, int worker) => Thread.SpinWait(5);
        public void CallerRangeDone()
        {
            calls[0]++;
            calls[1] = Environment.CurrentManagedThreadId;
        }
    }

    [Fact]
    public void AStructBody_CallerRangeDone_RunsOnceOnTheCallingThread()
    {
        using var s = new HeartbeatScheduler(Aggressive);
        var calls = new int[2];
        s.For(0, 50_000, new CallerDoneCounter(calls));
        Assert.Equal(1, calls[0]);
        Assert.Equal(Environment.CurrentManagedThreadId, calls[1]);
    }

    // ------------------------------------------------------------------------------------- failures stop the loop

    private sealed class FailOnAWorker : IHeartbeatLoop
    {
        public int Executed, Thrown, CallerDone;

        public void Execute(int index, int worker)
        {
            if (worker != 0 && Interlocked.Exchange(ref Thrown, 1) == 0) throw new FormatException("bad row");
            Interlocked.Increment(ref Executed);
            Thread.SpinWait(50);
        }

        public void CallerRangeDone() => CallerDone++;
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void AfterAFailure_TheOtherThreads_AbandonTheirRanges(HeartbeatIdlePolicy idle)
    {
        // A worker's first iteration throws. The calling thread once went on to finish the half it kept, some 50,000
        // iterations; it now stops at its next beat, and CallerRangeDone does not run for a failed loop.
        using var s = new HeartbeatScheduler(AggressiveWith(idle));
        const int N = 100_000;
        var body = new FailOnAWorker();
        Assert.Throws<FormatException>(() => s.For(0, N, body));
        Assert.True(body.Executed < N / 10, $"{body.Executed:N0} iterations ran");
        Assert.Equal(0, body.CallerDone);
        var r = new Recorder(10_000);
        s.For(0, 10_000, r);                                      // still fully usable
        Assert.All(r.Count, c => Assert.Equal(1, c));
    }

    // ------------------------------------------------------------------------------------- idle policy

    [Fact]
    public void TheBusySpinPolicy_NeverParks_TheDefaultDoes()
    {
        var shortSpin = Aggressive with { SpinBeforeParkMicroseconds = 50 };
        using var spin = new HeartbeatScheduler(shortSpin with { IdlePolicy = HeartbeatIdlePolicy.BusySpin });
        using var park = new HeartbeatScheduler(shortSpin);
        spin.For(0, 10_000, new Recorder(10_000, _ => 3));
        park.For(0, 10_000, new Recorder(10_000, _ => 3));
        Thread.Sleep(50);                                         // far past the 50 µs spin window
        Assert.Equal(0, spin.Parked);
        Assert.True(SpinWait.SpinUntil(() => park.Parked == park.Workers, 5_000), $"{park.Parked} of {park.Workers} parked");
        var r = new Recorder(100_000, _ => 3);
        spin.For(0, 100_000, r);                                  // still a working scheduler
        Assert.All(r.Count, c => Assert.Equal(1, c));
        Assert.True(r.Worker.Distinct().Count() > 1, "spinning workers never joined");
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(1e6 + 1)]                        // beyond a second: the tick conversion heads for saturation
    [InlineData(1e30)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void AHeartbeatOutOfRange_IsRejected(double microseconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new HeartbeatScheduler(new HeartbeatOptions { HeartbeatMicroseconds = microseconds }));

    [Theory]
    [InlineData(-5)]
    [InlineData(6e7 + 1)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void ASpinBeforeParkOutOfRange_IsRejected(double microseconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new HeartbeatScheduler(new HeartbeatOptions { SpinBeforeParkMicroseconds = microseconds }));

    [Fact]
    public void TheBoundsThemselves_AreAccepted()
    {
        using (new HeartbeatScheduler(new HeartbeatOptions { Workers = 1, HeartbeatMicroseconds = 0.1, SpinBeforeParkMicroseconds = 0 })) { }
        using (new HeartbeatScheduler(new HeartbeatOptions { Workers = 1, HeartbeatMicroseconds = 1e6, SpinBeforeParkMicroseconds = 6e7 })) { }
    }

    [Fact]
    public void AnUndefinedIdlePolicy_IsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new HeartbeatScheduler(new HeartbeatOptions { IdlePolicy = (HeartbeatIdlePolicy)7 }));

    // ------------------------------------------------------------------------------------- spinning workers, the estimate

    [Fact]
    public void UnderBusySpin_ALoopShorterThanAHeartbeat_RarelySplits_AndNeverMoreThanOncePerBeat()
    {
        // Spinning workers beat between loops too, so a loop starts on the current beat, not a fresh one: a beat may land
        // inside a short loop. Each range starts on the beat current when it starts, so the thread that takes the
        // split half does not split again on the same beat.
        using var s = new HeartbeatScheduler(AggressiveWith(HeartbeatIdlePolicy.BusySpin) with { HeartbeatMicroseconds = 500 });
        long most = 0;
        for (var round = 0; round < 200; round++)
        {
            var before = s.Splits;
            s.For(0, 4, new Nothing());                        // four empty iterations: far shorter than a heartbeat
            most = Math.Max(most, s.Splits - before);
        }
        Assert.True(most <= 1, $"a short loop split {most} times");
        Assert.True(s.Splits <= 10, $"{s.Splits} splits in 200 loops of four empty iterations");
    }

    [Fact]
    public void AnEstimate_BelowAHeartbeat_SplitsNothingUpFront()
    {
        using var s = new HeartbeatScheduler(Aggressive with { HeartbeatMicroseconds = 100_000 });   // beats effectively never
        Thread.Sleep(5);                                        // let workers reach their idle spin
        var microsecond = Stopwatch.Frequency / 1e6;
        // 10,000 iterations at a claimed 1 µs: 10 ms, under a 100 ms heartbeat. (At 100 µs each it splits at once.)
        s.ForTicks(0, 10_000, new Recorder(10_000, _ => 20), estimatedTicksPerIteration: microsecond);
        Assert.Equal(0, s.Splits);
    }

    [Fact]
    public void TheDefaultHeartbeat_IsFiveMicroseconds() => Assert.Equal(5, new HeartbeatOptions().HeartbeatMicroseconds);

    // ------------------------------------------------------------------------------------- the bypass, TimeSpan estimates

    [Theory]
    [MemberData(nameof(Policies))]
    public void ALoopEstimatedBelowAHeartbeat_RunsPlainOnTheCallingThread(HeartbeatIdlePolicy idle)
    {
        // A 1 µs heartbeat and idle workers: these loops of a few milliseconds would split many times. Told they take
        // half a heartbeat, the scheduler trusts the host: a plain loop on the calling thread, no worker involved.
        using var s = new HeartbeatScheduler(AggressiveWith(idle));
        const int N = 1_000;
        var halfAHeartbeat = Stopwatch.Frequency / 1e6 * s.Options.HeartbeatMicroseconds / 2 / N;
        for (var round = 0; round < 20; round++)
        {
            var r = new Recorder(N, _ => 20);
            s.ForTicks(0, N, r, halfAHeartbeat);
            Assert.All(r.Count, c => Assert.Equal(1, c));
            Assert.All(r.Worker, w => Assert.Equal(0, w));
            Assert.Equal(1, r.CallerDone);
        }
        Assert.Equal(0, s.Splits);
        // No estimate, no bypass. Twenty times longer than the loops above: bypassed loops never wake the workers, so with
        // SpinThenPark they have parked by now, and a loop as short as those (~1 ms) could finish before they woke.
        var unknown = new Recorder(20 * N, _ => 20);
        s.For(0, 20 * N, unknown);
        Assert.True(s.Splits > 0, "without an estimate the loop should have split");
    }

    [Fact]
    public void TimeSpanEstimates_MatchTheirTicks()
    {
        using var s = new HeartbeatScheduler(Aggressive with { HeartbeatMicroseconds = 100_000 });   // beats effectively never
        Thread.Sleep(5);
        s.For(0, 10_000, new Recorder(10_000, _ => 20), TimeSpan.FromMicroseconds(1));     // 10 ms: under a heartbeat
        Assert.Equal(0, s.Splits);
        s.For(0, 10_000, new Recorder(10_000, _ => 20), TimeSpan.FromMicroseconds(100));   // 1 s: split up front
        Assert.True(s.Splits > 0);

        int[] count = new int[1_000], worker = new int[1_000], batchCount = new int[1_000], maxBatch = new int[1];
        s.For(0, 1_000, new StructRecorder(count, worker), TimeSpan.FromMicroseconds(1));
        s.For(0, 1_000, new StructBatchRecorder(batchCount, maxBatch), 16, TimeSpan.FromMicroseconds(1));
        s.For(0, 1_000, new BatchRecorder(1_000), 16, TimeSpan.FromMicroseconds(1));
        Assert.All(count, c => Assert.Equal(1, c));
        Assert.All(batchCount, c => Assert.Equal(1, c));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.For(0, 10, new Recorder(10), TimeSpan.FromTicks(-1)));
    }

    // ------------------------------------------------------------------------------------- slow clocks

    [Theory]
    [MemberData(nameof(Policies))]
    public void WithASlowClock_LoopsStillSplit_AndRunEveryIndexOnce(HeartbeatIdlePolicy idle)
    {
        var options = AggressiveWith(idle) with { HeartbeatMicroseconds = 20, SlowClockNanoseconds = 1_000 };
        using var s = new HeartbeatScheduler(options);
        Assert.InRange(s.ClockNanoseconds, 900, 5_000);           // the test mode really slows the clock
        for (var round = 0; round < 5; round++)
        {
            var r = new Recorder(50_000, _ => 5);
            s.For(0, 50_000, r);
            Assert.All(r.Count, c => Assert.Equal(1, c));
            var b = new BatchRecorder(50_000);
            s.For(0, 50_000, b, 16);
            Assert.All(b.Count, c => Assert.Equal(1, c));
        }
        Assert.True(s.Splits > 0, "no beat moved the loops on");
    }

    [Fact]
    public void Beats_KeepComing_ButNeverFasterThanTheHeartbeat()
    {
        // Spinning workers beat between loops too: a beat may come late, never early.
        var options = new HeartbeatOptions { Workers = Fit(2), IdlePolicy = HeartbeatIdlePolicy.BusySpin, HeartbeatMicroseconds = 50 };
        using var s = new HeartbeatScheduler(options);
        Thread.Sleep(20);
        var before = s.Beats;
        var sw = Stopwatch.StartNew();
        Thread.Sleep(100);
        var beats = s.Beats - before;
        var most = sw.Elapsed.TotalMicroseconds / options.HeartbeatMicroseconds + 2;
        Assert.True(beats > 0, "no beats");
        Assert.True(beats <= most, $"{beats} beats in {sw.Elapsed.TotalMilliseconds:F0} ms: more than one a heartbeat");
    }

    // ------------------------------------------------------------------------------------- the single beater

    [Fact]
    public void OneIdleThread_KeepsTheBeat_ForAll()
    {
        using var s = new HeartbeatScheduler(new HeartbeatOptions { Workers = Fit(4), IdlePolicy = HeartbeatIdlePolicy.BusySpin, HeartbeatMicroseconds = 20 });
        Thread.Sleep(20);
        Assert.InRange(s.Beater, 1, s.Workers);                           // a worker holds the role
        var before = s.Beats;
        Thread.Sleep(50);
        Assert.True(s.Beats > before, "the beater kept no beat");
        var r = new Recorder(100_000, _ => 3);                    // and loops split on it
        s.For(0, 100_000, r);
        Assert.All(r.Count, c => Assert.Equal(1, c));
        Assert.True(s.Splits > 0);
    }

    [Fact]
    public void AStalledBeater_IsReplaced()
    {
        // The role handed to a lane that never beats (as if its thread were descheduled): after about four heartbeats
        // without a beat, a watcher takes it over.
        using var s = new HeartbeatScheduler(new HeartbeatOptions { Workers = Fit(3), IdlePolicy = HeartbeatIdlePolicy.BusySpin, HeartbeatMicroseconds = 20 });
        Thread.Sleep(20);
        s.SetBeater(99);
        var before = s.Beats;
        Assert.True(SpinWait.SpinUntil(() => s.Beats > before + 10, 5_000), "the beat never resumed");
        Assert.InRange(s.Beater, 1, s.Workers);
    }

    // ------------------------------------------------------------------------------------- the work-based split guard

    private sealed class Slow(int size, double microseconds) : IHeartbeatLoop
    {
        public readonly int[] Count = new int[size], Worker = new int[size];

        public void Execute(int index, int worker)
        {
            Interlocked.Increment(ref Count[index]);
            Worker[index] = worker;
            var start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start).TotalMicroseconds < microseconds) { }
        }
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void GivenAnEstimate_AFewLongIterations_Split_ThoughFewerThanTwoMinChunks(HeartbeatIdlePolicy idle)
    {
        // Twelve iterations of ~100 µs: without an estimate, MinChunk (8) forbids any split (12 < 2 x 8), however long
        // they take. Told the cost, the scheduler splits by work: each half keeps far more than a quarter heartbeat.
        // A loaded machine may deschedule every worker for the loop's 1.2 ms, so the estimated loop gets a few tries.
        using var s = new HeartbeatScheduler(new HeartbeatOptions { Workers = Fit(3), IdlePolicy = idle, HeartbeatMicroseconds = 5 });
        s.For(0, 1_000, new Recorder(1_000, _ => 20));          // the workers are up
        var before = s.Splits;
        var blind = new Slow(12, 100);
        s.For(0, 12, blind);
        Assert.Equal(before, s.Splits);
        Assert.All(blind.Worker, w => Assert.Equal(0, w));

        Slow told = null!;
        for (var attempt = 0; attempt < 10 && s.Splits == before; attempt++)
        {
            told = new Slow(12, 100);
            s.For(0, 12, told, TimeSpan.FromMicroseconds(100));
            Assert.All(told.Count, c => Assert.Equal(1, c));
        }
        Assert.True(s.Splits > before, "twelve 100 µs iterations never split, in ten tries");
        Assert.True(told.Worker.Distinct().Count() > 1, "only the calling thread worked");
    }

    // ------------------------------------------------------------------------------------- thread quality

    private const uint QosUserInteractive = 0x21;

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern uint qos_class_self();

    [StructLayout(LayoutKind.Sequential)]
    private struct SchedParam
    {
        public int Priority;
        public int Opaque;
    }

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern nint pthread_self();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int pthread_getschedparam(nint thread, out int policy, ref SchedParam param);

    /// <summary>macOS: whether the calling thread is in the user-interactive band, by its quality of service or, for a
    /// thread opted out of it, its priority.</summary>
    private static bool UserInteractive()
    {
        if (qos_class_self() == QosUserInteractive) return true;
        var p = new SchedParam();
        return pthread_getschedparam(pthread_self(), out _, ref p) == 0 && p.Priority == 47;
    }

    private sealed class QosRecorder(int size) : IHeartbeatLoop
    {
        public readonly bool[] Interactive = new bool[size];
        public readonly int[] Worker = new int[size];

        public void Execute(int index, int worker)
        {
            Worker[index] = worker;
            if (OperatingSystem.IsMacOS()) Interactive[index] = UserInteractive();
            Thread.SpinWait(20);
        }
    }

    [Fact]
    public void SetCurrentQuality_RaisesTheCallingThread_WhereTheSystemAllows()
    {
        bool applied = false, interactive = false;
        var t = new Thread(() =>                                  // a thread of our own: the test runner's stays as it was
        {
            applied = HeartbeatThreads.SetCurrentQuality(HeartbeatThreadQuality.High);
            if (OperatingSystem.IsMacOS()) interactive = UserInteractive();
        });
        t.Start();
        t.Join();
        Assert.Equal(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), applied);
        if (OperatingSystem.IsMacOS()) Assert.True(interactive, "the thread is not in the user-interactive band");
        Assert.Throws<ArgumentOutOfRangeException>(() => HeartbeatThreads.SetCurrentQuality((HeartbeatThreadQuality)5));
    }

    // ------------------------------------------------------------------------------------- the host's threads

    /// <summary>A minimal event loop: one thread running posted actions in order.</summary>
    private sealed class EventLoop : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = [];
        private readonly Thread _thread;

        public EventLoop(string name)
        {
            _thread = new Thread(() => { foreach (var a in _queue.GetConsumingEnumerable()) a(); }) { Name = name, IsBackground = true };
            _thread.Start();
        }

        public void Post(Action action) => _queue.Add(action);

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
        }
    }

    [ThreadStatic] private static string? t_setUp;

    private sealed class ThreadRecorder(int size) : IHeartbeatLoop
    {
        public readonly string?[] Name = new string?[size], SetUp = new string?[size];
        public readonly bool[] Interactive = new bool[size];
        public readonly int[] Worker = new int[size], Count = new int[size];

        public void Execute(int index, int worker)
        {
            Interlocked.Increment(ref Count[index]);
            Worker[index] = worker;
            Name[index] = Thread.CurrentThread.Name;
            SetUp[index] = t_setUp;
            if (OperatingSystem.IsMacOS()) Interactive[index] = UserInteractive();
            Thread.SpinWait(20);
        }
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void Workers_RunOnTheHostsEventLoops_SetUpAsTheHostWishes(HeartbeatIdlePolicy idle)
    {
        var loops = Enumerable.Range(1, 3).Select(i => new EventLoop($"host-{i}")).ToArray();
        try
        {
            foreach (var (loop, i) in loops.Select((l, i) => (l, i)))
                loop.Post(() =>                                       // the host's own one-off setup task, first
                {
                    t_setUp = $"set up {i + 1}";
                    HeartbeatThreads.SetCurrentQuality(HeartbeatThreadQuality.High);
                });
            using (var s = new HeartbeatScheduler(AggressiveWith(idle) with
                   {
                       Workers = loops.Length,
                       StartWorker = (lane, run) => loops[lane - 1].Post(run),
                   }))
            {
                var r = new ThreadRecorder(100_000);
                s.For(0, 100_000, r);
                Assert.All(r.Count, c => Assert.Equal(1, c));
                var byWorkers = Enumerable.Range(0, r.Worker.Length).Where(i => r.Worker[i] != 0).ToArray();
                Assert.NotEmpty(byWorkers);
                foreach (var i in byWorkers)
                {
                    Assert.Equal($"host-{r.Worker[i]}", r.Name[i]);     // worker n runs on the loop it was posted to
                    Assert.Equal($"set up {r.Worker[i]}", r.SetUp[i]);  // after the host's setup task
                    if (OperatingSystem.IsMacOS()) Assert.True(r.Interactive[i], $"worker {r.Worker[i]} not in the user-interactive band");
                }
            }
            // Disposed: every worker loop has returned, so each event loop is free to run the next task at once.
            foreach (var loop in loops)
            {
                using var ran = new ManualResetEventSlim();
                loop.Post(ran.Set);
                Assert.True(ran.Wait(5_000), "a worker loop still held its event loop after Dispose");
            }
        }
        finally
        {
            foreach (var loop in loops) loop.Dispose();
        }
    }

    [Fact]
    public void Dispose_WaitsForEveryWorkerLoopToReturn()
    {
        var returned = new int[4];
        var threads = new List<Thread>();
        var s = new HeartbeatScheduler(Aggressive with
        {
            Workers = 3,
            StartWorker = (lane, run) =>
            {
                var t = new Thread(() => { run(); Volatile.Write(ref returned[lane], 1); });
                threads.Add(t);
                t.Start();
            },
        });
        s.For(0, 10_000, new Recorder(10_000, _ => 3));
        s.Dispose();
        foreach (var t in threads) t.Join(5_000);
        Assert.All(returned.Skip(1), r => Assert.Equal(1, r));
    }

    [Fact]
    public void AThrowingStarter_FailsTheConstructor_WithoutLeakingTheWorkersStarted()
    {
        var threads = new List<Thread>();
        var ex = Assert.Throws<NotSupportedException>(() => new HeartbeatScheduler(Aggressive with
        {
            Workers = 3,
            StartWorker = (lane, run) =>
            {
                if (lane == 3) throw new NotSupportedException("no third thread");
                var t = new Thread(() => run());
                threads.Add(t);
                t.Start();
            },
        }));
        Assert.Equal("no third thread", ex.Message);
        Assert.All(threads, t => Assert.True(t.Join(5_000), "a started worker kept running after the constructor threw"));
    }

    [Fact]
    public void AWorkerLoop_RunsOnce_NeverStarts_OrStartsLate_Safely()
    {
        Action? first = null, unstarted = null;
        var s = new HeartbeatScheduler(Aggressive with
        {
            Workers = 3,
            StartWorker = (lane, run) =>
            {
                if (lane == 1) { first = run; new Thread(() => run()).Start(); }
                else if (lane == 2) unstarted = run;                  // never run until after Dispose
                else new Thread(() => run()).Start();
            },
        });
        var r = new Recorder(50_000, _ => 3);
        s.For(0, 50_000, r);                                          // two of three workers: still a working scheduler
        Assert.All(r.Count, c => Assert.Equal(1, c));
        Assert.DoesNotContain(2, r.Worker);
        Assert.Throws<InvalidOperationException>(() => first!());     // lane 1's loop already ran
        s.Dispose();
        unstarted!();                                                 // after Dispose: returns at once, touches nothing
    }

    [Fact]
    public void DefaultWorkers_RunOnNamedBackgroundThreads()
    {
        using var s = new HeartbeatScheduler(Aggressive);
        var r = new ThreadRecorder(100_000);
        s.For(0, 100_000, r);
        var byWorkers = Enumerable.Range(0, r.Worker.Length).Where(i => r.Worker[i] != 0).ToArray();
        Assert.NotEmpty(byWorkers);
        Assert.All(byWorkers, i => Assert.Equal($"Heartbeat-{r.Worker[i]}", r.Name[i]));
    }

    [Fact]
    public void BackgroundThreads_TakeTheirNameAndQuality()
    {
        using var s = new HeartbeatScheduler(Aggressive with { StartWorker = HeartbeatThreads.Background("custom", HeartbeatThreadQuality.High) });
        var r = new ThreadRecorder(100_000);
        s.For(0, 100_000, r);
        var byWorkers = Enumerable.Range(0, r.Worker.Length).Where(i => r.Worker[i] != 0).ToArray();
        Assert.NotEmpty(byWorkers);
        foreach (var i in byWorkers)
        {
            Assert.Equal($"custom-{r.Worker[i]}", r.Name[i]);
            if (OperatingSystem.IsMacOS()) Assert.True(r.Interactive[i], $"worker {r.Worker[i]} not in the user-interactive band");
        }
        Assert.Throws<ArgumentNullException>(() => HeartbeatThreads.Background(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => HeartbeatThreads.Background("x", (HeartbeatThreadQuality)5));
    }

    [Fact]
    public void MicrosecondEstimates_MatchTheirTicks_BelowTimeSpansResolution()
    {
        using var s = new HeartbeatScheduler(Aggressive with { HeartbeatMicroseconds = 100_000 });   // beats effectively never
        Thread.Sleep(5);
        s.ForMicroseconds(0, 10_000, new Recorder(10_000, _ => 20), 1);           // 10 ms: under a heartbeat
        Assert.Equal(0, s.Splits);
        s.ForMicroseconds(0, 10_000, new Recorder(10_000, _ => 20), 100);         // 1 s: split up front
        Assert.True(s.Splits > 0);

        // 50 ns, which a TimeSpan cannot hold: 1,000 iterations predict 50 µs, under the 100 ms heartbeat, so the loop runs
        // plain on the calling thread although each iteration really takes far longer.
        var tiny = new Recorder(1_000, _ => 20);
        long before = s.Splits;
        s.ForMicroseconds(0, 1_000, tiny, 0.05);
        Assert.Equal(before, s.Splits);
        Assert.All(tiny.Worker, w => Assert.Equal(0, w));

        int[] count = new int[1_000], worker = new int[1_000], batchCount = new int[1_000], maxBatch = new int[1];
        s.ForMicroseconds(0, 1_000, new StructRecorder(count, worker), 0.05);
        s.ForMicroseconds(0, 1_000, new StructBatchRecorder(batchCount, maxBatch), 16, 0.05);
        s.ForMicroseconds(0, 1_000, new BatchRecorder(1_000), 16, 0.05);
        Assert.All(count, c => Assert.Equal(1, c));
        Assert.All(batchCount, c => Assert.Equal(1, c));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.ForMicroseconds(0, 10, new Recorder(10), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.ForMicroseconds(0, 10, new Recorder(10), double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.ForMicroseconds(0, 10, new Recorder(10), double.PositiveInfinity));
    }
}
