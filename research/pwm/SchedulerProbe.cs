using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

internal static class SchedulerProbe
{
    private static object Run(int periodUs, string policy, double durationSeconds)
    {
        // Preallocated storage; the timed loop has no I/O, UI, driver calls,
        // allocations, or changes to system timer resolution/priority.
        int capacity = (int)(durationSeconds * 1000000 / periodUs) + 100;
        double[] late = new double[capacity];
        double[] intervals = new double[capacity];
        double ticksPerUs = Stopwatch.Frequency / 1000000.0;
        double periodTicks = periodUs * ticksPerUs;
        long start = Stopwatch.GetTimestamp();
        long stop = start + (long)(durationSeconds * Stopwatch.Frequency);
        long index = 1;
        long skipped = 0;
        long previous = start;
        int count = 0;
        while (count < capacity)
        {
            long deadline = start + (long)Math.Round(index * periodTicks);
            if (deadline >= stop) break;
            long now = Stopwatch.GetTimestamp();
            while (now < deadline)
            {
                if (policy == "sleep-1ms") Thread.Sleep(1);
                else Thread.SpinWait(8);
                now = Stopwatch.GetTimestamp();
            }
            if (now >= stop) break;
            late[count] = (now - deadline) / ticksPerUs;
            intervals[count] = (now - previous) / ticksPerUs;
            previous = now;
            count++;
            // A late producer skips expired slots instead of issuing a burst.
            long next = Math.Max(index + 1, (long)Math.Floor((now - start) / periodTicks) + 1);
            skipped += next - index - 1;
            index = next;
        }
        long expectedSlots = (long)Math.Ceiling(durationSeconds * 1000000 / periodUs) - 1;
        if (index <= expectedSlots) skipped += expectedSlots - index + 1;
        Array.Resize(ref late, count);
        Array.Resize(ref intervals, count);
        Array.Sort(late);
        Array.Sort(intervals);
        return new Dictionary<string, object> {
            {"policy", policy}, {"periodUs", periodUs}, {"requestedHz", 1000000.0 / periodUs},
            {"durationSeconds", durationSeconds}, {"observedSlots", count}, {"expectedSlots", expectedSlots},
            {"skippedSlots", skipped}, {"lateP50Us", Quantile(late, .50)},
            {"lateP95Us", Quantile(late, .95)}, {"lateP99Us", Quantile(late, .99)},
            {"lateMaxUs", Quantile(late, 1)}, {"intervalP50Us", Quantile(intervals, .50)},
            {"intervalP99Us", Quantile(intervals, .99)}, {"intervalMaxUs", Quantile(intervals, 1)}
        };
    }

    private static double Quantile(double[] values, double p)
    {
        if (values.Length == 0) return 0;
        int i = (int)Math.Ceiling(p * values.Length) - 1;
        return values[Math.Max(0, Math.Min(values.Length - 1, i))];
    }

    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        Run(50, "spin", .05); // JIT warmup is excluded.
        var scenarios = new List<object>();
        foreach (int period in new[] {50, 1000})
            foreach (string policy in new[] {"spin", "sleep-1ms"})
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                scenarios.Add(Run(period, policy, 1));
            }
        var result = new Dictionary<string, object> {
            {"experiment", "Userspace deadline producer only, no I/O"},
            {"timestampUtc", DateTime.UtcNow.ToString("o")},
            {"stopwatchFrequencyHz", Stopwatch.Frequency},
            {"stopwatchIsHighResolution", Stopwatch.IsHighResolution},
            {"threadPriority", Thread.CurrentThread.Priority.ToString()},
            {"globalTimerResolutionChanged", false}, {"hardwareOutput", false},
            {"dllOrDriverLoaded", false}, {"scenarios", scenarios},
            {"interpretation", "Single short observation on this host. Measures managed producer dispatch only, not InpOut latency, pin waveform, acoustic output, or a worst-case guarantee."}
        };
        string json = new JavaScriptSerializer().Serialize(result);
        File.WriteAllText(args[0], json);
        Console.WriteLine(json);
        return 0;
    }
}
