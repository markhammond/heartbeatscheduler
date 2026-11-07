using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace HeartbeatScheduling;

/// <summary>Configuration of a <see cref="HeartbeatScheduler"/>.</summary>
public sealed record HeartbeatOptions
{
    /// <summary>Worker threads, in addition to the calling thread.</summary>
    public int Workers { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>
    /// Heartbeat period: each thread splits its remaining range at most once per period, so splitting costs at most
    /// (cost of a hand-off) / period of the run time. From 0.1 to 1,000,000 (one second).
    /// </summary>
    /// <remarks>
    /// Calibrating it to the workload's loops: the period trades what splitting may cost against how soon a loop
    /// spreads across the threads.
    /// <list type="bullet">
    /// <item><b>The overhead bound.</b> A hand-off costs about 0.2-0.5 µs (more on a busy or virtualized machine), so
    /// the default 5 µs keeps splitting to about 5% of run time: some 20 hand-offs to a period, as heartbeat scheduling
    /// recommends. At 2-3 µs the bound passes 10%; at 1 µs, 20%.</item>
    /// <item><b>Spreading.</b> Without an estimate, a loop reaches every thread after about log2(Workers + 1)
    /// heartbeats: three at 5 µs on six cores, so 15 µs. Loops much longer than that hardly notice the period. For loops
    /// of a few such spreads (10-50 µs), a shorter period (2-3 µs) runs more of each loop in parallel, at the higher
    /// overhead above.</item>
    /// <item><b>Short loops.</b> A loop shorter than a period never splits (with <see cref="HeartbeatIdlePolicy.BusySpin"/>,
    /// at most once). With many loops of only a few periods and no estimate, a longer period (10-20 µs) keeps beats
    /// from landing in loops too short to repay a split.</item>
    /// <item><b>With an estimate</b> (<c>ForMicroseconds</c>, or the <c>For</c> overloads that take a <see cref="TimeSpan"/>), the period
    /// also draws the lines: a loop predicted to take less than a period runs as a plain loop on the calling thread, one
    /// predicted to take more splits up front, and each half of a split keeps a quarter of a period's predicted work.
    /// Set it near the shortest loop worth parallelizing, about 20 hand-offs.</item>
    /// <item><b>Checking a choice.</b> Time representative loops at two or three periods (say 2, 5 and 10 µs), and
    /// watch <see cref="HeartbeatScheduler.Splits"/>: splits near the ceiling (one per thread per period of the loop)
    /// mean splitting costs close to the bound, and a longer period may pay; long loops that hardly split spread slowly,
    /// and a shorter one may.</item>
    /// </list>
    /// </remarks>
    public double HeartbeatMicroseconds { get; init; } = 5;

    /// <summary>Without an estimate of the iteration cost, a range is only split if both halves keep at least this many
    /// iterations (in a batched loop, the split point is rounded down to a whole batch, so the half kept may hold as
    /// little as one batch). With one, the guard is the work instead: both halves must keep a quarter of a heartbeat's
    /// predicted work, so a range of microsecond iterations splits down to a few, and one of nanosecond iterations does
    /// not split into slivers.</summary>
    public int MinChunk { get; init; } = 8;

    /// <summary>How long idle workers keep spinning after a loop before parking (<see cref="HeartbeatIdlePolicy.SpinThenPark"/>).
    /// Parked workers wake in microseconds to tens of microseconds, so loops in quick succession should find them spinning:
    /// set it to cover the usual gap between loops. From 0 to 60,000,000 (one minute).</summary>
    public double SpinBeforeParkMicroseconds { get; init; } = 500;

    /// <summary>What idle workers do between loops (see <see cref="HeartbeatIdlePolicy"/> for choosing).</summary>
    public HeartbeatIdlePolicy IdlePolicy { get; init; } = HeartbeatIdlePolicy.SpinThenPark;

    /// <summary>
    /// Where the workers run: called once per worker lane (1..<see cref="Workers"/>) with that worker's loop, to run on a
    /// thread of the host's choosing, set up as it wishes (priority, affinity, quality of service: see
    /// <see cref="HeartbeatThreads"/>). Post it to one of the host's event loops, or start a thread. The loop holds its
    /// thread until the scheduler is disposed (it spins, or parks, between loops), must run once (a second run throws),
    /// and may start late: a worker not yet running is simply not offered work. Null (the default):
    /// <see cref="HeartbeatThreads.Background"/>, a background thread per worker.
    /// </summary>
    public Action<int, Action>? StartWorker { get; init; }

    /// <summary>Test mode: every clock read is made this much slower, as on hosts with a slow clock (a virtual machine
    /// whose clock source is not the CPU's counter). 0: off.</summary>
    internal double SlowClockNanoseconds { get; init; }
}

/// <summary>What a <see cref="HeartbeatScheduler"/>'s idle workers do between loops. During a loop, idle workers always
/// spin: they are the heartbeat, and take the work busy threads split off.</summary>
/// <remarks>
/// Choosing:
/// <list type="bullet">
/// <item><see cref="SpinThenPark"/>, the default, suits a shared machine, development, loops that come in bursts, and
/// wherever energy matters (a laptop on battery, a server billed or cooled by the watt): between bursts the workers cost
/// no CPU and let the cores idle. Loops closer together than <see cref="HeartbeatOptions.SpinBeforeParkMicroseconds"/> find the
/// workers still spinning and run much as under <see cref="BusySpin"/>, so set that to cover the usual gap between loops. A
/// loop after a longer gap first wakes the workers (microseconds to tens), and a loop shorter than a heartbeat never
/// splits.</item>
/// <item><see cref="BusySpin"/> suits cores reserved for the scheduler, with loops back to back: a level-by-level graph
/// evaluation, a simulation tick, a query pipeline. No loop pays a signal or a wake-up, which matters most for loops of a
/// few microseconds. The workers hold their cores at all times. On chips that slow each core as more are busy, they also
/// slow the calling thread, by a few percent with a few spinning workers and by as much as a third with one on every core.
/// So give it the cores its loops can use, and no more.</item>
/// </list>
/// </remarks>
public enum HeartbeatIdlePolicy
{
    /// <summary>Spin for <see cref="HeartbeatOptions.SpinBeforeParkMicroseconds"/>, then park until the next loop: loops
    /// in quick succession find the workers spinning, and an idle scheduler costs no CPU (or energy). The choice for
    /// development and shared machines.</summary>
    SpinThenPark,

    /// <summary>Spin until disposed, never parking, as idle threads of a running loop do: workers take offers and move
    /// the beat on at all times, so starting a loop signals no one and costs only the caller's own bookkeeping. A loop
    /// then starts on the current beat rather than a fresh one, so one shorter than a heartbeat may still split once
    /// (with odds of about its length over a heartbeat). The cost: a busy core per worker at all times. For a dedicated
    /// machine where loops arrive back to back.</summary>
    BusySpin,
}

/// <summary>How the operating system schedules a thread (see <see cref="HeartbeatThreads"/>).</summary>
public enum HeartbeatThreadQuality
{
    /// <summary>The runtime's default.</summary>
    Default,

    /// <summary>The highest an ordinary thread may have. On macOS, the user-interactive band, which Apple silicon serves
    /// from its fastest cores: its quality of service where the thread can take one, else its priority (47, against 31
    /// by default; threads .NET starts have their priority set the POSIX way, which opts them out of quality of
    /// service). On Windows, <see cref="ThreadPriority.Highest"/>. No effect on Linux, where raising a thread's priority
    /// needs privileges (pin or prioritize the process instead: taskset, nice, cgroups).</summary>
    High,
}

/// <summary>The body of a heartbeat-scheduled loop. One instance is shared by every thread of a loop; a struct body
/// (see <see cref="HeartbeatScheduler.For{TLoop}(int, int, in TLoop)"/>) is copied for each range instead.</summary>
public interface IHeartbeatLoop
{
    /// <summary>
    /// Runs iteration <paramref name="index"/> on worker <paramref name="worker"/>: 0 is the thread that called
    /// <see cref="HeartbeatScheduler.For(int, int, IHeartbeatLoop)"/>, 1..<see cref="HeartbeatScheduler.Workers"/> are the scheduler's
    /// threads. A worker runs one iteration at a time, so per-worker state indexed by it needs no synchronization.
    /// </summary>
    void Execute(int index, int worker);

    /// <summary>
    /// Called on the calling thread when its own share of the range is done, unless the loop has failed: a place for
    /// work that must stay on the calling thread but may overlap with the loop. The calling thread then helps the
    /// workers until all are done, so it may still run iterations afterwards, as worker 0.
    /// </summary>
    void CallerRangeDone() { }
}

/// <summary>
/// The body of a heartbeat-scheduled loop processed in batches: the scheduler hands each thread contiguous batches of up
/// to <c>batch</c> iterations and checks the heartbeat between them, so the body's inner loop over a batch stays
/// tight (inlined, register-resident accumulators, vectorizable). The form to use when an iteration costs only a
/// few nanoseconds, e.g. a vector of rows in a query engine. One instance is shared by every thread of a loop; a struct
/// body is copied for each range instead.
/// </summary>
public interface IHeartbeatBatchLoop
{
    /// <summary>Runs iterations [<paramref name="start"/>, <paramref name="end"/>) on worker <paramref name="worker"/>
    /// (0 is the calling thread). A worker runs one batch at a time.</summary>
    void Execute(int start, int end, int worker);

    /// <summary>See <see cref="IHeartbeatLoop.CallerRangeDone"/>.</summary>
    void CallerRangeDone() { }
}

/// <summary>
/// Heartbeat scheduling (Acar, Charguéraud, Rainey et al.) for flat parallel loops of micro-tasks.
/// <list type="bullet">
/// <item>Each thread runs its range front to back. Between iterations it checks a shared beat counter (one load); if
/// the beat moved since its last split, it offers the upper half of its remaining range to an idle thread. Task
/// creation is thereby bounded to one per heartbeat per thread, independent of iteration size, and a loop shorter
/// than a heartbeat runs as a sequential loop on the calling thread (exactly so with
/// <see cref="HeartbeatIdlePolicy.SpinThenPark"/>; with <see cref="HeartbeatIdlePolicy.BusySpin"/> it may split once).</item>
/// <item>Only idle threads advance the beat: a split is only made when someone can take it, and the clock reads cost
/// the busy threads nothing. The calling thread, once its own share is done, is an idle thread like the workers: it
/// takes work from busy ones until the loop completes.</item>
/// <item>With an estimate of the iteration cost, a range expected to take more than a heartbeat is split as soon
/// as a thread is idle (at the start, or the moment one becomes available), without waiting for a beat.</item>
/// <item>A hand-off is one mailbox word per thread: the offerer installs a range with CAS and retracts it with CAS if
/// the thread turned out not to be idle; the thread claims it with CAS, leaving the mailbox busy until it is idle
/// again. Exactly one side wins.</item>
/// <item>The join is a pending-range counter reaching zero (a full fence), so everything the loop wrote is visible to
/// the caller when <c>For</c> returns. The first exception from any thread is rethrown after the join; the other
/// threads abandon the rest of their ranges at their next beat.</item>
/// </list>
/// One loop at a time: <c>For</c> must not be called concurrently or from inside a loop body. Debug builds check this
/// and throw <see cref="InvalidOperationException"/>, as does any build compiled with the <c>HEARTBEAT_REENTRANCY_GUARD</c>
/// symbol (<c>-p:HeartbeatReentrancyGuard=true</c>); other release builds do not (the check is an atomic per loop), and a
/// loop started anyway corrupts the one running.
/// </summary>
public sealed class HeartbeatScheduler : IDisposable
{
    internal const int CacheLine = 128;          // Apple silicon's line, and an adjacent pair of x64's 64-byte lines
    private const long Busy = long.MinValue;     // a mailbox whose lane runs a claimed range: lo = int.MinValue, length 0
    // Given an estimate: both halves of a split keep a quarter heartbeat of predicted work (measured against 1/10 and 1:
    // as good as 1/10, and a whole heartbeat starves narrow loops), and ranges predicted over a heartbeat, the line
    // below which a loop runs plain, split as soon as a thread is idle (over two: loops of one to two heartbeats waited
    // for a beat, 1.41x against 1.64x on narrow levels of ~100 ns nodes).
    private const double MinHalfHeartbeats = 0.25, EagerHeartbeats = 1;

    /// <summary>One thread's hand-off state, alone on its cache lines: offers to one lane never disturb another. A full
    /// line of padding on each side, as objects are only 8-byte aligned.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 3 * CacheLine)]
    internal struct Lane
    {
        [FieldOffset(CacheLine)] public long Mailbox;       // 0: empty; Busy; else lo << 32 | (hi - lo): a length is never 0
        [FieldOffset(CacheLine + 8)] public int Idle;       // owner-written: open to offers
        [FieldOffset(CacheLine + 12)] public int Started;   // the worker's loop has run (once only)
    }

    /// <summary>The beat, read by every busy thread at every iteration, alone on its cache lines: the counters that
    /// change at each hand-off never invalidate it.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 3 * CacheLine)]
    internal struct Clock
    {
        [FieldOffset(CacheLine)] public long Beat;
        [FieldOffset(CacheLine + 8)] public long LastBeat;  // written with Beat, read by idle threads
    }

    /// <summary>The lane that reads the clock for everyone, alone on its cache lines: role changes never disturb the
    /// beat the busy threads read.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 3 * CacheLine)]
    internal struct BeaterSlot
    {
        [FieldOffset(CacheLine)] public int Holder;        // the beater's lane, or NoBeater
    }

    private const int NoBeater = -1;

    /// <summary>A lane's split count, alone on its cache lines: written by the lane's own thread only, so splitting takes
    /// no atomic and no thread writes a line another one does.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 3 * CacheLine)]
    internal struct SplitCount
    {
        [FieldOffset(CacheLine)] public long Value;
    }

    /// <summary>An idle thread's beat duty across its passes.</summary>
    private struct IdleClock
    {
        public bool Timed;                       // as the beater: Since began a run of reads that can be timed
        public long Since;
        public int Reads;
        public long SeenBeat;                    // as a watcher: the beat last seen, and passes it has not moved for
        public int Quiet;
    }

    private readonly HeartbeatOptions _options;
    private readonly Lane[] _lanes;              // 0 = the calling thread, 1.. = the workers
    private readonly int _workers;
    private int _runningWorkers;                 // worker loops running (Dispose waits for them on the host's threads)
    private readonly long _beatTicks, _spinBeforeParkTicks;
    private readonly bool _park;                 // idle workers park between loops (SpinThenPark)
    private readonly long _slowClockTicks;       // test mode: each clock read takes this long
    private readonly double _nsPerTick = 1e9 / Stopwatch.Frequency;
    private readonly SemaphoreSlim _wake = new(0);
    private Clock _clock;
    private BeaterSlot _beater = new() { Holder = NoBeater };
    private int _stealAfterPasses;               // a watcher takes over the beat after this many passes without one
    private volatile bool _stopping;
#if DEBUG || HEARTBEAT_REENTRANCY_GUARD
    private int _running;                         // 1 while a loop runs (reentrancy guard: debug builds, or HeartbeatReentrancyGuard)
#endif
    private int _phase;                           // 1 while a loop runs (workers spin, beat, take ranges)
    private int _pending;                         // offered ranges not yet completed
    private int _parked;
    private int _idleLanes;                       // lanes idle in a running loop: estimate-driven splits need one
    private readonly SplitCount[] _splits;        // per lane, summed by Splits
    private Runner? _runner;
    private Runner? _lastRunner;
    private Dictionary<Type, Runner>? _runners;
    private int _batch;
    private double _estimate;
    private Exception? _fault;

    /// <summary>Starts the worker threads (idle workers park after <see cref="HeartbeatOptions.SpinBeforeParkMicroseconds"/>).</summary>
    public HeartbeatScheduler(HeartbeatOptions? options = null)
    {
        _options = options ?? new HeartbeatOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.Workers, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MinChunk, 1);
        // Finite, and small enough that the conversion to ticks cannot saturate (a heartbeat of long.MaxValue ticks would
        // quietly turn splitting off; a spin of it, parking off).
        if (_options.HeartbeatMicroseconds is not (>= 0.1 and <= 1e6))
            throw new ArgumentOutOfRangeException(nameof(options), _options.HeartbeatMicroseconds,
                "HeartbeatMicroseconds must be from 0.1 to 1,000,000 (one second).");
        if (_options.SpinBeforeParkMicroseconds is not (>= 0 and <= 6e7))
            throw new ArgumentOutOfRangeException(nameof(options), _options.SpinBeforeParkMicroseconds,
                "SpinBeforeParkMicroseconds must be from 0 to 60,000,000 (one minute).");
        if (!Enum.IsDefined(_options.IdlePolicy))
            throw new ArgumentOutOfRangeException(nameof(options), _options.IdlePolicy, "Not a HeartbeatIdlePolicy.");
        _park = _options.IdlePolicy == HeartbeatIdlePolicy.SpinThenPark;
        _beatTicks = Math.Max(1, (long)(_options.HeartbeatMicroseconds * Stopwatch.Frequency / 1e6));
        _spinBeforeParkTicks = (long)(_options.SpinBeforeParkMicroseconds * Stopwatch.Frequency / 1e6);
        _slowClockTicks = (long)(_options.SlowClockNanoseconds / _nsPerTick);
        ClockNanoseconds = NanosecondsOf(() => Now());
        // A first guess at a pass (~150 ns): the beater measures real ones (Thread.SpinWait's units are calibrated by
        // the runtime only after start-up, so a pass timed here could be off tenfold).
        _stealAfterPasses = StealAfter(150);
        _workers = _options.Workers;
        _lanes = new Lane[_workers + 1];
        _splits = new SplitCount[_workers + 1];
        var start = _options.StartWorker ?? HeartbeatThreads.Background();
        try
        {
            for (var lane = 1; lane <= _workers; lane++)
            {
                var l = lane;
                start(l, () => RunWorker(l));
            }
        }
        catch
        {
            Dispose();                                           // the workers already started return before this throws
            throw;
        }
    }

    /// <summary>A worker's loop, wherever it runs: once per lane, and not at all once the scheduler is stopping.</summary>
    private void RunWorker(int lane)
    {
        if (Interlocked.Exchange(ref _lanes[lane].Started, 1) != 0)
            throw new InvalidOperationException($"Worker {lane}'s loop runs once; it was run again.");
        Interlocked.Increment(ref _runningWorkers);              // full fence before reading _stopping (Dekker with Dispose)
        try
        {
            if (!_stopping) WorkerLoop(lane);
        }
        finally
        {
            Interlocked.Decrement(ref _runningWorkers);
        }
    }

    private double BeatNanoseconds => _beatTicks * _nsPerTick;

    /// <summary>Passes of about four heartbeats: a beater that has let the beat stall that long has stalled itself.</summary>
    private int StealAfter(double pass) => Math.Max(16, (int)(4 * BeatNanoseconds / pass));

    /// <summary>
    /// An idle pass's beat duty. One idle thread, the beater, reads the clock for all, every pass; every other idle
    /// thread watches its mailbox and never reads it: on a slow clock (some virtual machines read it through a system
    /// call) a read leaves the mailbox unwatched for its whole length, so offers go to the beater last. A watcher takes
    /// the role when it is free, or when the beat has not moved for about four heartbeats (its holder descheduled).
    /// </summary>
    private void IdleBeat(int lane, ref IdleClock c)
    {
        var beater = Volatile.Read(ref _beater.Holder);
        if (beater != lane)
        {
            if (beater == NoBeater) Interlocked.CompareExchange(ref _beater.Holder, lane, NoBeater);
            else
            {
                var beat = Volatile.Read(ref _clock.Beat);
                if (beat != c.SeenBeat)
                {
                    c.SeenBeat = beat;
                    c.Quiet = 0;
                }
                else if (++c.Quiet > _stealAfterPasses)
                {
                    Interlocked.CompareExchange(ref _beater.Holder, lane, beater);
                    c.Quiet = 0;
                }
            }
            c.Timed = false;                                     // a new beater's first read only starts the timing
            return;
        }
        var now = Beat();
        if (!c.Timed)
        {
            c.Since = now;
            c.Reads = 0;
            c.Timed = true;
        }
        else if (++c.Reads == 64)
        {
            // A watcher's pass is a beater's without the read: the stall threshold, counted in watcher passes, follows
            // what passes really cost.
            var pass = Math.Max(1, (now - c.Since) * _nsPerTick / 64 - ClockNanoseconds);
            PassNanoseconds = pass;
            _stealAfterPasses = StealAfter(pass);
            c.Since = now;
            c.Reads = 0;
        }
    }

    /// <summary>Gives up the beater role, if <paramref name="lane"/> holds it: another idle thread takes it.</summary>
    private void ReleaseBeater(int lane)
    {
        if (Volatile.Read(ref _beater.Holder) == lane) Interlocked.CompareExchange(ref _beater.Holder, NoBeater, lane);
    }

    /// <summary>The beater (diagnostics; <see cref="NoBeater"/> when none).</summary>
    internal int Beater => Volatile.Read(ref _beater.Holder);

    /// <summary>Test hook: hands the role to <paramref name="lane"/>, which may be a lane that never beats.</summary>
    internal void SetBeater(int lane) => Volatile.Write(ref _beater.Holder, lane);

    /// <summary>A clock read, measured at construction (diagnostics).</summary>
    internal double ClockNanoseconds { get; }

    /// <summary>An idle pass, as the beater last measured it (diagnostics).</summary>
    internal double PassNanoseconds { get; private set; }

    /// <summary>Mean nanoseconds of <paramref name="action"/>, over enough calls to see past the clock's own cost.</summary>
    private static double NanosecondsOf(Action action)
    {
        const int calls = 256;
        for (var i = 0; i < 16; i++) action();
        var best = long.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            var t0 = Stopwatch.GetTimestamp();
            for (var i = 0; i < calls; i++) action();
            best = Math.Min(best, Stopwatch.GetTimestamp() - t0);
        }
        return best * 1e9 / Stopwatch.Frequency / calls;
    }

    /// <summary>The clock, as idle threads read it (slowed in the test mode: a wait on the real clock, exact whatever
    /// Thread.SpinWait's units).</summary>
    private long Now()
    {
        var t = Stopwatch.GetTimestamp();
        if (_slowClockTicks == 0) return t;
        var start = t;
        while ((t = Stopwatch.GetTimestamp()) - start < _slowClockTicks) { }
        return t;
    }

    /// <summary>Beats so far.</summary>
    internal long Beats => Volatile.Read(ref _clock.Beat);

    /// <summary>Worker threads; lanes passed to <see cref="IHeartbeatLoop.Execute"/> are 0..Workers.</summary>
    public int Workers => _workers;

    /// <summary>Ranges handed to other threads so far (diagnostics): exact on the calling thread once <c>For</c> returns,
    /// approximate while a loop runs. Counted per lane and summed here, so splitting costs no shared write.</summary>
    public long Splits
    {
        get
        {
            long total = 0;
            for (var k = 0; k < _splits.Length; k++) total += Volatile.Read(ref _splits[k].Value);
            return total;
        }
    }

    /// <summary>The scheduler's configuration.</summary>
    public HeartbeatOptions Options => _options;

    /// <summary>
    /// Runs <paramref name="loop"/> for every index in [<paramref name="start"/>, <paramref name="end"/>), each exactly
    /// once, on the calling thread and any idle workers, and returns when all have completed. Allocation-free.
    /// </summary>
    /// <param name="start">First index (inclusive).</param>
    /// <param name="end">Last index (exclusive).</param>
    /// <param name="loop">The loop body, shared by all threads.</param>
    public void For(int start, int end, IHeartbeatLoop loop) => ForTicks(start, end, loop, 0);

    /// <summary>The estimate in <see cref="Stopwatch"/> ticks, whose length varies by platform (tests and benchmarks).</summary>
    internal void ForTicks(int start, int end, IHeartbeatLoop loop, double estimatedTicksPerIteration)
    {
        ArgumentNullException.ThrowIfNull(loop);
        Run(new Items(loop), start, end, 1, estimatedTicksPerIteration);
    }

    /// <summary>
    /// As <see cref="For(int, int, IHeartbeatLoop)"/>, with an estimate of the mean iteration cost: the mean, as it
    /// predicts the work left. A loop predicted to take less than a heartbeat runs as a plain loop on the calling thread,
    /// involving no worker; ranges predicted to exceed a heartbeat are split up front. A <see cref="TimeSpan"/>'s
    /// resolution is 100 ns: for costs below that, use <see cref="ForMicroseconds(int, int, IHeartbeatLoop, double)"/>.
    /// </summary>
    /// <param name="start">First index (inclusive).</param>
    /// <param name="end">Last index (exclusive).</param>
    /// <param name="loop">The loop body, shared by all threads.</param>
    /// <param name="estimatedIterationCost">Mean iteration cost (<see cref="TimeSpan.Zero"/>: not known).</param>
    public void For(int start, int end, IHeartbeatLoop loop, TimeSpan estimatedIterationCost)
        => ForTicks(start, end, loop, Ticks(estimatedIterationCost));

    /// <summary>
    /// Runs <paramref name="loop"/> over [<paramref name="start"/>, <paramref name="end"/>) in batches of at most
    /// <paramref name="batch"/> iterations, each iteration exactly once, on the calling thread and any idle workers;
    /// returns when all have completed. Splits happen at batch boundaries. Allocation-free.
    /// </summary>
    /// <param name="start">First index (inclusive).</param>
    /// <param name="end">Last index (exclusive).</param>
    /// <param name="loop">The loop body, shared by all threads.</param>
    /// <param name="batch">Iterations per call: large enough to amortize the call and the heartbeat check (a few
    /// hundred nanoseconds of work), small enough to keep splits fine-grained.</param>
    public void For(int start, int end, IHeartbeatBatchLoop loop, int batch) => ForTicks(start, end, loop, batch, 0);

    /// <summary>The estimate in <see cref="Stopwatch"/> ticks (tests and benchmarks).</summary>
    internal void ForTicks(int start, int end, IHeartbeatBatchLoop loop, int batch, double estimatedTicksPerIteration)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        Run(new Batches(loop), start, end, batch, estimatedTicksPerIteration);
    }

    /// <summary>As <see cref="For(int, int, IHeartbeatBatchLoop, int)"/>, with an estimate of the mean iteration cost (see
    /// <see cref="For(int, int, IHeartbeatLoop, TimeSpan)"/>; 100 ns resolution: for costs below that, use
    /// <see cref="ForMicroseconds(int, int, IHeartbeatBatchLoop, int, double)"/>).</summary>
    /// <param name="start">First index (inclusive).</param>
    /// <param name="end">Last index (exclusive).</param>
    /// <param name="loop">The loop body, shared by all threads.</param>
    /// <param name="batch">Iterations per call.</param>
    /// <param name="estimatedIterationCost">Mean iteration cost (<see cref="TimeSpan.Zero"/>: not known).</param>
    public void For(int start, int end, IHeartbeatBatchLoop loop, int batch, TimeSpan estimatedIterationCost)
        => ForTicks(start, end, loop, batch, Ticks(estimatedIterationCost));

    /// <summary>
    /// As <see cref="For(int, int, IHeartbeatLoop)"/>, with a struct body the JIT can inline into the scheduling loop:
    /// no interface call per iteration. Each range runs on its own copy of <paramref name="loop"/>, so keep results in
    /// arrays or objects it references, not in its own fields. Allocation-free once the scheduler has run a loop of
    /// this type.
    /// </summary>
    /// <param name="start">First index (inclusive).</param>
    /// <param name="end">Last index (exclusive).</param>
    /// <param name="loop">The loop body, copied for each range.</param>
    public void For<TLoop>(int start, int end, in TLoop loop) where TLoop : struct, IHeartbeatLoop => ForTicks(start, end, in loop, 0d);

    /// <summary>The estimate in <see cref="Stopwatch"/> ticks (tests and benchmarks).</summary>
    internal void ForTicks<TLoop>(int start, int end, in TLoop loop, double estimatedTicksPerIteration) where TLoop : struct, IHeartbeatLoop
        => Run(new StructItems<TLoop>(loop), start, end, 1, estimatedTicksPerIteration);

    /// <summary>As <see cref="For{TLoop}(int, int, in TLoop)"/>, with an estimate of the mean iteration cost (see
    /// <see cref="For(int, int, IHeartbeatLoop, TimeSpan)"/>; 100 ns resolution: for costs below that, use
    /// <see cref="ForMicroseconds{TLoop}(int, int, in TLoop, double)"/>).</summary>
    /// <param name="start">First index (inclusive).</param>
    /// <param name="end">Last index (exclusive).</param>
    /// <param name="loop">The loop body, copied for each range.</param>
    /// <param name="estimatedIterationCost">Mean iteration cost (<see cref="TimeSpan.Zero"/>: not known).</param>
    public void For<TLoop>(int start, int end, in TLoop loop, TimeSpan estimatedIterationCost) where TLoop : struct, IHeartbeatLoop
        => ForTicks(start, end, in loop, Ticks(estimatedIterationCost));

    /// <summary>
    /// As <see cref="For(int, int, IHeartbeatBatchLoop, int)"/>, with a struct body the JIT can inline. Each range runs
    /// on its own copy of <paramref name="loop"/>, so keep results in arrays or objects it references, not in its own
    /// fields. Allocation-free once the scheduler has run a loop of this type.
    /// </summary>
    /// <param name="start">First index (inclusive).</param>
    /// <param name="end">Last index (exclusive).</param>
    /// <param name="loop">The loop body, copied for each range.</param>
    /// <param name="batch">Iterations per call.</param>
    public void For<TLoop>(int start, int end, in TLoop loop, int batch) where TLoop : struct, IHeartbeatBatchLoop
        => ForTicks(start, end, in loop, batch, 0);

    /// <summary>The estimate in <see cref="Stopwatch"/> ticks (tests and benchmarks).</summary>
    internal void ForTicks<TLoop>(int start, int end, in TLoop loop, int batch, double estimatedTicksPerIteration)
        where TLoop : struct, IHeartbeatBatchLoop
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        Run(new StructBatches<TLoop>(loop), start, end, batch, estimatedTicksPerIteration);
    }

    /// <summary>As <see cref="For{TLoop}(int, int, in TLoop, int)"/>, with an estimate of the mean iteration cost (see
    /// <see cref="For(int, int, IHeartbeatLoop, TimeSpan)"/>; 100 ns resolution: for costs below that, use
    /// <see cref="ForMicroseconds{TLoop}(int, int, in TLoop, int, double)"/>).</summary>
    /// <param name="start">First index (inclusive).</param>
    /// <param name="end">Last index (exclusive).</param>
    /// <param name="loop">The loop body, copied for each range.</param>
    /// <param name="batch">Iterations per call.</param>
    /// <param name="estimatedIterationCost">Mean iteration cost (<see cref="TimeSpan.Zero"/>: not known).</param>
    public void For<TLoop>(int start, int end, in TLoop loop, int batch, TimeSpan estimatedIterationCost)
        where TLoop : struct, IHeartbeatBatchLoop
        => ForTicks(start, end, in loop, batch, Ticks(estimatedIterationCost));

    /// <summary>As <see cref="For(int, int, IHeartbeatLoop, TimeSpan)"/>, with the mean iteration cost in microseconds, of any
    /// precision (0.05 for 50 ns, below a <see cref="TimeSpan"/>'s resolution).</summary>
    /// <param name="start">First index (inclusive).</param>
    /// <param name="end">Last index (exclusive).</param>
    /// <param name="loop">The loop body, shared by all threads.</param>
    /// <param name="estimatedMicrosecondsPerIteration">Mean iteration cost in microseconds (0: not known).</param>
    public void ForMicroseconds(int start, int end, IHeartbeatLoop loop, double estimatedMicrosecondsPerIteration)
        => ForTicks(start, end, loop, Ticks(estimatedMicrosecondsPerIteration));

    /// <summary>As <see cref="For(int, int, IHeartbeatBatchLoop, int, TimeSpan)"/>, with the mean iteration cost in
    /// microseconds, of any precision.</summary>
    /// <param name="start">First index (inclusive).</param>
    /// <param name="end">Last index (exclusive).</param>
    /// <param name="loop">The loop body, shared by all threads.</param>
    /// <param name="batch">Iterations per call.</param>
    /// <param name="estimatedMicrosecondsPerIteration">Mean iteration cost in microseconds (0: not known).</param>
    public void ForMicroseconds(int start, int end, IHeartbeatBatchLoop loop, int batch, double estimatedMicrosecondsPerIteration)
        => ForTicks(start, end, loop, batch, Ticks(estimatedMicrosecondsPerIteration));

    /// <summary>As <see cref="For{TLoop}(int, int, in TLoop, TimeSpan)"/>, with the mean iteration cost in microseconds, of
    /// any precision.</summary>
    /// <param name="start">First index (inclusive).</param>
    /// <param name="end">Last index (exclusive).</param>
    /// <param name="loop">The loop body, copied for each range.</param>
    /// <param name="estimatedMicrosecondsPerIteration">Mean iteration cost in microseconds (0: not known).</param>
    public void ForMicroseconds<TLoop>(int start, int end, in TLoop loop, double estimatedMicrosecondsPerIteration)
        where TLoop : struct, IHeartbeatLoop
        => ForTicks(start, end, in loop, Ticks(estimatedMicrosecondsPerIteration));

    /// <summary>As <see cref="For{TLoop}(int, int, in TLoop, int, TimeSpan)"/>, with the mean iteration cost in
    /// microseconds, of any precision.</summary>
    /// <param name="start">First index (inclusive).</param>
    /// <param name="end">Last index (exclusive).</param>
    /// <param name="loop">The loop body, copied for each range.</param>
    /// <param name="batch">Iterations per call.</param>
    /// <param name="estimatedMicrosecondsPerIteration">Mean iteration cost in microseconds (0: not known).</param>
    public void ForMicroseconds<TLoop>(int start, int end, in TLoop loop, int batch, double estimatedMicrosecondsPerIteration)
        where TLoop : struct, IHeartbeatBatchLoop
        => ForTicks(start, end, in loop, batch, Ticks(estimatedMicrosecondsPerIteration));

    /// <summary>A <see cref="TimeSpan"/> cost in <see cref="Stopwatch"/> ticks.</summary>
    private static double Ticks(TimeSpan cost)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cost, TimeSpan.Zero);
        return cost.Ticks * ((double)Stopwatch.Frequency / TimeSpan.TicksPerSecond);
    }

    /// <summary>A cost in microseconds, in <see cref="Stopwatch"/> ticks.</summary>
    private static double Ticks(double microseconds)
    {
        if (!double.IsFinite(microseconds)) throw new ArgumentOutOfRangeException(nameof(microseconds), "Not a finite number.");
        ArgumentOutOfRangeException.ThrowIfNegative(microseconds);
        return microseconds * (Stopwatch.Frequency / 1e6);
    }

    // ------------------------------------------------------------------------------------------------ bodies

    /// <summary>A loop body as the scheduling loop sees it: one iteration at a time, or one batch at a time
    /// (<see cref="Batched"/>, a constant to the JIT, so each specialization keeps only its own loop).</summary>
    private interface IInvoker
    {
        static abstract bool Batched { get; }
        void Execute(int index, int lane);
        void Execute(int start, int end, int lane);
        void CallerRangeDone();
    }

    private readonly struct Items(IHeartbeatLoop loop) : IInvoker
    {
        public static bool Batched => false;
        public void Execute(int index, int lane) => loop.Execute(index, lane);
        public void Execute(int start, int end, int lane) => throw new NotSupportedException();
        public void CallerRangeDone() => loop.CallerRangeDone();
    }

    private readonly struct Batches(IHeartbeatBatchLoop loop) : IInvoker
    {
        public static bool Batched => true;
        public void Execute(int index, int lane) => throw new NotSupportedException();
        public void Execute(int start, int end, int lane) => loop.Execute(start, end, lane);
        public void CallerRangeDone() => loop.CallerRangeDone();
    }

    private struct StructItems<TLoop>(TLoop loop) : IInvoker where TLoop : struct, IHeartbeatLoop
    {
        private TLoop _loop = loop;
        public static bool Batched => false;
        public void Execute(int index, int lane) => _loop.Execute(index, lane);
        public void Execute(int start, int end, int lane) => throw new NotSupportedException();
        public void CallerRangeDone() { if (Implements<TLoop, IHeartbeatLoop>.CallerRangeDone) _loop.CallerRangeDone(); }
    }

    private struct StructBatches<TLoop>(TLoop loop) : IInvoker where TLoop : struct, IHeartbeatBatchLoop
    {
        private TLoop _loop = loop;
        public static bool Batched => true;
        public void Execute(int index, int lane) => throw new NotSupportedException();
        public void Execute(int start, int end, int lane) => _loop.Execute(start, end, lane);
        public void CallerRangeDone() { if (Implements<TLoop, IHeartbeatBatchLoop>.CallerRangeDone) _loop.CallerRangeDone(); }
    }

    /// <summary>Whether a struct body implements <c>CallerRangeDone</c>: the interface's default, called through a
    /// struct, would box it on every loop. Read once per type (a constant to the JIT), so the call is elided.</summary>
    private static class Implements<TLoop,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] TInterface>
        where TLoop : struct, TInterface
    {
        // One value per closed type, which is the point (hence the suppression: Rider warns because a static in a generic
        // type is usually meant to be shared, and is not). Safe across threads: the runtime runs a type's initializer
        // exactly once, before the field's first read, and blocks other threads until it has; the field is readonly
        // after. Tier-1 code then reads it as a constant, so the branch on it costs nothing.
        // ReSharper disable once StaticMemberInGenericType
        public static readonly bool CallerRangeDone = Find();

        private static bool Find()
        {
            var map = typeof(TLoop).GetInterfaceMap(typeof(TInterface));
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
                if (map.InterfaceMethods[i].Name == nameof(IHeartbeatLoop.CallerRangeDone))
                    return map.TargetMethods[i].DeclaringType != typeof(TInterface);
            return true;
        }
    }

    /// <summary>The running loop's body behind one virtual call per range: inside, the body is a struct the JIT
    /// specializes the scheduling loop for.</summary>
    private abstract class Runner
    {
        public abstract void Process(HeartbeatScheduler s, int lane, int i, int end);
    }

    private sealed class Runner<T> : Runner where T : struct, IInvoker
    {
        public T Body;
        public override void Process(HeartbeatScheduler s, int lane, int i, int end) => s.ProcessRange(Body, lane, i, end);
    }

    /// <summary>One runner per body type, reused: only a scheduler's first loop of a type allocates.</summary>
    private Runner<T> RunnerOf<T>() where T : struct, IInvoker
    {
        if (_lastRunner is Runner<T> last) return last;
        _runners ??= [];
        if (!_runners.TryGetValue(typeof(T), out var r)) _runners[typeof(T)] = r = new Runner<T>();
        _lastRunner = r;
        return (Runner<T>)r;
    }

    // ------------------------------------------------------------------------------------------------ the loop

    private void Run<T>(T body, int start, int end, int batch, double estimate) where T : struct, IInvoker
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (end <= start)
        {
            body.CallerRangeDone();
            return;
        }
#if DEBUG || HEARTBEAT_REENTRANCY_GUARD
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("HeartbeatScheduler runs one loop at a time: For was called concurrently or from inside a loop body.");
#endif
        try
        {
            // Not enough slack to split: with no workers, or an estimate predicting less than a heartbeat of work (no
            // split could win back its hand-off), the loop runs plain on the calling thread: no beat checks, no worker.
            if (_workers == 0 || (estimate > 0 && ((long)end - start) * estimate < _beatTicks))
            {
                RunPlain(body, start, end, batch);
                body.CallerRangeDone();
                return;
            }
            var runner = RunnerOf<T>();
            runner.Body = body;
            _runner = runner;
            _batch = batch;
            _estimate = estimate;
            if (_park)
            {
                // The beat clock restarts, so the next beat comes a heartbeat in (the clock left from the last loop
                // would tick at once): a loop shorter than a heartbeat never splits. Spinning workers need no signal:
                // they are idle threads between loops too, beating all along, and the loop starts on the current beat.
                Volatile.Write(ref _clock.LastBeat, Now());
                Interlocked.Exchange(ref _phase, 1);             // full fence before reading _parked (Dekker with park)
                var parked = Volatile.Read(ref _parked);
                if (parked > 0) _wake.Release(parked);
            }
            try
            {
                ProcessRange(body, 0, start, end);
                if (Volatile.Read(ref _fault) is null) body.CallerRangeDone();
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _fault, ex, null);
            }
            Join();                                              // always, even after a failure
            if (_park) Volatile.Write(ref _phase, 0);
            if (Interlocked.Exchange(ref _fault, null) is { } fault) ExceptionDispatchInfo.Throw(fault);
        }
        finally
        {
            if (_runner is Runner<T> r) r.Body = default;        // drop the body's references
            _runner = null;
#if DEBUG || HEARTBEAT_REENTRANCY_GUARD
            Volatile.Write(ref _running, 0);
#endif
        }
    }

    /// <summary>
    /// A plain loop on the calling thread. Its own method, not inlined, so it is optimized for its own use: as a branch
    /// of <see cref="Run"/>, profile-guided optimization compiled it as cold (out of line, the body not inlined) whenever a
    /// scheduler's first loops of a type were not bypassed, and later bypassed loops ran at half speed.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunPlain<T>(T body, int start, int end, int batch) where T : struct, IInvoker
    {
        if (!T.Batched)
        {
            for (var i = start; i < end; i++) body.Execute(i, 0);
            return;
        }
        for (var i = start; i < end;)
        {
            var stop = (int)Math.Min(end, (long)i + batch);       // in long: i + batch may pass int.MaxValue
            body.Execute(i, stop, 0);
            i = stop;
        }
    }

    /// <summary>The calling thread, its own share done, is an idle lane until every offered range has completed:
    /// it takes work from busy threads and moves the beat on. No offer can be in flight once the count is zero, as
    /// only threads running a range make them.</summary>
    private void Join()
    {
        if (Volatile.Read(ref _pending) == 0) return;            // nothing offered: no need to touch the idle count
        var clock = new IdleClock();
        while (Volatile.Read(ref _pending) != 0)
        {
            SetIdle(0, true);                                    // again after each range taken: taking one clears it
            if (TryClaim(0))
            {
                clock.Timed = false;
                continue;
            }
            IdleBeat(0, ref clock);
            Thread.SpinWait(4);
        }
        SetIdle(0, false);
        ReleaseBeater(0);
    }

    private void ProcessRange<T>(T body, int lane, int i, int end) where T : struct, IInvoker
    {
        var batch = _batch;
        // Both halves keep MinChunk iterations, or, given an estimate, a share of a heartbeat's predicted work.
        var minHalf = _estimate > 0
            ? (long)Math.Clamp(Math.Ceiling(MinHalfHeartbeats * _beatTicks / _estimate), 1, int.MaxValue)
            : _options.MinChunk;
        var minSplit = 2L * Math.Max(minHalf, batch);              // remaining counts are long: a range may exceed int.MaxValue
        // Iterations predicted to take more than a heartbeat, once per range: a range shorter than that compares two
        // registers per iteration and never reads the idle count (saturates at long.MaxValue: never).
        var eager = _estimate > 0 ? (long)(EagerHeartbeats * _beatTicks / _estimate) : long.MaxValue;
        while ((long)end - i >= minSplit && (long)end - i > eager)   // up-front splits
        {
            var mid = Mid(i, end, batch);
            if (!TryOffer(lane, mid, end)) break;
            end = mid;
        }
        var seen = Volatile.Read(ref _clock.Beat);               // a range starts on the current beat: one split per beat seen
        while (i < end)
        {
            var left = (long)end - i;
            if (left >= minSplit)
            {
                var beat = Volatile.Read(ref _clock.Beat);
                if (beat != seen || (left > eager && Volatile.Read(ref _idleLanes) > 0))
                {
                    if (Volatile.Read(ref _fault) is not null) break;   // another thread failed: abandon the rest
                    seen = beat;
                    var mid = Mid(i, end, batch);
                    if (TryOffer(lane, mid, end)) end = mid;
                }
            }
            if (!T.Batched) body.Execute(i++, lane);
            else
            {
                var stop = (long)end - i > batch ? i + batch : end;
                body.Execute(i, stop, lane);
                i = stop;
            }
        }
    }

    /// <summary>The split point: halfway, rounded to a batch boundary relative to <paramref name="i"/>.</summary>
    private static int Mid(int i, int end, int batch)
    {
        var half = ((long)end - i) / 2;
        if (batch > 1) half = Math.Max(batch, half / batch * batch);
        return (int)(i + half);
    }

    // ------------------------------------------------------------------------------------------------ hand-offs

    /// <summary>Lane <paramref name="from"/> (the calling thread's) offers [<paramref name="lo"/>, <paramref name="hi"/>)
    /// to an idle lane: the beater last, as each clock read leaves its mailbox unwatched (for a microsecond or more on a
    /// slow clock).</summary>
    internal bool TryOffer(int from, int lo, int hi)
    {
        var m = ((long)lo << 32) | (uint)(hi - lo);     // hi > lo: never 0 (empty) nor Busy (length 0)
        var beater = Volatile.Read(ref _beater.Holder);
        var offered = false;
        for (var k = 0; k < _lanes.Length && !offered; k++)
            offered = k != beater && TryOfferTo(k, m);
        if (!offered) offered = beater >= 0 && beater < _lanes.Length && TryOfferTo(beater, m);
        if (offered)
        {
            // The lane's own thread is the only writer, and a 64-bit store is atomic: no interlocked add. The join's
            // full fence publishes it with the range's writes.
            ref var splits = ref _splits[from].Value;
            splits++;
        }
        return offered;
    }

    private bool TryOfferTo(int k, long m)
    {
        ref var l = ref _lanes[k];
        if (Volatile.Read(ref l.Idle) == 0 || Volatile.Read(ref l.Mailbox) != 0) return false;
        Interlocked.Increment(ref _pending);                  // before publishing: the join must wait for it
        if (Interlocked.CompareExchange(ref l.Mailbox, m, 0) != 0)
        {
            Interlocked.Decrement(ref _pending);
            return false;
        }
        if (Volatile.Read(ref l.Idle) == 0 && Interlocked.CompareExchange(ref l.Mailbox, 0, m) == m)
        {
            Interlocked.Decrement(ref _pending);              // retracted: the thread was leaving
            return false;
        }
        return true;
    }

    /// <summary>Claims the range offered to <paramref name="lane"/>, leaving its mailbox busy: no further offer can
    /// land, and wait behind this range, before <see cref="Finish"/>.</summary>
    internal bool TryTake(int lane, out int lo, out int hi)
    {
        ref var l = ref _lanes[lane];
        var m = Volatile.Read(ref l.Mailbox);
        if (m is 0 or Busy || Interlocked.CompareExchange(ref l.Mailbox, Busy, m) != m)
        {
            lo = hi = 0;
            return false;
        }
        SetIdle(lane, false);
        ReleaseBeater(lane);                                     // busy now: another idle thread keeps the beat
        lo = (int)(m >> 32);
        hi = unchecked(lo + (int)(uint)m);
        return true;
    }

    /// <summary>Completes a claimed range: the mailbox opens again (no offer lands before the lane is idle) and the
    /// pending count drops.</summary>
    internal void Finish(int lane)
    {
        Volatile.Write(ref _lanes[lane].Mailbox, 0);
        Interlocked.Decrement(ref _pending);                      // full fence: publishes the range's writes
    }

    private bool TryClaim(int lane)
    {
        if (!TryTake(lane, out var lo, out var hi)) return false;
        try
        {
            if (Volatile.Read(ref _fault) is null) _runner!.Process(this, lane, lo, hi);
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _fault, ex, null);
        }
        finally
        {
            Finish(lane);
        }
        return true;
    }

    /// <summary>Owner-only transitions of a lane's idle flag, mirrored in the idle count (full fences).</summary>
    internal void SetIdle(int lane, bool idle)
    {
        ref var l = ref _lanes[lane];
        if ((l.Idle != 0) == idle) return;
        Interlocked.Exchange(ref l.Idle, idle ? 1 : 0);
        if (idle) Interlocked.Increment(ref _idleLanes);
        else Interlocked.Decrement(ref _idleLanes);
    }

    internal int Pending => Volatile.Read(ref _pending);

    internal int Parked => Volatile.Read(ref _parked);

    /// <summary>An idle lane is the heartbeat: moves the beat on once a heartbeat has passed. The beat is claimed with
    /// CAS on its time: idle threads seeing the same heartbeat pass once all moved it on, splitting more often than one
    /// a heartbeat.</summary>
    private long Beat()
    {
        long now = Now(), last = Volatile.Read(ref _clock.LastBeat);
        if (now - last >= _beatTicks && Interlocked.CompareExchange(ref _clock.LastBeat, now, last) == last)
            Interlocked.Increment(ref _clock.Beat);
        return now;
    }

    private void WorkerLoop(int lane)
    {
        var idleSince = Now();
        var clock = new IdleClock();                                     // in a loop: the beat duty
        var inLoop = false;
        while (!_stopping)
        {
            if (_park && Volatile.Read(ref _phase) == 0)            // between loops (spinning workers stay idle threads)
            {
                if (inLoop)
                {
                    inLoop = false;
                    ReleaseBeater(lane);
                    idleSince = Now();                                   // a watcher read no clock during the loop
                }
                SetIdle(lane, false);                                    // fenced before the mailbox check
                if (TryClaim(lane))
                {
                    idleSince = Now();
                    continue;
                }
                if (Now() - idleSince > _spinBeforeParkTicks)
                {
                    Interlocked.Increment(ref _parked);                  // full fence before re-reading the phase
                    if (Volatile.Read(ref _phase) == 0 && !_stopping) _wake.Wait();
                    Interlocked.Decrement(ref _parked);
                    idleSince = Now();
                }
                else Thread.SpinWait(16);
                continue;
            }
            inLoop = true;
            SetIdle(lane, true);
            if (TryClaim(lane))                                          // the passes around the range are not idle ones:
            {                                                            // the next read only restarts the timing
                clock.Timed = false;
                continue;
            }
            IdleBeat(lane, ref clock);
            Thread.SpinWait(4);
        }
        SetIdle(lane, false);                                            // a stopped worker takes no offer
        ReleaseBeater(lane);
    }

    /// <summary>Stops the workers and returns once every worker loop has returned: no worker touches the scheduler
    /// afterwards. The threads themselves are their starter's (<see cref="HeartbeatOptions.StartWorker"/>). A later
    /// <c>For</c> throws <see cref="ObjectDisposedException"/>. Must not race with <c>For</c>.</summary>
    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        Interlocked.MemoryBarrier();                             // before reading the running count (Dekker with RunWorker)
        if (_workers > 0) _wake.Release(_workers);
        var spin = new SpinWait();                               // not a hot path: yielding is right here
        while (Volatile.Read(ref _runningWorkers) != 0) spin.SpinOnce();
        _wake.Dispose();
    }
}
