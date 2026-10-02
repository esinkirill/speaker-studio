using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SpeakerPlayer
{
    public sealed class ChildProcessRunner : IDisposable
    {
        private readonly object sync = new object();
        private Process child;
        private bool cancelled;
        private bool disposed;
        public bool Started { get; private set; }
        public void Prepare()
        {
            lock (sync) {
                if (disposed) throw new ObjectDisposedException("ChildProcessRunner");
                if (child != null) throw new InvalidOperationException("Операция уже запущена.");
                cancelled = false; Started = false;
            }
        }
        public void Run(string executable, string[] arguments, string directory, Action<string> log)
        {
            StringBuilder args = new StringBuilder();
            foreach (string arg in arguments) { if (args.Length > 0) args.Append(' '); args.Append(Quote(arg)); }
            Process process = new Process { StartInfo = new ProcessStartInfo(executable, args.ToString()) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) log(e.Data); };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) log(e.Data); };
            try {
                lock (sync) {
                    if (disposed) throw new ObjectDisposedException("ChildProcessRunner");
                    if (cancelled) throw new OperationCanceledException("Операция отменена.");
                    process.Start(); child = process; Started = true;
                }
                process.BeginOutputReadLine(); process.BeginErrorReadLine(); process.WaitForExit();
                if (cancelled) throw new OperationCanceledException("Операция отменена.");
                if (process.ExitCode != 0) throw new InvalidOperationException("Процесс завершился с кодом " + process.ExitCode + ". Подробности в журнале.");
            } finally { lock (sync) { if (child == process) child = null; } process.Dispose(); }
        }
        public void Cancel()
        {
            lock (sync) {
                cancelled = true;
                if (child == null || child.HasExited) return;
                using (Process killer = Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/PID " + child.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })) {
                    if (killer != null) killer.WaitForExit(2000);
                }
            }
        }
        public static string Quote(string value)
        {
            StringBuilder quoted = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value) {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { quoted.Append('\\', slashes * 2 + 1); quoted.Append(c); }
                else { quoted.Append('\\', slashes); quoted.Append(c); }
                slashes = 0;
            }
            quoted.Append('\\', slashes * 2); quoted.Append('"'); return quoted.ToString();
        }
        public void Dispose() { Cancel(); lock (sync) { disposed = true; } }
    }
}
