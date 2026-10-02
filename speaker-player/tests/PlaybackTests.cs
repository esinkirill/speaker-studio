using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SpeakerPlayer;

internal static class PlaybackTests
{
    private static int assertions;
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
    }
    private static PlaybackSettings Visual()
    {
        return new PlaybackSettings { Output = OutputMode.Visual };
    }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        bool thrown = false;
        try { action(); } catch (T) { thrown = true; }
        Assert(thrown, message);
    }

    public static int Main()
    {
        try
        {
            SnapshotAndTiming();
            CumulativeDeadlines();
            PauseResume();
            SeekStarts();
            SeekPausedAndRunning();
            SeekRacesAndLoop();
            StopRestartAndLoop();
            SessionIdentity();
            ErrorsAndDispose();
            Console.WriteLine("PASS: " + assertions + " playback assertions; Visual mode only, no audio or driver loaded.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void SnapshotAndTiming()
    {
        using (PlaybackEngine engine = new PlaybackEngine())
        using (ManualResetEvent done = new ManualResetEvent(false))
        {
            List<PlaybackProgress> events = new List<PlaybackProgress>();
            string failure = "unset";
            engine.Progress += delegate(PlaybackProgress p) { lock (events) events.Add(p); };
            engine.Completed += delegate(string error) { failure = error; done.Set(); };
            List<ToneRow> source = new List<ToneRow> { new ToneRow(440, 40, 10), new ToneRow(0, 20, 0) };
            PlaybackSettings settings = Visual();
            settings.Transpose = -12;
            settings.Speed = 2;
            settings.NoteGapMs = 5;
            engine.Play(source, settings, "Z:\\this-directory-does-not-exist");
            Assert(source[0].Frequency == 440 && source[0].DurationMs == 40 && source[0].PauseMs == 10,
                "Play must not edit CSV values");
            source[0].Frequency = 880;
            source[0].DurationMs = 500;
            settings.Transpose = 12;
            settings.Loop = true;
            settings.NoteGapMs = 999;
            Assert(done.WaitOne(2000), "Snapshot playback timed out");
            Assert(failure == null && !engine.IsPlaying, "Visual playback failed without DLL directory");
            PlaybackProgress first = events[0];
            Assert(first.RowIndex == 0 && first.RowCount == 2 && first.Frequency == 220,
                "Transpose and row metadata must use snapshot");
            Assert(first.DurationMs == 15 && first.PauseMs == 10 && first.TotalMs == 35,
                "Gap must consume scaled note duration and preserve total");
            bool separatePause = false, rest = false;
            long previous = -1;
            foreach (PlaybackProgress p in events)
            {
                Assert(p.PositionMs >= previous && p.PositionMs <= 35, "Playback positions must be monotonic");
                previous = p.PositionMs;
                if (p.RowIndex == 0 && p.IsPause && p.Frequency == 0) separatePause = true;
                if (p.RowIndex == 1 && p.IsPause && p.PauseMs == 0) rest = true;
            }
            Assert(separatePause && rest && previous == 35, "Pause/rest reporting must finish full timeline");
        }
    }

    private static void CumulativeDeadlines()
    {
        using (PlaybackEngine engine = new PlaybackEngine())
        using (ManualResetEvent done = new ManualResetEvent(false))
        {
            List<ToneRow> rows = new List<ToneRow>();
            for (int i = 0; i < 8; i++) rows.Add(new ToneRow(440, 100, 0));
            int lastDelayedRow = -1;
            long position = 0;
            string failure = "unset";
            engine.Progress += delegate(PlaybackProgress p) {
                position = p.PositionMs;
                if (p.RowIndex != lastDelayedRow)
                {
                    lastDelayedRow = p.RowIndex;
                    // Simulate a slow observer at every row start. Its time must
                    // count inside the track, not add 8 * 30ms to playback.
                    Thread.Sleep(30);
                }
            };
            engine.Completed += delegate(string error) { failure = error; done.Set(); };
            Stopwatch watch = Stopwatch.StartNew();
            engine.Play(rows, Visual(), null);
            Assert(done.WaitOne(2000) && failure == null, "Cumulative deadline playback must complete");
            Assert(position == 800, "Cumulative deadlines must report full nominal timeline");
            Assert(watch.ElapsedMilliseconds >= 750 && watch.ElapsedMilliseconds < 960,
                "Observer delays must not accumulate across notes: " + watch.ElapsedMilliseconds + "ms");
            Console.WriteLine("Cumulative deadline timing: " + watch.ElapsedMilliseconds + "ms for 800ms track with 240ms observer work.");
        }
    }

    private static void PauseResume()
    {
        using (PlaybackEngine engine = new PlaybackEngine())
        using (ManualResetEvent started = new ManualResetEvent(false))
        using (ManualResetEvent done = new ManualResetEvent(false))
        {
            long position = 0;
            string failure = "unset";
            Stopwatch watch = Stopwatch.StartNew();
            engine.Progress += delegate(PlaybackProgress p) { Interlocked.Exchange(ref position, p.PositionMs); started.Set(); };
            engine.Completed += delegate(string error) { failure = error; done.Set(); };
            engine.Play(new List<ToneRow> { new ToneRow(523, 220, 0) }, Visual(), null);
            Assert(started.WaitOne(1000), "Playback did not start");
            engine.Pause();
            Thread.Sleep(35);
            long held = Interlocked.Read(ref position);
            Thread.Sleep(100);
            Assert(engine.IsPlaying && !done.WaitOne(0), "Pause must retain track until resume");
            Assert(Interlocked.Read(ref position) == held, "User pause must freeze timeline");
            engine.Resume();
            Assert(done.WaitOne(2000) && failure == null, "Resume must complete remaining duration");
            Assert(Interlocked.Read(ref position) == 220, "Paused playback must preserve complete duration");
            Assert(watch.ElapsedMilliseconds >= 340, "User pause must be excluded from the playback clock");
        }
    }

    private static void StopRestartAndLoop()
    {
        using (PlaybackEngine engine = new PlaybackEngine())
        using (ManualResetEvent started = new ManualResetEvent(false))
        using (ManualResetEvent done = new ManualResetEvent(false))
        using (ManualResetEvent looped = new ManualResetEvent(false))
        {
            int starts = 0, finishes = 0;
            engine.Progress += delegate(PlaybackProgress p) {
                started.Set();
                if (p.PositionMs == 0 && Interlocked.Increment(ref starts) >= 3) looped.Set();
            };
            engine.Completed += delegate(string error) { Interlocked.Increment(ref finishes); done.Set(); };
            engine.Play(new List<ToneRow> { new ToneRow(440, 3000, 0) }, Visual(), null);
            Assert(started.WaitOne(1000), "Long playback did not start");
            Throws<InvalidOperationException>(delegate {
                engine.Play(new List<ToneRow> { new ToneRow(440, 1, 0) }, Visual(), null);
            }, "Concurrent track must not replace playing session");
            Stopwatch stop = Stopwatch.StartNew();
            engine.Stop();
            Assert(stop.ElapsedMilliseconds < 250 && done.WaitOne(0) && !engine.IsPlaying,
                "Stop must promptly interrupt long tone");
            engine.Stop();
            Assert(finishes == 1, "Repeated Stop must not raise duplicate completion");
            done.Reset();
            PlaybackSettings looping = Visual(); looping.Loop = true;
            engine.Play(new List<ToneRow> { new ToneRow(330, 15, 0) }, looping, null);
            Assert(looped.WaitOne(1000), "Loop must restart from beginning");
            engine.Pause();
            engine.Stop();
            Assert(done.WaitOne(0) && !engine.IsPlaying && finishes == 2, "Stop must interrupt paused loop");
            done.Reset();
            engine.Play(new List<ToneRow> { new ToneRow(262, 10, 0) }, Visual(), null);
            Assert(done.WaitOne(1000) && finishes == 3, "Stopped engine must support immediate restart");
        }
    }

    private static void SeekStarts()
    {
        List<ToneRow> rows = new List<ToneRow> { new ToneRow(440, 100, 50), new ToneRow(0, 70, 0), new ToneRow(660, 80, 0) };
        foreach (long start in new long[] { 50, 125, 180, 220, 300, 900, -10 })
        using (PlaybackEngine engine = new PlaybackEngine())
        using (ManualResetEvent done = new ManualResetEvent(false))
        {
            List<PlaybackProgress> events = new List<PlaybackProgress>();
            string failure = "unset";
            engine.Progress += delegate(PlaybackProgress p) { lock (events) events.Add(p); };
            engine.Completed += delegate(string error) { failure = error; done.Set(); };
            PlaybackSettings settings = Visual(); settings.StartPositionMs = start;
            Stopwatch watch = Stopwatch.StartNew();
            engine.Play(rows, settings, null);
            Assert(done.WaitOne(1000) && failure == null, "Seek-start visual playback must complete");
            long clipped = Math.Max(0, Math.Min(300, start));
            PlaybackProgress first = events[0];
            Assert(first.PositionMs == clipped && first.TotalMs == 300 && first.RowCount == 3,
                "Seek-start must keep full timeline metadata and clamp start position");
            int expectedFrequency = clipped < 100 ? 440 : clipped < 220 ? 0 : clipped < 300 ? 660 : 0;
            Assert(first.Frequency == expectedFrequency && first.IsPause == (expectedFrequency == 0),
                "Seek-start inside tone/pause/REST must select the correct segment");
            Assert(events[events.Count - 1].PositionMs == 300 && watch.ElapsedMilliseconds < 300 - clipped + 150,
                "Seek-start must play only the remaining budget");
            long previous = -1;
            foreach (PlaybackProgress p in events)
            {
                Assert(p.PositionMs >= previous && p.PositionMs <= 300 && p.PositionRevision == engine.PositionRevision,
                    "Seek-start progress must remain monotone within its position revision");
                previous = p.PositionMs;
            }
        }
        using (PlaybackEngine engine = new PlaybackEngine())
        using (ManualResetEvent done = new ManualResetEvent(false))
        {
            PlaybackProgress first = null;
            engine.Progress += delegate(PlaybackProgress p) { if (first == null) first = p; };
            engine.Completed += delegate { done.Set(); };
            PlaybackSettings settings = Visual(); settings.Speed = 2; settings.StartPositionMs = 55;
            engine.Play(rows, settings, null);
            Assert(done.WaitOne(1000) && first.PositionMs == 55 && first.TotalMs == 150 && first.IsPause,
                "Start positions use the transformed high-speed timeline, including pause boundaries");
        }
    }

    private static void SeekPausedAndRunning()
    {
        using (PlaybackEngine engine = new PlaybackEngine())
        using (ManualResetEvent started = new ManualResetEvent(false))
        using (AutoResetEvent progressed = new AutoResetEvent(false))
        using (ManualResetEvent done = new ManualResetEvent(false))
        {
            List<PlaybackProgress> events = new List<PlaybackProgress>();
            string failure = "unset";
            engine.Progress += delegate(PlaybackProgress p) { lock (events) events.Add(p); started.Set(); progressed.Set(); };
            engine.Completed += delegate(string error) { failure = error; done.Set(); };
            engine.Play(new ToneRow[] { new ToneRow(440, 500, 100), new ToneRow(0, 200, 0), new ToneRow(880, 400, 0) }, Visual(), null);
            Assert(started.WaitOne(1000), "Seek test track must start");
            long session = engine.LastSessionId;
            engine.Pause();
            engine.Seek(650);
            long revision = engine.PositionRevision;
            PlaybackProgress sought = WaitRevision(events, progressed, revision);
            Assert(sought.PositionMs == 650 && sought.IsPause && sought.Frequency == 0,
                "Paused seek must promptly report the target REST position");
            Thread.Sleep(70);
            lock (events) Assert(events[events.Count - 1].PositionMs == 650 && !done.WaitOne(0),
                "Seek must retain user pause state and freeze the new target");
            engine.Seek(1000);
            revision = engine.PositionRevision;
            sought = WaitRevision(events, progressed, revision);
            Assert(sought.PositionMs == 1000 && sought.Frequency == 880 && sought.SessionId == session,
                "Paused seek into a tone must keep the same session/backend");
            Stopwatch tail = Stopwatch.StartNew();
            engine.Resume();
            Assert(done.WaitOne(1000) && failure == null && tail.ElapsedMilliseconds >= 170 && tail.ElapsedMilliseconds < 400,
                "Resume after seek must complete the remaining 200ms, excluding prior pause");
        }
        using (PlaybackEngine engine = new PlaybackEngine())
        using (ManualResetEvent started = new ManualResetEvent(false))
        using (AutoResetEvent progressed = new AutoResetEvent(false))
        using (ManualResetEvent done = new ManualResetEvent(false))
        {
            List<PlaybackProgress> events = new List<PlaybackProgress>();
            engine.Progress += delegate(PlaybackProgress p) { lock (events) events.Add(p); started.Set(); progressed.Set(); };
            engine.Completed += delegate { done.Set(); };
            engine.Play(new ToneRow[] { new ToneRow(440, 1000, 0), new ToneRow(880, 1000, 0) }, Visual(), null);
            Assert(started.WaitOne(1000), "Inflight seek track must start");
            long session = engine.LastSessionId, oldRevision = engine.PositionRevision;
            engine.Seek(1250);
            long revision = engine.PositionRevision;
            PlaybackProgress sought = WaitRevision(events, progressed, revision);
            Assert(revision > oldRevision && sought.PositionMs >= 1250 && sought.PositionMs < 1300 && sought.Frequency == 880,
                "Inflight seek must use a new revision and correct tone");
            Assert(sought.SessionId == session && engine.LastSessionId == session,
                "Inflight seek must not stop/reopen a playback session");
            engine.Seek(Int64.MaxValue);
            Assert(done.WaitOne(1000), "Seeking to end must complete promptly");
            lock (events) Assert(events[events.Count - 1].PositionMs == 2000 && events[events.Count - 1].Frequency == 0,
                "Seeking to end must report silence at full track end");
        }
    }

    private static PlaybackProgress WaitRevision(List<PlaybackProgress> events, AutoResetEvent changed, long revision)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 1000)
        {
            lock (events)
                foreach (PlaybackProgress p in events) if (p.PositionRevision == revision) return p;
            changed.WaitOne(10);
        }
        throw new Exception("No progress for seek revision " + revision);
    }

    private static void SeekRacesAndLoop()
    {
        using (PlaybackEngine engine = new PlaybackEngine())
        using (ManualResetEvent started = new ManualResetEvent(false))
        using (AutoResetEvent progressed = new AutoResetEvent(false))
        using (ManualResetEvent done = new ManualResetEvent(false))
        {
            List<PlaybackProgress> events = new List<PlaybackProgress>();
            string failure = "unset";
            engine.Progress += delegate(PlaybackProgress p) { lock (events) events.Add(p); started.Set(); progressed.Set(); };
            engine.Completed += delegate(string error) { failure = error; done.Set(); };
            engine.Play(new ToneRow[] { new ToneRow(440, 10000, 0), new ToneRow(880, 10000, 0) }, Visual(), null);
            Assert(started.WaitOne(1000), "Concurrent seek track must start");
            Thread[] callers = new Thread[4];
            for (int i = 0; i < callers.Length; i++)
            {
                int thread = i;
                callers[i] = new Thread(delegate() { for (int j = 0; j < 64; j++) engine.Seek((j * 1031 + thread * 997) % 18000); });
                callers[i].Start();
            }
            foreach (Thread caller in callers) Assert(caller.Join(1000), "Concurrent seek must not deadlock");
            engine.Pause();
            engine.Seek(10005);
            PlaybackProgress final = WaitRevision(events, progressed, engine.PositionRevision);
            Assert(final.PositionMs == 10005 && final.Frequency == 880 && engine.IsPlaying,
                "Final concurrent seek must atomically win with correct paused segment");
            engine.Stop();
            Assert(done.WaitOne(0) && failure == null && !engine.IsPlaying, "Stop must cancel after concurrent paused seeks");
        }
        using (PlaybackEngine engine = new PlaybackEngine())
        using (ManualResetEvent looped = new ManualResetEvent(false))
        {
            PlaybackProgress first = null;
            engine.Progress += delegate(PlaybackProgress p) {
                if (first == null) first = p;
                else if (p.PositionMs == 0 && p.PositionRevision > first.PositionRevision) looped.Set();
            };
            PlaybackSettings settings = Visual(); settings.Loop = true; settings.StartPositionMs = 60;
            engine.Play(new ToneRow[] { new ToneRow(440, 80, 0) }, settings, null);
            Assert(looped.WaitOne(1000) && first.PositionMs == 60,
                "Loop must play selected tail once then restart from zero with a new revision");
            engine.Stop();
        }
    }

    private static void ErrorsAndDispose()
    {
        using (PlaybackEngine engine = new PlaybackEngine())
        {
            PlaybackSettings bad = Visual(); bad.Speed = 0;
            Throws<ArgumentException>(delegate { engine.Play(new List<ToneRow> { new ToneRow(440, 1, 0) }, bad, null); },
                "Zero speed must fail before worker starts");
            bad = Visual(); bad.NoteGapMs = -1;
            Throws<ArgumentException>(delegate { engine.Play(new List<ToneRow> { new ToneRow(440, 1, 0) }, bad, null); },
                "Negative pause must fail");
            bad = Visual(); bad.Loop = true;
            Throws<ArgumentException>(delegate { engine.Play(new List<ToneRow> { new ToneRow(440, 0, 0) }, bad, null); },
                "Zero duration loop must fail without spinning");
            Assert(!engine.IsPlaying, "Invalid input must not leave active session");
        }
        PlaybackEngine disposable = new PlaybackEngine();
        disposable.Play(new List<ToneRow> { new ToneRow(440, 3000, 0) }, Visual(), null);
        disposable.Dispose();
        Assert(!disposable.IsPlaying, "Dispose must stop current session");
        Throws<ObjectDisposedException>(delegate {
            disposable.Play(new List<ToneRow> { new ToneRow(440, 10, 0) }, Visual(), null);
        }, "Disposed engine must reject restart");
        using (PlaybackEngine engine = new PlaybackEngine())
        using (ManualResetEvent done = new ManualResetEvent(false))
        {
            string failure = null;
            engine.Progress += delegate { throw new InvalidOperationException("UI callback test"); };
            engine.Completed += delegate(string error) { failure = error; done.Set(); };
            engine.Play(new List<ToneRow> { new ToneRow(440, 10, 0) }, Visual(), null);
            Assert(done.WaitOne(1000) && failure == "UI callback test" && !engine.IsPlaying,
                "Worker failure must complete and release session");
        }
    }

    private static void SessionIdentity()
    {
        using (PlaybackEngine engine = new PlaybackEngine())
        using (ManualResetEvent started = new ManualResetEvent(false))
        {
            List<long> queuedIds = new List<long>();
            long completedId = 0;
            engine.Progress += delegate(PlaybackProgress p) {
                lock (queuedIds) queuedIds.Add(p.SessionId);
                started.Set();
            };
            engine.SessionCompleted += delegate(long id, string error) { completedId = id; lock (queuedIds) queuedIds.Add(id); };
            engine.Play(new List<ToneRow> { new ToneRow(440, 1000, 0) }, Visual(), null);
            long first = engine.LastSessionId;
            Assert(first > 0 && started.WaitOne(1000), "First session must have a positive identity");
            engine.Stop();
            Assert(completedId == first && engine.LastSessionId == first,
                "Completion must carry identity after engine stops");
            int oldCount; lock (queuedIds) oldCount = queuedIds.Count;
            started.Reset();
            engine.Play(new List<ToneRow> { new ToneRow(220, 1000, 0) }, Visual(), null);
            long second = engine.LastSessionId;
            Assert(second > first && started.WaitOne(1000), "Restart must get a new session identity");
            lock (queuedIds)
            {
                for (int i = 0; i < oldCount; i++)
                    Assert(queuedIds[i] != second, "UI can reject old queued progress/completion by identity");
                Assert(queuedIds[oldCount] == second, "New progress must carry new identity");
            }
            engine.Stop();
            Assert(completedId == second, "Restart completion must identify second session");
        }
    }
}
