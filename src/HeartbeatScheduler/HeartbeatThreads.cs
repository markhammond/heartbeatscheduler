using System.Runtime.InteropServices;

namespace HeartbeatScheduling;

/// <summary>
/// Threads for a <see cref="HeartbeatScheduler"/>'s workers: ways to start them (values for
/// <see cref="HeartbeatOptions.StartWorker"/>), and the settings a host may give its own threads. Thread creation and
/// placement live here, apart from scheduling.
/// </summary>
public static class HeartbeatThreads
{
    /// <summary>
    /// A background thread per worker, named <paramref name="namePrefix"/>-1, -2, ..., given <paramref name="quality"/>
    /// before it runs the worker's loop. The default <see cref="HeartbeatOptions.StartWorker"/>.
    /// </summary>
    /// <param name="namePrefix">Thread name prefix.</param>
    /// <param name="quality">How the operating system schedules the threads (see <see cref="SetCurrentQuality"/>).</param>
    public static Action<int, Action> Background(string namePrefix = "Heartbeat", HeartbeatThreadQuality quality = HeartbeatThreadQuality.Default)
    {
        ArgumentNullException.ThrowIfNull(namePrefix);
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        return (lane, run) => new Thread(() =>
        {
            if (quality != HeartbeatThreadQuality.Default) SetCurrentQuality(quality);
            run();
        }) { Name = $"{namePrefix}-{lane}", IsBackground = true }.Start();
    }

    /// <summary>
    /// Sets the calling thread's <paramref name="quality"/>: on a host's own worker threads (in its
    /// <see cref="HeartbeatOptions.StartWorker"/>), or on the thread that calls <c>For</c> (worker 0, with a share of
    /// every loop). Returns whether the operating system applied it: false on Linux, where it has no effect.
    /// </summary>
    /// <param name="quality">The quality to give the calling thread.</param>
    public static bool SetCurrentQuality(HeartbeatThreadQuality quality)
    {
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        if (OperatingSystem.IsMacOS())
        {
            var high = quality == HeartbeatThreadQuality.High;
            if (pthread_set_qos_class_self_np(high ? QosUserInteractive : QosDefault, 0) == 0) return true;
            // Opted out of quality of service (EPERM: .NET set the thread's priority the POSIX way): the priority at the
            // base of the same band, which the kernel serves alike (user-interactive 47, default 31; SCHED_OTHER 15..47).
            var param = new SchedParam { Priority = high ? 47 : 31 };
            return pthread_setschedparam(pthread_self(), SchedOther, ref param) == 0;
        }
        if (!OperatingSystem.IsWindows()) return false;
        Thread.CurrentThread.Priority = quality == HeartbeatThreadQuality.High ? ThreadPriority.Highest : ThreadPriority.Normal;
        return true;
    }

    private const uint QosUserInteractive = 0x21, QosDefault = 0x15;
    private const int SchedOther = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SchedParam
    {
        public int Priority;
        private readonly int _opaque;
    }

    /// <summary>macOS: sets the calling thread's quality-of-service class (only a thread may set its own).</summary>
    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern nint pthread_self();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int pthread_setschedparam(nint thread, int policy, ref SchedParam param);
}
