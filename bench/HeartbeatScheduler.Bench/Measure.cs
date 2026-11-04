using System.Diagnostics;

/// <summary>One contender's timing: the best time per call, and what its calls allocated (on every thread, the
/// scheduler's workers and the thread pool's included) and the collections they caused.</summary>
readonly record struct Measurement(double Nanoseconds, double BytesPerCall, double Gen0PerThousand, double Gen1PerThousand, double Gen2PerThousand)
{
    public string Bytes => BytesPerCall switch
    {
        < 0.5 => "0 B",
        < 1024 => $"{BytesPerCall:F0} B",
        < 1024 * 1024 => $"{BytesPerCall / 1024:F1} KB",
        _ => $"{BytesPerCall / (1024 * 1024):F1} MB",
    };

    /// <summary>Collections per 1,000 calls, gen0/gen1/gen2; "0" when there were none.</summary>
    public string Collections => Gen0PerThousand + Gen1PerThousand + Gen2PerThousand == 0
        ? "0"
        : $"{Gen0PerThousand:0.#}/{Gen1PerThousand:0.#}/{Gen2PerThousand:0.#}";
}

static class Measure
{
    /// <summary>Warms <paramref name="body"/> up for <paramref name="warmMilliseconds"/>, then times 5 rounds of about
    /// <paramref name="roundMilliseconds"/> each: the best round's time per call, and the allocations and collections of
    /// all five.</summary>
    public static Measurement Time(Action body, int warmMilliseconds = 400, double roundMilliseconds = 60)
    {
        Thread.Sleep(50);                                        // the last contender's idle threads park first
        var warm = Stopwatch.StartNew();
        var calls = 0;
        while (warm.ElapsedMilliseconds < warmMilliseconds) { body(); calls++; }
        var reps = Math.Max(1, (int)(calls * roundMilliseconds / warm.ElapsedMilliseconds));
        var best = double.MaxValue;
        long bytes0 = GC.GetTotalAllocatedBytes(precise: true);
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        for (var t = 0; t < 5; t++)
        {
            var start = Stopwatch.GetTimestamp();                // not Stopwatch.StartNew(): its object would count
            for (var i = 0; i < reps; i++) body();
            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalNanoseconds / reps);
        }
        double total = 5.0 * reps;
        // Collections count every generation they reach: a gen1 collection also counts as a gen0 one, and so on.
        int g2 = GC.CollectionCount(2) - gen2, g1 = GC.CollectionCount(1) - gen1 - g2, g0 = GC.CollectionCount(0) - gen0 - g1 - g2;
        return new(best, (GC.GetTotalAllocatedBytes(precise: true) - bytes0) / total,
                   1000 * g0 / total, 1000 * g1 / total, 1000 * g2 / total);
    }
}
