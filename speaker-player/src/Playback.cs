using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpeakerPlayer
{
    public enum OutputMode { Speaker, Preview, Visual }

    public sealed class PlaybackSettings
    {
        public int Transpose = 0;
        public double Speed = 1.0;
        public int NoteGapMs = 0;
        public bool Loop = false;
        public OutputMode Output = OutputMode.Speaker;
        public RhythmSettings Rhythm = new RhythmSettings();
        public long StartPositionMs = 0;
    }

    public sealed class PlaybackProgress
    {
        // RowIndex is zero based. PositionMs counts active playback, excluding a user pause.
        public int RowIndex, RowCount, Frequency, DurationMs, PauseMs;
        public long PositionMs, TotalMs;
        public long SessionId;
        public long PositionRevision;
        public bool IsPause;
    }

    public sealed class PlaybackEngine : IDisposable
    {
        private sealed class PreparedRow
        {
            public int Frequency, Duration, Pause;
        }

        private sealed class PreparedSegment
        {
            public int RowIndex, Frequency;
            public long Start, End;
            public bool IsPause;
        }

        private sealed class Session
        {
            public readonly AutoResetEvent Changed = new AutoResetEvent(false);
            public readonly ActiveClock Clock = new ActiveClock();
            public volatile bool Cancelled;
            public Thread Thread;
            public PreparedRow[] Rows;
            public PreparedSegment[] Segments;
            public PlaybackSettings Settings;
            public string BaseDirectory;
            public long Total;
            public long Id;
            public long PositionRevision;
        }

        private sealed class ActiveClock
        {
            private readonly object gate = new object();
            private long origin, pauseStarted, excludedTicks;
            private bool started, paused;
            private long offsetMs;

            public void Seek(long positionMs)
            {
                lock (gate)
                {
                    offsetMs = positionMs;
                    origin = Stopwatch.GetTimestamp();
                    excludedTicks = 0;
                    pauseStarted = origin;
                }
            }

            public void Start()
            {
                lock (gate)
                {
                    origin = Stopwatch.GetTimestamp();
                    excludedTicks = 0;
                    pauseStarted = origin;
                    started = true;
                }
            }

            public void Pause()
            {
                lock (gate)
                {
                    if (paused) return;
                    paused = true;
                    pauseStarted = Stopwatch.GetTimestamp();
                }
            }

            public void Resume()
            {
                lock (gate)
                {
                    if (!paused) return;
                    if (started) excludedTicks += Stopwatch.GetTimestamp() - pauseStarted;
                    paused = false;
                }
            }

            public double ElapsedMilliseconds(out bool isPaused)
            {
                lock (gate)
                {
                    isPaused = paused;
                    if (!started) return offsetMs;
                    long now = paused ? pauseStarted : Stopwatch.GetTimestamp();
                    return offsetMs + (now - origin - excludedTicks) * 1000.0 / Stopwatch.Frequency;
                }
            }
        }

        private readonly object sync = new object();
        private Session current;
        private bool disposed;
        private long lastSessionId;
        private long lastPositionRevision;

        public event Action<PlaybackProgress> Progress;
        // Events arrive on the playback thread. A UI should marshal them with BeginInvoke.
        public event Action<string> Completed;
        public event Action<long, string> SessionCompleted;

        public long LastSessionId
        {
            get { lock (sync) return lastSessionId; }
        }

        public long PositionRevision
        {
            get { lock (sync) return lastPositionRevision; }
        }

        public bool IsPlaying
        {
            get { lock (sync) return current != null; }
        }

        public void Play(IList<ToneRow> notes, PlaybackSettings settings, string baseDirectory)
        {
            if (notes == null) throw new ArgumentNullException("notes");
            if (settings == null) throw new ArgumentNullException("settings");
            if (!Enum.IsDefined(typeof(OutputMode), settings.Output))
                throw new ArgumentException("Неизвестный выход воспроизведения.", "settings");

            // Copy settings and rows: changing controls or an editor cannot mutate a running track.
            Session session = new Session();
            try
            {
                session.Settings = new PlaybackSettings {
                    Transpose = settings.Transpose, Speed = settings.Speed,
                    NoteGapMs = settings.NoteGapMs, Loop = settings.Loop, Output = settings.Output,
                    Rhythm = settings.Rhythm == null ? new RhythmSettings() : settings.Rhythm.Copy(),
                    StartPositionMs = settings.StartPositionMs
                };
                session.BaseDirectory = baseDirectory;
                List<ToneRow> transformed = SequenceTiming.Transform(notes, session.Settings.Transpose,
                    session.Settings.Speed, session.Settings.NoteGapMs, session.Settings.Rhythm);
                session.Rows = new PreparedRow[transformed.Count];
                List<PreparedSegment> segments = new List<PreparedSegment>();
                for (int i = 0; i < transformed.Count; i++)
                {
                    ToneRow row = transformed[i];
                    session.Rows[i] = new PreparedRow { Frequency = row.Frequency,
                        Duration = row.DurationMs, Pause = row.PauseMs };
                    if (row.DurationMs > 0) segments.Add(new PreparedSegment {
                        RowIndex = i, Frequency = row.Frequency, Start = session.Total,
                        End = checked(session.Total + row.DurationMs), IsPause = row.Frequency == 0
                    });
                    if (row.PauseMs > 0) segments.Add(new PreparedSegment {
                        RowIndex = i, Frequency = 0, Start = checked(session.Total + row.DurationMs),
                        End = checked(session.Total + row.DurationMs + row.PauseMs), IsPause = true
                    });
                    session.Total = checked(session.Total + row.DurationMs + row.PauseMs);
                }
                if (session.Rows.Length == 0) throw new ArgumentException("Последовательность пуста.", "notes");
                if (session.Settings.Loop && session.Total == 0)
                    throw new ArgumentException("Нельзя зациклить последовательность нулевой длительности.", "notes");
                session.Segments = segments.ToArray();
                session.Settings.StartPositionMs = Math.Max(0, Math.Min(session.Total, session.Settings.StartPositionMs));
                session.Clock.Seek(session.Settings.StartPositionMs);

                lock (sync)
                {
                    if (disposed) throw new ObjectDisposedException("PlaybackEngine");
                    if (current != null) throw new InvalidOperationException("Сначала остановите текущий трек.");
                    session.Id = ++lastSessionId;
                    session.PositionRevision = ++lastPositionRevision;
                    current = session;
                    session.Thread = new Thread(delegate() { Run(session); });
                    session.Thread.IsBackground = true;
                    session.Thread.Name = "Speaker playback";
                    try { session.Thread.Start(); }
                    catch { current = null; throw; }
                }
            }
            catch
            {
                session.Changed.Dispose();
                throw;
            }
        }

        public void Pause()
        {
            lock (sync)
            {
                if (current == null) return;
                current.Clock.Pause();
                current.Changed.Set();
            }
        }

        public void Resume()
        {
            lock (sync)
            {
                if (current == null) return;
                current.Clock.Resume();
                current.Changed.Set();
            }
        }

        public void Seek(long positionMs)
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("PlaybackEngine");
                if (current == null || current.Cancelled) return;
                current.Clock.Seek(Math.Max(0, Math.Min(current.Total, positionMs)));
                current.PositionRevision = ++lastPositionRevision;
                current.Changed.Set();
            }
        }

        private struct PlaybackState
        {
            public double Position;
            public bool Paused;
            public long Revision;
        }

        private PlaybackState ReadState(Session session)
        {
            lock (sync)
            {
                bool paused;
                double position = session.Clock.ElapsedMilliseconds(out paused);
                return new PlaybackState { Position = position, Paused = paused, Revision = session.PositionRevision };
            }
        }

        private static int FindSegment(Session session, double position)
        {
            int low = 0, high = session.Segments.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (session.Segments[middle].End <= position) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        public void Stop()
        {
            Session session;
            lock (sync)
            {
                session = current;
                if (session == null) return;
                session.Cancelled = true;
                session.Changed.Set();
            }
            // A bounded join cannot hang the UI if a native call becomes stuck.
            // BeginInvoke event handlers do not wait for the UI thread during this join.
            if (session.Thread != Thread.CurrentThread) session.Thread.Join(1000);
        }

        private void Run(Session session)
        {
            IToneOutput output = null;
            string failure = null;
            try
            {
                if (session.Cancelled) return;
                // Starting at the end must not open any audio or hardware backend.
                PlaybackState initial = ReadState(session);
                bool startAtEnd;
                lock (sync)
                {
                    startAtEnd = initial.Revision == session.PositionRevision && initial.Position >= session.Total;
                    if (startAtEnd && current == session) current = null;
                }
                if (startAtEnd) { ReportEnd(session, initial.Revision); return; }
                switch (session.Settings.Output)
                {
                    case OutputMode.Speaker:
                        string dllName = IntPtr.Size == 8 ? "inpoutx64.dll" : "inpout32.dll";
                        output = new SpeakerOutput(Path.Combine(session.BaseDirectory, dllName));
                        break;
                    case OutputMode.Preview: output = new WaveOutput(); break;
                    default: output = new SilentOutput(); break;
                }
                session.Clock.Start();
                while (!session.Cancelled)
                {
                    PlaybackState state = ReadState(session);
                    int first = FindSegment(session, state.Position);
                    bool seeked = false;
                    for (int i = first; i < session.Segments.Length && !session.Cancelled; i++)
                    {
                        PreparedSegment segment = session.Segments[i];
                        long clippedStart = i == first ? Math.Max(segment.Start,
                            Math.Min(segment.End, (long)state.Position)) : segment.Start;
                        if (!PlaySegment(session, output, segment, clippedStart, state.Revision))
                        { seeked = true; break; }
                    }
                    if (session.Cancelled) break;
                    if (seeked) continue;
                    if (first == session.Segments.Length) ReportEnd(session, state.Revision);
                    lock (sync)
                    {
                        if (session.PositionRevision != state.Revision) continue;
                        if (!session.Settings.Loop)
                        {
                            if (current == session) current = null;
                            break;
                        }
                        session.Clock.Seek(0);
                        session.PositionRevision = ++lastPositionRevision;
                    }
                }
            }
            catch (Exception ex) { failure = ex.GetBaseException().Message; }
            finally
            {
                if (output != null)
                {
                    try { output.Dispose(); }
                    catch (Exception ex) { if (failure == null) failure = ex.GetBaseException().Message; }
                }
                lock (sync)
                {
                    if (current == session) current = null;
                    session.Changed.Dispose();
                }
                Action<string> completed = Completed;
                Action<long, string> sessionCompleted = SessionCompleted;
                if (sessionCompleted != null)
                {
                    try { sessionCompleted(session.Id, failure); }
                    catch (Exception ex) { Trace.TraceError("Playback completion observer failed: " + ex); }
                }
                if (completed != null)
                {
                    // A closed UI must not turn an event callback into an unhandled thread exception.
                    try { completed(failure); }
                    catch (Exception ex) { Trace.TraceError("Playback completion observer failed: " + ex); }
                }
            }
        }

        private bool PlaySegment(Session session, IToneOutput output, PreparedSegment segment,
            long clippedStart, long revision)
        {
            bool sounding = false;
            double nextReport = clippedStart;
            PreparedRow row = session.Rows[segment.RowIndex];
            Report(session, row, segment.RowIndex, segment.Frequency, clippedStart, segment.IsPause, revision);
            try
            {
                while (!session.Cancelled)
                {
                    PlaybackState state = ReadState(session);
                    if (state.Revision != revision) return false;
                    if (state.Paused)
                    {
                        if (sounding) { output.StopTone(); sounding = false; }
                        session.Changed.WaitOne(20);
                        continue;
                    }
                    double remaining = segment.End - state.Position;
                    if (remaining <= 0) break;
                    if (!sounding && segment.Frequency > 0)
                    {
                        output.StartTone(segment.Frequency);
                        sounding = true;
                    }
                    state = ReadState(session);
                    if (state.Revision != revision) return false;
                    if (state.Paused || segment.End <= state.Position) continue;
                    remaining = segment.End - state.Position;
                    // One clock spans the track: scheduler/output/observer delays
                    // consume the remaining budget instead of lengthening every note.
                    session.Changed.WaitOne((int)Math.Max(1, Math.Min(20, Math.Ceiling(remaining))));
                    state = ReadState(session);
                    if (state.Revision != revision) return false;
                    if (!state.Paused && state.Position >= nextReport)
                    {
                        long nominalPosition = Math.Max(clippedStart, Math.Min(segment.End, (long)state.Position));
                        Report(session, row, segment.RowIndex, segment.Frequency, nominalPosition, segment.IsPause, revision);
                        nextReport = state.Position + 40;
                    }
                }
                if (!session.Cancelled)
                    Report(session, row, segment.RowIndex, segment.Frequency, segment.End, segment.IsPause, revision);
                return true;
            }
            finally { if (sounding) output.StopTone(); }
        }

        private void Report(Session session, PreparedRow row, int rowIndex,
            int frequency, long position, bool isPause, long revision)
        {
            lock (sync) if (revision != session.PositionRevision) return;
            Action<PlaybackProgress> progress = Progress;
            if (progress != null)
                progress(new PlaybackProgress {
                    RowIndex = rowIndex, RowCount = session.Rows.Length, Frequency = frequency,
                    DurationMs = row.Duration, PauseMs = row.Pause, PositionMs = position,
                    TotalMs = session.Total, IsPause = isPause, SessionId = session.Id,
                    PositionRevision = revision
                });
        }

        private void ReportEnd(Session session, long revision)
        {
            int rowIndex = session.Rows.Length - 1;
            Report(session, session.Rows[rowIndex], rowIndex, 0, session.Total, true, revision);
        }

        public void Dispose()
        {
            lock (sync) disposed = true;
            Stop();
        }
    }

    internal interface IToneOutput : IDisposable
    {
        void StartTone(int frequency);
        void StopTone();
    }

    internal sealed class SilentOutput : IToneOutput
    {
        public void StartTone(int frequency) { }
        public void StopTone() { }
        public void Dispose() { }
    }

    internal sealed class SpeakerOutput : IToneOutput
    {
        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate short ReadPort(short port);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void WritePort(short port, short value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool DriverOpen();

        private IntPtr module;
        private ReadPort read;
        private WritePort write;
        private bool playing;

        public SpeakerOutput(string path)
        {
            module = LoadLibraryEx(Path.GetFullPath(path), IntPtr.Zero, 0x00000900);
            if (module == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось загрузить InpOut DLL.");
            try
            {
                read = Export<ReadPort>("Inp32");
                write = Export<WritePort>("Out32");
                if (!Export<DriverOpen>("IsInpOutDriverOpen")())
                    throw new InvalidOperationException("InpOut driver не открылся. Запустите приложение от имени администратора.");
            }
            catch { FreeLibrary(module); module = IntPtr.Zero; throw; }
        }

        private T Export<T>(string name) where T : class
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "В InpOut отсутствует функция " + name + ".");
            return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        public void StartTone(int frequency)
        {
            int divisor = Math.Max(2, Math.Min(65536, (int)Math.Round(1193182.0 / frequency)));
            playing = true;
            write(0x43, 0xB6);
            write(0x42, (short)(divisor & 0xFF));
            write(0x42, (short)((divisor >> 8) & 0xFF));
            write(0x61, (short)((read(0x61) & 0xFC) | 3));
        }

        public void StopTone()
        {
            if (!playing) return;
            write(0x61, (short)(read(0x61) & 0xFC));
            playing = false;
        }

        public void Dispose()
        {
            if (module == IntPtr.Zero) return;
            try { StopTone(); }
            finally { FreeLibrary(module); module = IntPtr.Zero; }
        }
    }

    internal sealed class WaveOutput : IToneOutput
    {
        private const int SampleRate = 44100;
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormat
        {
            public ushort FormatTag, Channels;
            public uint SamplesPerSecond, AverageBytesPerSecond;
            public ushort BlockAlign, BitsPerSample, ExtraSize;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr Data;
            public uint BufferLength, BytesRecorded;
            public UIntPtr User;
            public uint Flags, Loops;
            public IntPtr Next;
            public UIntPtr Reserved;
        }

        [DllImport("winmm.dll")]
        private static extern uint waveOutOpen(out IntPtr handle, uint device, ref WaveFormat format,
            IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")]
        private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")]
        private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")]
        private static extern uint waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")]
        private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")]
        private static extern uint waveOutClose(IntPtr handle);

        private IntPtr handle;
        private IntPtr header;
        private GCHandle samples;
        private bool prepared;
        private readonly uint headerSize = (uint)Marshal.SizeOf(typeof(WaveHeader));

        public WaveOutput()
        {
            WaveFormat format = new WaveFormat {
                FormatTag = 1, Channels = 1, SamplesPerSecond = SampleRate,
                AverageBytesPerSecond = SampleRate * 2, BlockAlign = 2, BitsPerSample = 16
            };
            Check(waveOutOpen(out handle, UInt32.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0), "открыть аудиовыход");
        }

        public void StartTone(int frequency)
        {
            StopTone();
            if (frequency >= SampleRate / 2)
                throw new InvalidOperationException("Для предпросмотра частота должна быть ниже 22050 Гц.");
            // An integer number of cycles makes the loop boundary continuous.
            int cycles = Math.Max(1, (int)Math.Round(frequency * 0.08));
            int count = Math.Max(2, (int)Math.Round(SampleRate * (double)cycles / frequency));
            short[] pcm = new short[count];
            for (int i = 0; i < count; i++)
                pcm[i] = (short)(Math.Sin(2.0 * Math.PI * cycles * i / count) * 4200);
            samples = GCHandle.Alloc(pcm, GCHandleType.Pinned);
            header = Marshal.AllocHGlobal((int)headerSize);
            WaveHeader value = new WaveHeader {
                Data = samples.AddrOfPinnedObject(), BufferLength = (uint)(count * 2),
                Flags = 0x00000004 | 0x00000008, Loops = UInt32.MaxValue
            };
            Marshal.StructureToPtr(value, header, false);
            Check(waveOutPrepareHeader(handle, header, headerSize), "подготовить звук");
            prepared = true;
            Check(waveOutWrite(handle, header, headerSize), "воспроизвести звук");
        }

        public void StopTone()
        {
            if (header == IntPtr.Zero) return;
            if (prepared)
            {
                Check(waveOutReset(handle), "остановить звук");
                Check(waveOutUnprepareHeader(handle, header, headerSize), "освободить звук");
                prepared = false;
            }
            ReleaseBuffer();
        }

        private void ReleaseBuffer()
        {
            if (header != IntPtr.Zero) { Marshal.FreeHGlobal(header); header = IntPtr.Zero; }
            if (samples.IsAllocated) samples.Free();
        }

        private static void Check(uint result, string operation)
        {
            if (result != 0)
                throw new InvalidOperationException("Не удалось " + operation + " (waveOut " + result + ").");
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            try { StopTone(); }
            finally
            {
                uint result = waveOutClose(handle);
                if (result == 0) { handle = IntPtr.Zero; prepared = false; ReleaseBuffer(); }
                Check(result, "закрыть аудиовыход");
            }
        }
    }
}
