using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SpeakerPlayer;

internal static class SequenceDataChecks
{
    private static int checks;
    private static string scratch;

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }

    private static void EqualRows(List<ToneRow> actual, List<ToneRow> expected, string name)
    {
        Assert(actual.Count == expected.Count, name + ": row count");
        for (int i = 0; i < actual.Count; i++)
            Assert(actual[i].Frequency == expected[i].Frequency &&
                actual[i].DurationMs == expected[i].DurationMs && actual[i].PauseMs == expected[i].PauseMs,
                name + ": row " + (i + 1));
    }

    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (InvalidDataException) { checks++; return; }
        catch (NotSupportedException) { checks++; return; }
        catch (ArgumentOutOfRangeException) { checks++; return; }
        throw new Exception("Expected rejection: " + name);
    }

    private static byte[] Vlq(int value)
    {
        List<byte> bytes = new List<byte>();
        bytes.Insert(0, (byte)(value & 127));
        value >>= 7;
        while (value > 0) { bytes.Insert(0, (byte)((value & 127) | 128)); value >>= 7; }
        return bytes.ToArray();
    }

    private static byte[] E(int delta, params byte[] data)
    {
        List<byte> bytes = new List<byte>(Vlq(delta));
        bytes.AddRange(data);
        return bytes.ToArray();
    }

    private static byte[] Join(params byte[][] chunks)
    {
        List<byte> data = new List<byte>();
        foreach (byte[] chunk in chunks) data.AddRange(chunk);
        return data.ToArray();
    }

    private static byte[] Meta(int delta, int type, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return Join(E(delta, 255, (byte)type), Vlq(bytes.Length), bytes);
    }

    private static List<ToneRow> Collapse(List<ToneRow> rows)
    {
        List<ToneRow> result = new List<ToneRow>();
        foreach (ToneRow row in rows)
        {
            if (result.Count > 0 && result[result.Count - 1].Frequency == row.Frequency)
                result[result.Count - 1].DurationMs += row.DurationMs;
            else result.Add(new ToneRow(row.Frequency, row.DurationMs, 0));
        }
        return result;
    }

    private static void Number(BinaryWriter writer, int number, int length)
    {
        for (int i = length - 1; i >= 0; i--) writer.Write((byte)((number >> (8 * i)) & 255));
    }

    private static string Midi(string name, int format, int division, params byte[][] tracks)
    {
        string path = Path.Combine(scratch, name + ".mid");
        using (FileStream stream = File.Create(path))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("MThd"));
            Number(writer, 6, 4);
            Number(writer, format, 2);
            Number(writer, tracks.Length, 2);
            Number(writer, division, 2);
            foreach (byte[] track in tracks)
            {
                writer.Write(Encoding.ASCII.GetBytes("MTrk"));
                Number(writer, track.Length, 4);
                writer.Write(track);
            }
        }
        return path;
    }

    private static string Csv(string name, string text, bool bom)
    {
        string path = Path.Combine(scratch, name + ".csv");
        File.WriteAllText(path, text, new UTF8Encoding(bom));
        return path;
    }

    private static void CsvChecks()
    {
        List<ToneRow> expected = new List<ToneRow> { new ToneRow(440, 200, 10), new ToneRow(0, 50, 15) };
        EqualRows(SequenceFiles.ReadCsv(Csv("semicolon", "frequency;duration;pause\r\n440;200;10\r\n0;50;15\r\n", false)), expected, "semicolon");
        EqualRows(SequenceFiles.ReadCsv(Csv("comma", "frequency,duration,pause\n440,200,10\n0,50,15\n", false)), expected, "comma");
        EqualRows(SequenceFiles.ReadCsv(Csv("tab", "frequency\tduration\tpause\n440\t200\t10\n0\t50\t15\n", false)), expected, "tab");
        EqualRows(SequenceFiles.ReadCsv(Csv("excel quotes bom", "\"frequency\";\"duration\";\"pause\"\r\n\"440\";\"200\";\"10\"\r\n\"0\";\"50\";\"15\"\r\n", true)), expected, "quotes and UTF8 BOM");
        EqualRows(SequenceFiles.ReadCsv(Csv("reordered", "pause;frequency;duration\n10;440;200\n15;0;50", false)), expected, "header columns");
        EqualRows(SequenceFiles.ReadCsv(Csv("directive", "sep=,\nfrequency,duration,pause\n440,200,10\n0,50,15", false)), expected, "sep directive");
        Reject(delegate { SequenceFiles.ReadCsv(Csv("missing column", "frequency;duration\n440;200", false)); }, "missing column");
        Reject(delegate { SequenceFiles.ReadCsv(Csv("negative", "frequency;duration;pause\n440;-200;10", false)); }, "negative duration");
        Reject(delegate { SequenceFiles.ReadCsv(Csv("float", "frequency;duration;pause\n440.2;200;10", false)); }, "noninteger frequency");
        Reject(delegate { SequenceFiles.ReadCsv(Csv("bad quotes", "frequency;duration;pause\n\"440\"3;200;10", false)); }, "bad quote suffix");
        Reject(delegate { SequenceFiles.ReadCsv(Csv("empty", "frequency;duration;pause\n", false)); }, "empty sequence");
        string written = Path.Combine(scratch, "round trip.csv");
        SequenceFiles.WriteCsv(written, expected);
        Assert(File.ReadAllText(written) == "frequency;duration;pause\r\n440;200;10\r\n0;50;15\r\n", "canonical semicolon ASCII CRLF");
        EqualRows(SequenceFiles.ReadCsv(written), expected, "write/read round trip");
        Reject(delegate { SequenceFiles.WriteCsv(written, new ToneRow[] { new ToneRow(440, -1, 0) }); }, "invalid output data");
        EqualRows(SequenceFiles.ReadCsv(written), expected, "failed write preserves destination");
    }

    private static void MidiChecks(string root)
    {
        byte[] tempo = Join(E(0, 255, 81, 3, 7, 161, 32), E(480, 255, 81, 3, 15, 66, 64), E(960, 255, 47, 0));
        byte[] notes = Join(E(0, 255, 3, 4, 76, 101, 97, 100), E(0, 144, 60, 100), E(240, 64, 100), E(240, 64, 0), E(240, 60, 0), E(240, 69, 100), E(240, 69, 0), E(240, 255, 47, 0));
        byte[] drums = Join(E(0, 153, 100, 100), E(1440, 137, 100, 0), E(0, 255, 47, 0));
        string mixed = Midi("tempo polyphony", 1, 480, tempo, notes, drums);
        List<ToneRow> expected = new List<ToneRow> {
            new ToneRow(262,250,0), new ToneRow(330,250,0), new ToneRow(262,500,0),
            new ToneRow(0,500,0), new ToneRow(440,500,0), new ToneRow(0,500,0) };
        EqualRows(SequenceFiles.ReadMidi(mixed, -1), expected, "tempo/running/polyphony/drums");
        EqualRows(SequenceFiles.ReadMidi(mixed, 1), expected, "selected track retains global tempo");
        List<MidiTrackInfo> tracks = SequenceFiles.ReadMidiTracks(mixed);
        Assert(tracks.Count == 3 && tracks[0].NoteCount == 0 && tracks[1].NoteCount == 3 && tracks[2].NoteCount == 0, "playable note counts");
        Assert(tracks[1].Name == "Lead" && tracks[1].Index == 1 && tracks[1].ToString().Contains("Lead"), "track name and display");
        MidiTempoInfo changedTempo = SequenceFiles.ReadMidiTempo(mixed);
        Assert(Math.Abs(changedTempo.InitialBpm - 120) < 0.000001 && changedTempo.HasTempoChanges && changedTempo.TempoEventCount == 2,
            "initial BPM and global tempo changes");
        Reject(delegate { SequenceFiles.ReadMidi(mixed, 0); }, "tempo-only selection");
        Reject(delegate { SequenceFiles.ReadMidi(mixed, 3); }, "out of range selection");

        byte[] merge = Join(E(0, 144, 69, 100), E(240, 128, 69, 0), E(0, 144, 69, 100), E(240, 128, 69, 0), E(0, 255, 47, 0));
        string mergeMidi = Midi("format0 merge", 0, 480, merge);
        EqualRows(SequenceFiles.ReadMidi(mergeMidi, -1), new List<ToneRow> {
            new ToneRow(440, 250, 0), new ToneRow(440, 250, 0) }, "format0 repeated attack retained");
        MidiTempoInfo defaultTempo = SequenceFiles.ReadMidiTempo(mergeMidi);
        Assert(defaultTempo.InitialBpm == 120 && !defaultTempo.HasTempoChanges && defaultTempo.TempoEventCount == 0, "default120 BPM");
        Assert(!defaultTempo.AudioBarOffsetSeconds.HasValue, "ordinary MIDI has no audio offset");
        byte[] lowEvents = Join(E(0, 144, 69, 100), E(120, 144, 48, 100),
            E(120, 128, 48, 0), E(240, 128, 69, 0), E(0, 255, 47, 0));
        EqualRows(SequenceFiles.ReadMidi(Midi("low events do not retrigger", 0, 480, lowEvents), -1),
            new List<ToneRow> { new ToneRow(440, 500, 0) }, "lower note events must not split held top note");
        byte[] tempoInside = Join(E(240, 255, 81, 3, 7, 161, 32), E(240, 255, 47, 0));
        byte[] held = Join(E(0, 144, 69, 100), E(480, 128, 69, 0), E(0, 255, 47, 0));
        EqualRows(SequenceFiles.ReadMidi(Midi("tempo event no attack", 1, 480, tempoInside, held), 1),
            new List<ToneRow> { new ToneRow(440, 500, 0) }, "tempo event must not split a held note");
        byte[] lowReattack = Join(E(0, 144, 69, 100), E(0, 144, 48, 100), E(240, 128, 48, 0),
            E(0, 144, 48, 100), E(240, 128, 48, 0), E(0, 128, 69, 0), E(0, 255, 47, 0));
        EqualRows(SequenceFiles.ReadMidi(Midi("low reattack", 0, 480, lowReattack), -1),
            new List<ToneRow> { new ToneRow(440, 500, 0) }, "repeated lower notes must not split unchanged top note");
        byte[] marker = Join(Meta(0, 6, "muscriptor:bar_offset=1.2634"), E(0, 255, 47, 0));
        MidiTempoInfo offset = SequenceFiles.ReadMidiTempo(Midi("offset marker", 1, 480, marker, merge));
        Assert(offset.AudioBarOffsetSeconds.HasValue && Math.Abs(offset.AudioBarOffsetSeconds.Value - 1.2634) < 1E-12,
            "audio padding offset marker must be available in MIDI metadata");
        EqualRows(SequenceFiles.ReadMidi(Midi("offset not removed", 1, 480, marker, merge), 1),
            new List<ToneRow> { new ToneRow(440, 250, 0), new ToneRow(440, 250, 0) },
            "offset metadata must not change MIDI timeline");
        foreach (string invalid in new string[] { "NaN", "Infinity", "-1", "broken" })
        {
            byte[] invalidMarker = Join(Meta(0, 6, "muscriptor:bar_offset=" + invalid), E(0, 255, 47, 0));
            Assert(!SequenceFiles.ReadMidiTempo(Midi("invalid marker " + invalid, 1, 480, invalidMarker, merge)).AudioBarOffsetSeconds.HasValue,
                "invalid optional offset marker must not poison MIDI metadata");
        }
        byte[] duplicates = Join(E(0, 255, 81, 3, 7, 161, 32), E(480, 255, 81, 3, 7, 161, 32), E(0, 255, 47, 0));
        MidiTempoInfo repeatedTempo = SequenceFiles.ReadMidiTempo(Midi("duplicate tempo", 1, 480, duplicates, merge));
        Assert(repeatedTempo.InitialBpm == 120 && !repeatedTempo.HasTempoChanges && repeatedTempo.TempoEventCount == 2, "duplicate tempo is not a change");
        byte[] sixtyAtZero = Join(E(0, 255, 81, 3, 15, 66, 64), E(480, 255, 47, 0));
        MidiTempoInfo orderedTempo = SequenceFiles.ReadMidiTempo(Midi("tick0 global ordering", 1, 480, tempo, sixtyAtZero));
        Assert(orderedTempo.InitialBpm == 60 && !orderedTempo.HasTempoChanges && orderedTempo.TempoEventCount == 3, "last tick0 global tempo wins");
        byte[] laterSixty = Join(E(240, 255, 81, 3, 15, 66, 64), E(240, 255, 47, 0));
        MidiTempoInfo lateTempo = SequenceFiles.ReadMidiTempo(Midi("later tempo", 1, 480, laterSixty, merge));
        Assert(lateTempo.InitialBpm == 120 && lateTempo.HasTempoChanges && lateTempo.TempoEventCount == 1, "later event keeps initial default120");
        byte[] high = Join(E(0, 144, 127, 100), E(480, 128, 127, 0), E(0, 255, 47, 0));
        EqualRows(SequenceFiles.ReadMidi(Midi("high notes", 0, 480, high), -1), new List<ToneRow> { new ToneRow(12544, 500, 0) }, "frequency above10000");
        Reject(delegate { SequenceFiles.ReadMidi(Midi("format2", 2, 480, merge), -1); }, "format2");
        Reject(delegate { SequenceFiles.ReadMidi(Midi("SMPTE", 0, 59176, merge), -1); }, "SMPTE");
        Reject(delegate { SequenceFiles.ReadMidi(Csv("not midi", "not a MIDI file", false), -1); }, "invalid MIDI tag");

        byte[] referenceTempo = Join(E(0, 255, 81, 3, 9, 39, 192),
            Meta(0, 6, "muscriptor:bar_offset=0.125"), E(0, 255, 47, 0));
        byte[] referenceNotes = Join(E(240, 144, 69, 100), E(240, 128, 69, 0),
            E(0, 144, 69, 100), E(240, 128, 69, 0), E(0, 255, 47, 0));
        string referenceMidi = Midi("generated repeated attacks", 1, 480, referenceTempo, referenceNotes);
        List<ToneRow> referenceRows = SequenceFiles.ReadMidi(referenceMidi, 1);
        List<ToneRow> collapsedReference = new List<ToneRow> { new ToneRow(0, 300, 0), new ToneRow(440, 600, 0) };
        EqualRows(referenceRows, new List<ToneRow> { new ToneRow(0, 300, 0),
            new ToneRow(440, 300, 0), new ToneRow(440, 300, 0) }, "generated MIDI attack boundaries");
        EqualRows(Collapse(referenceRows), collapsedReference, "attack splitting preserves pitch and duration");
        string referenceCsv = Csv("generated reference", "frequency;duration;pause\n0;300;0\n440;600;0", false);
        EqualRows(Collapse(referenceRows), SequenceFiles.ReadCsv(referenceCsv), "independent CSV baseline matches collapsed MIDI timeline");
        List<MidiTrackInfo> referenceTracks = SequenceFiles.ReadMidiTracks(referenceMidi);
        Assert(referenceTracks.Count == 2 && referenceTracks[0].NoteCount == 0 && referenceTracks[1].NoteCount == 2,
            "generated MIDI counts metadata and music tracks separately");
        MidiTempoInfo referenceMetadata = SequenceFiles.ReadMidiTempo(referenceMidi);
        Assert(referenceMetadata.InitialBpm == 100 && !referenceMetadata.HasTempoChanges,
            "generated explicit600000us tempo is100 BPM");
        Assert(referenceMetadata.AudioBarOffsetSeconds.HasValue && referenceMetadata.AudioBarOffsetSeconds.Value == .125,
            "generated marker remains metadata and does not remove leading silence");

    }

    private static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[0]);
            scratch = Path.Combine(root, "build", "verification", "player-data");
            Directory.CreateDirectory(scratch);
            CsvChecks();
            MidiChecks(root);
            Console.WriteLine("PASS: " + checks + " assertions; CSV and MIDI only; hardwareAccess=false");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
