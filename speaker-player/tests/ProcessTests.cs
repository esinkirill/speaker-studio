using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using SpeakerPlayer;

internal static class ProcessTests
{
    private static int assertions;
    private static string fixtureDirectory;
    private static string fixtureExe;
    private static long cancellationMs;
    private static int parentPid, childPid;
    private static readonly List<string> checks = new List<string>();

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--child-", StringComparison.Ordinal))
            return RunChild(args);
        try
        {
            string reportDirectory = args.Length == 0 ? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) : Path.GetFullPath(args[0]);
            Directory.CreateDirectory(reportDirectory);
            fixtureDirectory = Path.Combine(reportDirectory, "process fixtures пробел " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureDirectory);
            fixtureExe = Path.Combine(fixtureDirectory, "Process Child тест.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, fixtureExe);
            CancelBeforeStart();
            DisposeBeforeStart();
            QuotingAndFreshPreparation();
            CancelProcessTree();
            string report = "{\n  \"timestampUtc\": \"" + DateTime.UtcNow.ToString("o") + "\",\n" +
                "  \"passed\": true,\n  \"assertions\": " + assertions + ",\n  \"hardwareCalled\": false,\n" +
                "  \"fixtureDirectory\": \"" + Json(fixtureDirectory) + "\",\n" +
                "  \"cancelledParentPid\": " + parentPid + ",\n  \"cancelledChildPid\": " + childPid + ",\n" +
                "  \"cancelElapsedMs\": " + cancellationMs + ",\n  \"checks\": [\n    \"" +
                String.Join("\",\n    \"", checks.ToArray()) + "\"\n  ]\n}\n";
            File.WriteAllText(Path.Combine(reportDirectory, "process-check.json"), report, new UTF8Encoding(false));
            Console.WriteLine("PASS: " + assertions + " process assertions; cancellation " + cancellationMs + " ms; no audio or driver invoked.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int RunChild(string[] args)
    {
        if (args[0] == "--child-echo")
        {
            string[] echoed = new string[args.Length - 2];
            for (int i = 2; i < args.Length; i++) echoed[i - 2] = Convert.ToBase64String(Encoding.UTF8.GetBytes(args[i]));
            File.WriteAllLines(args[1], echoed, new UTF8Encoding(false));
            Console.WriteLine("STDOUT echo complete");
            Console.Error.WriteLine("STDERR echo complete");
            return 0;
        }
        if (args[0] == "--child-sleep")
        {
            File.WriteAllText(args[1], Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
            Thread.Sleep(30000);
            return 0;
        }
        if (args[0] == "--child-tree")
        {
            string baseMarker = args[1];
            ProcessStartInfo start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                ChildProcessRunner.Quote("--child-sleep") + " " + ChildProcessRunner.Quote(baseMarker + ".child"));
            start.UseShellExecute = false; start.CreateNoWindow = true;
            using (Process child = Process.Start(start))
            {
                File.WriteAllText(baseMarker + ".parent", Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                Thread.Sleep(30000);
            }
            return 0;
        }
        throw new ArgumentException("Unknown harmless child mode");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
    }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        bool thrown = false;
        try { action(); } catch (T) { thrown = true; }
        Assert(thrown, message);
    }
    private static void CancelBeforeStart()
    {
        string marker = Path.Combine(fixtureDirectory, "cancelled-before-run.txt");
        using (ChildProcessRunner runner = new ChildProcessRunner())
        {
            runner.Prepare(); runner.Cancel();
            Throws<OperationCanceledException>(delegate {
                runner.Run(fixtureExe, new[] { "--child-echo", marker, "must not appear" }, fixtureDirectory, delegate { });
            }, "Cancelled prepared operation must reject child start");
            Assert(!runner.Started && !File.Exists(marker), "Cancelled queued operation must not produce marker");
        }
        checks.Add("Prepare-Cancel-Run: no process started, marker absent");
    }
    private static void DisposeBeforeStart()
    {
        string marker = Path.Combine(fixtureDirectory, "disposed-before-run.txt");
        ChildProcessRunner runner = new ChildProcessRunner();
        runner.Prepare(); runner.Dispose();
        Throws<ObjectDisposedException>(delegate {
            runner.Run(fixtureExe, new[] { "--child-echo", marker, "must not appear" }, fixtureDirectory, delegate { });
        }, "Disposed operation must reject child start");
        Throws<ObjectDisposedException>(delegate { runner.Prepare(); }, "Disposed operation must reject preparation");
        Assert(!runner.Started && !File.Exists(marker), "Disposed operation must not produce marker");
        checks.Add("Dispose-Run: no process started, marker absent");
    }
    private static void QuotingAndFreshPreparation()
    {
        string marker = Path.Combine(fixtureDirectory, "echo arguments тест.txt");
        string[] values = { "plain", "two words", "", "путь с пробелом", @"C:\folder with spaces\trailing\", @"many\\slashes", "a\"quoted\" value", @"\\server\share\" };
        string[] args = new string[values.Length + 2]; args[0] = "--child-echo"; args[1] = marker;
        Array.Copy(values, 0, args, 2, values.Length);
        List<string> logs = new List<string>();
        using (ChildProcessRunner runner = new ChildProcessRunner())
        {
            runner.Prepare(); runner.Cancel(); runner.Prepare();
            runner.Run(fixtureExe, args, fixtureDirectory, delegate(string message) { lock (logs) logs.Add(message); });
            Assert(runner.Started && File.Exists(marker), "Fresh Prepare after cancellation must start child");
            string[] actual = File.ReadAllLines(marker, Encoding.UTF8);
            Assert(actual.Length == values.Length, "Argument count must be preserved");
            for (int i = 0; i < values.Length; i++)
                Assert(Encoding.UTF8.GetString(Convert.FromBase64String(actual[i])) == values[i], "Argument " + i + " was altered");
            lock (logs)
            {
                Assert(logs.Contains("STDOUT echo complete"), "Standard output must drain before Run returns");
                Assert(logs.Contains("STDERR echo complete"), "Standard error must drain before Run returns");
            }
        }
        checks.Add("Fresh Prepare succeeds; spaces, Unicode, empty argv, quotes and trailing backslashes preserved");
        checks.Add("Both stdout and stderr delivered before Run returns");
    }
    private static void CancelProcessTree()
    {
        string marker = Path.Combine(fixtureDirectory, "sleep tree тест");
        using (ChildProcessRunner runner = new ChildProcessRunner())
        {
            Exception failure = null;
            runner.Prepare();
            Thread worker = new Thread(delegate() {
                try { runner.Run(fixtureExe, new[] { "--child-tree", marker }, fixtureDirectory, delegate { }); }
                catch (Exception ex) { failure = ex; }
            });
            worker.IsBackground = true; worker.Start();
            try
            {
                Stopwatch startup = Stopwatch.StartNew();
                while ((!File.Exists(marker + ".parent") || !File.Exists(marker + ".child")) && startup.ElapsedMilliseconds < 5000)
                    Thread.Sleep(10);
                Assert(File.Exists(marker + ".parent") && File.Exists(marker + ".child"), "Harmless sleep process tree did not start");
                parentPid = ReadPid(marker + ".parent"); childPid = ReadPid(marker + ".child");
                Assert(parentPid != childPid && Alive(parentPid) && Alive(childPid), "Both test process generations must be alive");
                Stopwatch cancel = Stopwatch.StartNew();
                runner.Cancel();
                Assert(worker.Join(2500), "Active cancellation must release Run promptly");
                cancellationMs = cancel.ElapsedMilliseconds;
                Assert(failure is OperationCanceledException, "Killed operation must report cancellation");
                Stopwatch shutdown = Stopwatch.StartNew();
                while ((Alive(parentPid) || Alive(childPid)) && shutdown.ElapsedMilliseconds < 1000) Thread.Sleep(10);
                Assert(!Alive(parentPid) && !Alive(childPid), "Taskkill must stop only the entire spawned fixture tree");
                Assert(cancellationMs < 2500, "Cancellation must not wait through 30-second sleep");
            }
            finally { runner.Cancel(); worker.Join(3000); }
        }
        checks.Add("Active Cancel terminates harmless 30-second parent-child sleep tree promptly");
    }
    private static int ReadPid(string path)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (true)
        {
            int pid;
            if (Int32.TryParse(File.ReadAllText(path), out pid)) return pid;
            if (timer.ElapsedMilliseconds > 1000) throw new Exception("Incomplete PID marker");
            Thread.Sleep(10);
        }
    }
    private static bool Alive(int pid)
    {
        try { using (Process process = Process.GetProcessById(pid)) return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static string Json(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
