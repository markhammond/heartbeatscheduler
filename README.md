# HeartbeatScheduler — nanosecond task scheduling without the heart pangs

[![NuGet](https://img.shields.io/nuget/v/HeartbeatScheduler.svg)](https://www.nuget.org/packages/HeartbeatScheduler) [![CI](https://github.com/markhammond/heartbeatscheduler/actions/workflows/ci.yml/badge.svg)](https://github.com/markhammond/heartbeatscheduler/actions/workflows/ci.yml)

**Parallelizing micro-tasks is particularly troublesome. Conventional task schedulers are dominated by scheduling and context-switching overhead, which harms, rather than helps, performance at that scale.**

When the work itself takes nanoseconds, the microseconds spent dispatching tasks and waking threads outweigh anything the extra cores win back: in the [Performance](#performance) tables below, `Parallel.For` runs short loops at 0.1x to 0.7x of a single thread's speed.

Heartbeat scheduling for parallel loops in .NET: a loop runs sequentially, and at most once per heartbeat (5 µs by default) each thread splits off half its remaining work for an idle worker. Parallelism thus costs a bounded fraction of run time however short the iterations, and a loop shorter than a heartbeat stays a plain loop on the calling thread. Allocation-free per loop.

## Usage

Install [HeartbeatScheduler from NuGet](https://www.nuget.org/packages/HeartbeatScheduler):

```bash
dotnet add package HeartbeatScheduler
```

```csharp
using HeartbeatScheduling;

using var scheduler = new HeartbeatScheduler(new HeartbeatOptions { Workers = 3 });
scheduler.For(0, items.Length, new Calculator(items));   // items: Action[]

sealed class Calculator(Action[] items) : IHeartbeatLoop
{
    public void Execute(int index, int worker) => items[index].Invoke();
}
```

For iterations of a few nanoseconds, a batched loop (`IHeartbeatBatchLoop`) hands each thread contiguous batches, so the inner loop stays tight and the heartbeat is checked between batches. Given an estimate of the mean cost per iteration (in microseconds with `ForMicroseconds`, of any precision, or as a `TimeSpan` for costs of 100 ns or more), a loop predicted to take less than a heartbeat runs as a plain loop on the calling thread, involving no worker, and one predicted to take more is split up front, as soon as a worker is idle, without waiting for a beat. Splits are then guarded by work rather than a count: each half keeps a quarter of a heartbeat of predicted work, so a few long iterations still spread across threads.

A struct body (`scheduler.For(0, n, new PricerStruct(...))`) is inlined into the scheduling loop, with no interface call per iteration; each range runs on its own copy, so keep results in the arrays it references. Once its own share is done, the calling thread takes work from busy workers until the loop completes. Idle workers spin, then park between loops; with `IdlePolicy = HeartbeatIdlePolicy.BusySpin` they never park, so a loop starts without signalling anyone, at the cost of a busy core per worker. If an iteration throws, the other threads abandon their ranges at their next beat, and the first exception is rethrown after the join.

The host can own the worker threads: `StartWorker` hands it each worker's loop to run on a thread of its choosing, set up as it wishes (priority, CPU affinity, quality of service), for example by posting it to one of its event loops:

```csharp
using var scheduler = new HeartbeatScheduler(new HeartbeatOptions
{
    Workers = loops.Length,
    StartWorker = (lane, run) => loops[lane - 1].Post(run),   // holds that loop's thread until the scheduler is disposed
});
```

`HeartbeatThreads` holds the thread side: `HeartbeatThreads.Background(namePrefix, quality)`, the default (a background thread per worker), and `HeartbeatThreads.SetCurrentQuality(HeartbeatThreadQuality.High)`, which raises the calling thread, whether a worker's or the host's own (on macOS, the user-interactive band; on Windows, the highest thread priority).

## Performance

Against `Parallel.For`, fixed-size chunks (`Parallel.ForEach` over a `Partitioner`), CommunityToolkit's `ParallelHelper` (a struct body, the range split up front into a piece per core) and ZeroAllocJobScheduler's `IJobParallelFor`. Each loop is a map, `Out[i] = Body(i)`, with no shared writes; the costs shown are the plain loop's, measured. Every contender is held to six cores (`DOTNET_PROCESSOR_COUNT=6`, and `Parallel.For` capped by `MaxDegreeOfParallelism`), and the heartbeat scheduler's workers busy-wait (`IdlePolicy = HeartbeatIdlePolicy.BusySpin`). Measured on .NET 10 with the repository's own harness ([bench/HeartbeatScheduler.Bench](https://github.com/markhammond/heartbeatscheduler/tree/main/bench/HeartbeatScheduler.Bench)): the best of five timed rounds, after a warm-up. Figures vary by about ±5% between runs. The heartbeat columns use a struct body, batched, without and with the mean iteration cost as an estimate. Each cell is the speed-up over a plain loop on one thread, so below 1.0x is slower than not parallelizing at all.

Flat loops:

| Loop | `Parallel.For` | Chunks | `ParallelHelper` | ZeroAllocJobScheduler | HeartbeatScheduler | HeartbeatScheduler, estimate |
|---|---|---|---|---|---|---|
| 1,000 × ~0.5 ns | 0.1x | 0.2x | 0.2x | 0.0x | 0.5x | **0.8x** |
| 100,000 × ~0.5 ns | 1.2x | 3.2x | 3.2x | 1.0x | 2.6x | **4.1x** |
| 4,000,000 × ~0.5 ns | 2.3x | **5.5x** | **5.5x** | 2.0x | 5.2x | 5.3x |
| 100 × ~6 ns | 0.2x | 0.2x | 0.2x | 0.0x | **0.8x** | **0.8x** |
| 1,000 × ~6 ns | 0.6x | 0.9x | 0.9x | 0.2x | 1.1x | **1.6x** |
| 100,000 × ~6 ns | 4.7x | 4.8x | **5.3x** | 2.5x | 4.8x | 5.1x |
| 16 × ~150 ns | 0.4x | 0.5x | 0.5x | 0.1x | 0.9x | **1.0x** |
| 100 × ~150 ns | 1.5x | 1.6x | 1.7x | 0.5x | 1.4x | **3.0x** |
| 10,000 × ~150 ns | 5.5x | 5.6x | **5.7x** | 3.0x | 5.5x | **5.7x** |
| 1,000, ramp from ~0.5 to ~30 ns | 0.8x | 1.0x | 1.0x | 0.3x | 1.1x | **1.3x** |
| 100,000, ramp from ~0.5 to ~30 ns | 4.1x | 2.7x | 2.5x | 2.7x | 4.7x | **5.1x** |
| 1,000, first 1/16 at ~150 ns, rest ~0.5 ns | 0.9x | 0.9x | 0.9x | 0.4x | **1.0x** | **1.0x** |
| 100,000, first 1/16 at ~150 ns, rest ~0.5 ns | 4.7x | 1.0x | 1.0x | 3.3x | 4.9x | **5.2x** |

Level-by-level evaluation of a leveled graph (each level one loop, the join the barrier before the next), about 110,000 nodes in all, against evaluating it on one thread. The heartbeat columns use a struct body, without and with the mean node cost as an estimate. ZeroAllocJobScheduler's idle workers block rather than spin, so each level waits for them to wake.

| Graph | Node cost | `Parallel.For` | Chunks | `ParallelHelper` | ZeroAllocJobScheduler | HeartbeatScheduler | HeartbeatScheduler, estimate |
|---|---|---|---|---|---|---|---|
| 300 levels, 1 to 3,000 nodes wide | ~1.5 µs | 4.8x | 4.9x | 5.0x | 1.3x | 4.8x | **5.3x** |
| | ~100 ns | 3.1x | 3.1x | 3.1x | 1.5x | 3.1x | **4.1x** |
| | ~10 ns to ~1.5 µs | 4.2x | 3.9x | 3.9x | 1.0x | 4.0x | **4.7x** |
| 3,000 levels, 1 to 200 nodes wide | ~1.5 µs | 3.3x | 3.3x | 3.3x | 0.2x | 2.5x | **4.1x** |
| | ~100 ns | 0.7x | 0.8x | 0.8x | 0.2x | 1.2x | **1.7x** |
| | ~10 ns to ~1.5 µs | 1.5x | 1.5x | 1.5x | 0.3x | 1.6x | **2.4x** |

Allocation, counted on every thread (the workers' and the thread pool's included):

| | `Parallel.For` | Chunks | `ParallelHelper` | ZeroAllocJobScheduler | HeartbeatScheduler |
|---|---|---|---|---|---|
| Per flat loop | 2.2 to 2.7 KB | 2.5 to 9.1 KB | 2.2 to 2.7 KB | 0 B | 0 B |
| Per evaluation, 300 levels | 0.7 MB | 0.9 to 1.0 MB | 0.7 MB | 0 B | 0 B |
| Per evaluation, 3,000 levels | 6.5 to 7.1 MB | 8.2 to 9.5 MB | 6.2 to 6.7 MB | 0 B | 0 B |
| Gen0 collections, per evaluation of 3,000 levels | about 0.8 | about 1 | about 0.8 | none | none |

## Build

```bash
dotnet test HeartbeatScheduler.slnx
```

```bash
dotnet run -c Release --project bench/HeartbeatScheduler.Bench
```

## Credits

Based on the heartbeat scheduling technique of Acar et al. (PLDI 2018), for flat loops: one loop at a time per scheduler, and a loop body must not start another.

## License

MIT. See [LICENSE](https://github.com/markhammond/heartbeatscheduler/blob/main/LICENSE).
