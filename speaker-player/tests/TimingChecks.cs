using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SpeakerPlayer;

internal static class TimingChecks
{
    private static int assertions;
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
    }

    private static long Total(IEnumerable<ToneRow> rows)
    {
        long total = 0;
        foreach (ToneRow row in rows) total += (long)row.DurationMs + row.PauseMs;
        return total;
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        bool thrown = false;
        try { action(); } catch (T) { thrown = true; }
        Assert(thrown, message);
    }

    private static List<ToneRow> Transform(ToneRow row, int transpose, double speed, int gap)
    {
        return SequenceTiming.Transform(new ToneRow[] { row }, transpose, speed, gap);
    }

    public static int Main(string[] args)
    {
        try
        {
            BasicArticulation();
            RoundedBoundaries();
            RhythmChecks();
            GlideChecks();
            RhythmPlaybackChecks();
            Validation();
            ReferenceTimeline();
            Console.WriteLine("PASS: " + assertions + " timing/Visual playback assertions; no audio or driver loaded.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void BasicArticulation()
    {
        ToneRow source = new ToneRow(440, 100, 20);
        ToneRow result = Transform(source, -12, 1, 10)[0];
        Assert(result.Frequency == 220 && result.DurationMs == 90 && result.PauseMs == 30,
            "Gap should consume note end without changing pitch scaling");
        Assert(source.Frequency == 440 && source.DurationMs == 100 && source.PauseMs == 20,
            "Transform must not mutate source");
        Assert(!Object.ReferenceEquals(source, result), "Result must contain copied rows");
        result = Transform(source, 0, 2, 10)[0];
        Assert(result.Frequency == 440 && result.DurationMs == 40 && result.PauseMs == 20,
            "Gap should be carved after speed scaling");
        Assert(Total(Transform(source, 24, 0.5, 99)) == 240,
            "Transpose and large gap must not change total timeline");
        result = Transform(new ToneRow(0, 100, 20), 12, 2, 30)[0];
        Assert(result.Frequency == 0 && result.DurationMs == 50 && result.PauseMs == 10,
            "REST must only be scaled, not articulated");
        result = Transform(new ToneRow(440, 3, 0), 0, 1, 1000)[0];
        Assert(result.DurationMs == 1 && result.PauseMs == 2,
            "A gap must retain one millisecond of a short sounding note");
        result = Transform(new ToneRow(440, 1, 0), 0, 1, 1000)[0];
        Assert(result.DurationMs == 1 && result.PauseMs == 0, "A 1ms note must remain audible in data");
        result = Transform(new ToneRow(440, 0, 10), 0, 1, 1000)[0];
        Assert(result.DurationMs == 0 && result.PauseMs == 10, "A zero duration row must not gain time");
        List<ToneRow> two = SequenceTiming.Transform(new ToneRow[] {
            new ToneRow(440, 99, 5), new ToneRow(880, 77, 13) }, 0, 1.3, 10);
        Assert(two[0].DurationMs + two[0].PauseMs == Math.Round(104 / 1.3),
            "Articulation must preserve the next note start");
        Assert(Total(two) == Math.Round(194 / 1.3), "Last row end must match scaled source end");
        Assert(SequenceTiming.Transform(new ToneRow[0], 0, 1, 0).Count == 0,
            "An empty enumerable should produce an empty timeline");
    }

    private static void RoundedBoundaries()
    {
        List<ToneRow> source = new List<ToneRow>();
        for (int i = 0; i < 1001; i++) source.Add(new ToneRow(440, 1, 1));
        List<ToneRow> result = SequenceTiming.Transform(source, 0, 3, 0);
        Assert(Total(result) == Math.Round(2002 / 3.0),
            "Many submillisecond pieces must retain cumulative duration");
        long actual = 0;
        for (int i = 0; i < result.Count; i++)
        {
            actual += result[i].DurationMs;
            Assert(actual == Math.Round((2 * i + 1) / 3.0), "Every scaled sound boundary must be exact");
            actual += result[i].PauseMs;
            Assert(actual == Math.Round((2 * i + 2) / 3.0), "Every scaled pause boundary must be exact");
        }
        List<ToneRow> withGap = SequenceTiming.Transform(source, -2, 1.4881, 10);
        Assert(Total(withGap) == Math.Round(2002 / 1.4881),
            "Noninteger tempo and articulation must not cause rounding drift");
        for (int i = 0; i < result.Count; i++)
            Assert(source[i].DurationMs == 1 && source[i].PauseMs == 1,
                "Rounding transform must preserve all input rows");
    }

    private static void Validation()
    {
        ToneRow row = new ToneRow(440, 100, 0);
        Throws<ArgumentNullException>(delegate { SequenceTiming.Transform(null, 0, 1, 0); }, "Null source must fail");
        foreach (double speed in new double[] { 0, -1, Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity })
            Throws<ArgumentException>(delegate { Transform(row, 0, speed, 0); }, "Invalid speed must fail");
        Throws<ArgumentException>(delegate { Transform(row, 0, 1, -1); }, "Negative gap must fail");
        Throws<ArgumentException>(delegate { Transform(null, 0, 1, 0); }, "Null row must fail");
        Throws<ArgumentException>(delegate { Transform(new ToneRow(-1, 1, 0), 0, 1, 0); }, "Negative pitch must fail");
        Throws<ArgumentException>(delegate { Transform(new ToneRow(440, -1, 0), 0, 1, 0); }, "Negative duration must fail");
        Throws<ArgumentException>(delegate { Transform(new ToneRow(440, 1, -1), 0, 1, 0); }, "Negative pause must fail");
        Throws<ArgumentException>(delegate { Transform(row, Int32.MaxValue, 1, 0); }, "Extreme transposition must fail");
        Throws<ArgumentException>(delegate { Transform(new ToneRow(Int32.MaxValue, 1, 0), 12, 1, 0); }, "Frequency overflow must fail");
        Throws<ArgumentException>(delegate { Transform(row, 0, 1E-300, 0); }, "Scaled duration overflow must fail");
        Throws<ArgumentException>(delegate { Transform(new ToneRow(440, 2, Int32.MaxValue), 0, 1, 1); },
            "Articulation pause overflow must fail before playback");
    }

    private static RhythmSettings Grid(RhythmMode mode)
    {
        return new RhythmSettings { Mode = mode, SourceBpm = 150, PartsPerBeat = 4,
            PhaseMs = 0, SoundPercent = 90 };
    }

    private static void RhythmPlaybackChecks()
    {
        foreach (RhythmMode mode in new RhythmMode[] { RhythmMode.Quantize, RhythmMode.Chop })
        using (PlaybackEngine engine = new PlaybackEngine())
        using (ManualResetEvent done = new ManualResetEvent(false))
        {
            List<ToneRow> source = new List<ToneRow> { new ToneRow(440, 257, 20), new ToneRow(0, 30, 0) };
            RhythmSettings rhythm = Grid(mode);
            rhythm.PhaseMs = 20;
            List<ToneRow> expected = SequenceTiming.Transform(source, -12, 2, 10, rhythm);
            List<PlaybackProgress> events = new List<PlaybackProgress>();
            string failure = "unset";
            engine.Progress += delegate(PlaybackProgress p) { lock (events) events.Add(p); };
            engine.Completed += delegate(string error) { failure = error; done.Set(); };
            PlaybackSettings settings = new PlaybackSettings { Output = OutputMode.Visual,
                Transpose = -12, Speed = 2, NoteGapMs = 10, Rhythm = rhythm };
            engine.Play(source, settings, "Z:\\no-dll-or-audio");
            rhythm.Mode = RhythmMode.Original;
            rhythm.SourceBpm = 0;
            rhythm.SoundPercent = 0;
            Assert(done.WaitOne(2000) && failure == null, "Visual rhythm playback must use its independent settings snapshot");
            Assert(events[0].RowCount == expected.Count && events[0].TotalMs == Total(expected),
                "Player must use the same shared rhythm transform as export and graph");
            HashSet<int> visited = new HashSet<int>();
            long previous = -1;
            foreach (PlaybackProgress p in events)
            {
                Assert(p.PositionMs >= previous && p.PositionMs <= Total(expected),
                    "Rhythm playback progress must preserve ordered nominal timeline");
                previous = p.PositionMs;
                if (!visited.Add(p.RowIndex)) continue;
                ToneRow row = expected[p.RowIndex];
                Assert(p.Frequency == row.Frequency && p.DurationMs == row.DurationMs && p.PauseMs == row.PauseMs,
                    "Rhythm playback rows must exactly match shared transform");
            }
            Assert(visited.Count == expected.Count && previous == Total(expected),
                "Visual playback must cover complete quantized/chopped timeline");
        }
    }

    private static void RhythmChecks()
    {
        List<ToneRow> source = new List<ToneRow> { new ToneRow(440, 300, 0) };
        RhythmSettings grid = Grid(RhythmMode.Chop);
        List<ToneRow> result = SequenceTiming.Transform(source, 0, 1, 0, grid);
        Assert(Total(result) == 300 && result.Count == 6, "Three 100ms chop cells must keep original duration");
        for (int i = 0; i < 6; i++)
            Assert(result[i].Frequency == (i % 2 == 0 ? 440 : 0) &&
                result[i].DurationMs == (i % 2 == 0 ? 90 : 10), "Chop must alternate 90ms tone and 10ms silence");
        result = SequenceTiming.Transform(source, 0, 1, 20, grid);
        long sounding = 0;
        foreach (ToneRow row in result) if (row.Frequency > 0) sounding += row.DurationMs;
        Assert(Total(result) == 300 && sounding == 260 && result[0].DurationMs == 90,
            "Articulation gap must affect original note end, not every chopped slice");
        grid.SoundPercent = 100;
        result = SequenceTiming.Transform(source, -12, 2, 10, grid);
        Assert(result.Count == 1 && result[0].Frequency == 220 && result[0].DurationMs == 140 && result[0].PauseMs == 10,
            "100 percent fill must retain original scaled note and gap");
        grid.SoundPercent = 0;
        result = SequenceTiming.Transform(source, 0, 1, 0, grid);
        Assert(result.Count == 1 && result[0].Frequency == 0 && Total(result) == 300,
            "Zero percent fill must be silence with unchanged length");
        grid.SoundPercent = 50;
        grid.PhaseMs = 50;
        result = SequenceTiming.Transform(source, 0, 1, 0, grid);
        Assert(result[0].Frequency == 0 && result[0].DurationMs == 50 && result[1].Frequency == 440 && result[1].DurationMs == 50,
            "Grid phase must determine initial silence and tone window");
        result = SequenceTiming.Transform(source, 0, 2, 0, grid);
        Assert(Total(result) == 150 && result[0].DurationMs == 25 && result[1].DurationMs == 25,
            "Speed must scale grid period and phase along with source timeline");
        grid.PhaseMs = -50;
        List<ToneRow> negativePhase = SequenceTiming.Transform(source, 0, 2, 0, grid);
        Assert(negativePhase.Count == result.Count, "Negative phase must normalize to equivalent grid");
        for (int i = 0; i < result.Count; i++)
            Assert(result[i].Frequency == negativePhase[i].Frequency && result[i].DurationMs == negativePhase[i].DurationMs,
                "Normalized negative phase must preserve every chop interval");
        grid.PhaseMs = 0;
        source = new List<ToneRow> { new ToneRow(0, 75, 20), new ToneRow(440, 205, 40) };
        result = SequenceTiming.Transform(source, 0, 1, 0, grid);
        Assert(result[0].Frequency == 0 && result[0].DurationMs == 75 && result[0].PauseMs == 20,
            "Chop must preserve original absolute REST and its pause");
        Assert(Total(result) == 340 && result[result.Count - 1].PauseMs == 40,
            "Chop must preserve original final pause");
        grid = Grid(RhythmMode.Quantize);
        source = new List<ToneRow> { new ToneRow(440, 137, 43), new ToneRow(660, 220, 0) };
        result = SequenceTiming.Transform(source, 0, 1, 0, grid);
        Assert(result.Count == 2 && result[0].DurationMs == 118 && result[0].PauseMs == 72 && result[1].DurationMs == 210,
            "Quantize must move boundaries halfway toward nearest grid, with final end anchored");
        Assert(result[0].PauseMs > 0 && Total(result) == 400, "Quantize must retain musical silence and total");
        grid.PhaseMs = 20;
        result = SequenceTiming.Transform(source, 0, 1, 0, grid);
        Assert(result[0].DurationMs == 128 && result[0].PauseMs == 72 && Total(result) == 400,
            "Quantize must honor nonzero grid phase");
        List<ToneRow> fast = new List<ToneRow>();
        for (int i = 0; i < 501; i++) fast.Add(new ToneRow(i % 3 == 0 ? 0 : 440, i % 2, 1));
        result = SequenceTiming.Transform(fast, 0, 1.4881, 10, grid);
        Assert(result.Count == fast.Count && Total(result) == Math.Round(Total(fast) / 1.4881),
            "Quantized zero/short intervals must remain ordered with exact cumulative total");
        for (int i = 0; i < result.Count; i++)
            Assert(result[i].Frequency == fast[i].Frequency && result[i].DurationMs >= 0 && result[i].PauseMs >= 0,
                "Quantize must never generate negative intervals or invent pitches in REST rows");
        RhythmSettings original = new RhythmSettings { SourceBpm = 0, PartsPerBeat = 3, PhaseMs = Double.NaN };
        result = SequenceTiming.Transform(source, 0, 1, 0, original);
        Assert(result[0].DurationMs == 137 && result[0].PauseMs == 43 && Total(result) == 400,
            "Original mode must retain previous behavior with no known grid");
        grid = Grid(RhythmMode.Chop);
        grid.SourceBpm = 0;
        Throws<ArgumentException>(delegate { SequenceTiming.Transform(source, 0, 1, 0, grid); }, "Unknown grid BPM must fail");
        grid = Grid(RhythmMode.Quantize); grid.PartsPerBeat = 5;
        Throws<ArgumentException>(delegate { SequenceTiming.Transform(source, 0, 1, 0, grid); }, "Unsupported subdivision must fail");
        grid = Grid(RhythmMode.Chop); grid.PhaseMs = Double.NaN;
        Throws<ArgumentException>(delegate { SequenceTiming.Transform(source, 0, 1, 0, grid); }, "Invalid phase must fail");
        grid = Grid(RhythmMode.Chop); grid.SoundPercent = 101;
        Throws<ArgumentException>(delegate { SequenceTiming.Transform(source, 0, 1, 0, grid); }, "Invalid fill must fail");
        grid = Grid(RhythmMode.Chop); grid.SourceBpm = 50000; grid.PartsPerBeat = 8;
        Throws<ArgumentException>(delegate { SequenceTiming.Transform(source, 0, 1, 0, grid); }, "Submillisecond grid must fail");
        grid = Grid(RhythmMode.Chop);
        Throws<ArgumentException>(delegate { SequenceTiming.Transform(new ToneRow[] { new ToneRow(440, Int32.MaxValue, 0) },
            0, 1, 0, grid); }, "Excessive chop output must stop at the safety limit");
    }

    private static void GlideChecks()
    {
        List<ToneRow> source = new List<ToneRow> { new ToneRow(440, 100, 0), new ToneRow(880, 100, 0) };
        RhythmSettings rhythm = new RhythmSettings { GlideMs = 60, CurveResolutionMs = 20 };
        List<ToneRow> result = SequenceTiming.Transform(source, 0, 1, 0, rhythm);
        Assert(result.Count == 4 && Total(result) == 200 && result[0].DurationMs == 100,
            "Glide must keep first tone and complete original length");
        Assert(result[1].Frequency == 440 && result[2].Frequency == (int)Math.Round(Math.Sqrt(440.0 * 880)) && result[3].Frequency == 880,
            "Musical glide must include exact endpoints and geometric-pitch midpoint");
        Assert(result[1].DurationMs == 20 && result[2].DurationMs == 20 && result[3].DurationMs == 60,
            "Glide must consume the beginning of the next note and keep its final hold");
        Assert(source.Count == 2 && source[0].DurationMs == 100 && source[1].Frequency == 880 && source[1].DurationMs == 100,
            "Glide must not mutate source rows");
        result = SequenceTiming.Transform(source, -12, 0.5, 0, rhythm);
        Assert(Total(result) == 400 && result[0].DurationMs == 200 && result[1].DurationMs == 20 && result[2].DurationMs == 20,
            "Glide duration and resolution are real milliseconds after speed scaling");
        Assert(result[1].Frequency == 220 && result[2].Frequency == 311 && result[3].Frequency == 440,
            "Glide must respect independent pitch transposition");
        result = SequenceTiming.Transform(source, 0, 2, 0, rhythm);
        Assert(Total(result) == 100 && result[0].DurationMs == 50 && result[result.Count - 1].Frequency == 880,
            "Glide longer than a scaled note must clamp without losing its target endpoint");
        result = SequenceTiming.Transform(source, 0, 1, 10, rhythm);
        Assert(result.Count == 2 && result[0].PauseMs == 10 && result[1].Frequency == 880 && Total(result) == 200,
            "Glide must not bridge articulation gaps");
        List<ToneRow> rests = new List<ToneRow> { new ToneRow(440, 100, 0), new ToneRow(0, 30, 0), new ToneRow(880, 100, 10) };
        result = SequenceTiming.Transform(rests, 0, 1, 0, rhythm);
        Assert(result.Count == 3 && result[1].Frequency == 0 && result[1].DurationMs == 30 && result[2].Frequency == 880 && Total(result) == 240,
            "Glide must never fill an original REST");
        List<ToneRow> same = new List<ToneRow> { new ToneRow(440, 100, 0), new ToneRow(440, 100, 0) };
        Assert(SequenceTiming.Transform(same, 0, 1, 0, rhythm).Count == 2,
            "Same-pitch repeated attacks must stay separate and not generate redundant ramps");
        List<ToneRow> shortNotes = new List<ToneRow> { new ToneRow(440, 1, 0), new ToneRow(880, 1, 0) };
        result = SequenceTiming.Transform(shortNotes, 0, 1, 0, rhythm);
        Assert(result.Count == 2 && result[1].Frequency == 880 && Total(result) == 2,
            "One millisecond notes must remain safe without an unresolvable ramp");
        rhythm = Grid(RhythmMode.Chop); rhythm.GlideMs = 100; rhythm.CurveResolutionMs = 10;
        result = SequenceTiming.Transform(new ToneRow[] { new ToneRow(440, 300, 0), new ToneRow(880, 300, 0) }, 0, 1, 0, rhythm);
        long position = 0;
        bool transition = false;
        foreach (ToneRow row in result)
        {
            if (position >= 300 && position < 390 && row.Frequency > 440 && row.Frequency < 880) transition = true;
            position += (long)row.DurationMs + row.PauseMs;
        }
        Assert(Total(result) == 600 && transition,
            "Glide must precede Chop so artificial silence slices do not suppress a genuine note transition");
        foreach (int parts in new int[] { 1, 2, 3, 4, 6, 8, 12, 16 })
        {
            rhythm = Grid(RhythmMode.Chop); rhythm.PartsPerBeat = parts;
            Assert(Total(SequenceTiming.Transform(source, 0, 1, 0, rhythm)) == 200,
                "Every binary/triplet subdivision must preserve total timeline");
        }
        RhythmSettings copy = rhythm.Copy();
        rhythm.GlideMs = 77; rhythm.CurveResolutionMs = 3;
        copy = rhythm.Copy();
        Assert(copy.GlideMs == 77 && copy.CurveResolutionMs == 3 && !Object.ReferenceEquals(copy, rhythm),
            "Rhythm settings snapshot must carry glide controls independently");
        rhythm = new RhythmSettings { GlideMs = -1 };
        Throws<ArgumentException>(delegate { SequenceTiming.Transform(source, 0, 1, 0, rhythm); }, "Negative glide must fail");
        rhythm = new RhythmSettings { GlideMs = 50, CurveResolutionMs = 0 };
        Throws<ArgumentException>(delegate { SequenceTiming.Transform(source, 0, 1, 0, rhythm); }, "Zero curve resolution must fail");
        rhythm = new RhythmSettings { GlideMs = Int32.MaxValue, CurveResolutionMs = 1 };
        Throws<ArgumentException>(delegate { SequenceTiming.Transform(new ToneRow[] {
            new ToneRow(440, 1, 0), new ToneRow(880, Int32.MaxValue, 0) }, 0, 1, 0, rhythm); },
            "Glide output must obey the shared 200000-piece guard");
    }

    private static void ReferenceTimeline()
    {
        List<ToneRow> source = new List<ToneRow> {
            new ToneRow(0, 173, 27), new ToneRow(440, 203, 0), new ToneRow(440, 117, 31),
            new ToneRow(659, 409, 13), new ToneRow(0, 61, 0), new ToneRow(523, 87, 5), new ToneRow(784, 201, 8)
        };
        Assert(source.Count == 7 && Total(source) == 1335, "Generated reference timeline must contain notes, repeated attacks and rests");
        List<ToneRow> normal = SequenceTiming.Transform(source, 0, 1, 10);
        Assert(Total(normal) == 1335, "Reference sequence with 10ms gaps must stay 20.748s");
        Assert(Total(SequenceTiming.Transform(source, -12, 0.5, 10)) == 2670,
            "Reference sequence at half tempo must take exactly twice as long");
        Assert(Total(SequenceTiming.Transform(source, 2, 1.5, 10)) == Math.Round(1335 / 1.5),
            "Reference sequence at 150 percent tempo must preserve rounded total");
        foreach (RhythmMode mode in new RhythmMode[] { RhythmMode.Quantize, RhythmMode.Chop })
        foreach (double speed in new double[] { 0.5, 1, 1.75 })
        foreach (double phase in new double[] { 0, 35 })
        {
            RhythmSettings grid = new RhythmSettings { Mode = mode, SourceBpm = 117.25,
                PartsPerBeat = 4, PhaseMs = phase, SoundPercent = 90 };
            Assert(Total(SequenceTiming.Transform(source, -2, speed, 10, grid)) == Math.Round(1335 / speed),
                "Reference sequence rhythm processing must preserve scaled total for every mode/speed/phase");
        }
        long sourceStart = 0, actualStart = 0;
        for (int i = 0; i < source.Count; i++)
        {
            Assert(sourceStart == actualStart, "Reference sequence gap must leave every note start unchanged");
            Assert(normal[i].Frequency == source[i].Frequency, "Gap must not alter frequency");
            if (source[i].Frequency == 0)
                Assert(normal[i].DurationMs == source[i].DurationMs && normal[i].PauseMs == source[i].PauseMs,
                    "Reference sequence initial silence must remain unchanged");
            else Assert(normal[i].DurationMs == source[i].DurationMs - 10 && normal[i].PauseMs == source[i].PauseMs + 10,
                "Reference sequence sounding notes must have a 10ms articulation gap");
            sourceStart += (long)source[i].DurationMs + source[i].PauseMs;
            actualStart += (long)normal[i].DurationMs + normal[i].PauseMs;
        }
    }
}
