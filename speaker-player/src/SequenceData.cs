using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SpeakerPlayer
{
    public sealed class ToneRow
    {
        public int Frequency;
        public int DurationMs;
        public int PauseMs;

        public ToneRow(int frequency, int durationMs, int pauseMs)
        {
            Frequency = frequency;
            DurationMs = durationMs;
            PauseMs = pauseMs;
        }
    }

    public sealed class MidiTrackInfo
    {
        public int Index;
        public int NoteCount;
        public string Name;

        public override string ToString()
        {
            return String.Format(CultureInfo.InvariantCulture, "{0}: {1} ({2} нот)",
                Index, String.IsNullOrWhiteSpace(Name) ? "Без названия" : Name, NoteCount);
        }
    }

    public sealed class MidiTempoInfo
    {
        public double InitialBpm;
        public bool HasTempoChanges;
        public int TempoEventCount;
        public double? AudioBarOffsetSeconds;
    }

    public static class SequenceFiles
    {
        public static List<ToneRow> ReadCsv(string path)
        {
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            int first = 0;
            while (first < lines.Length && String.IsNullOrWhiteSpace(lines[first])) first++;
            if (first == lines.Length) throw new InvalidDataException("CSV пуст.");
            string header = lines[first].TrimStart('\uFEFF').Trim();
            char delimiter = DetectDelimiter(header);
            if (header.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) && header.Length == 5)
            {
                delimiter = header[4];
                first++;
                while (first < lines.Length && String.IsNullOrWhiteSpace(lines[first])) first++;
                if (first == lines.Length) throw new InvalidDataException("В CSV нет заголовка.");
                header = lines[first];
            }
            string[] names = SplitCsvLine(header, delimiter);
            int frequencyColumn = ColumnIndex(names, "frequency");
            int durationColumn = ColumnIndex(names, "duration");
            int pauseColumn = ColumnIndex(names, "pause");
            if (names.Length != 3 || frequencyColumn < 0 || durationColumn < 0 || pauseColumn < 0)
                throw new InvalidDataException("Нужны три столбца CSV: frequency;duration;pause.");

            List<ToneRow> rows = new List<ToneRow>();
            for (int line = first + 1; line < lines.Length; line++)
            {
                if (String.IsNullOrWhiteSpace(lines[line])) continue;
                string[] fields = SplitCsvLine(lines[line], delimiter);
                if (fields.Length != 3)
                    throw new InvalidDataException("В строке " + (line + 1) + " CSV должно быть три значения.");
                rows.Add(new ToneRow(
                    ReadNonnegative(fields[frequencyColumn], "frequency", line + 1),
                    ReadNonnegative(fields[durationColumn], "duration", line + 1),
                    ReadNonnegative(fields[pauseColumn], "pause", line + 1)));
            }
            if (rows.Count == 0) throw new InvalidDataException("В CSV нет нот или пауз.");
            return rows;
        }

        public static void WriteCsv(string path, IEnumerable<ToneRow> rows)
        {
            StringBuilder csv = new StringBuilder("frequency;duration;pause\r\n");
            foreach (ToneRow row in rows)
            {
                if (row == null || row.Frequency < 0 || row.DurationMs < 0 || row.PauseMs < 0)
                    throw new InvalidDataException("Частота, длительность и пауза должны быть целыми неотрицательными числами.");
                csv.Append(row.Frequency.ToString(CultureInfo.InvariantCulture)).Append(';')
                    .Append(row.DurationMs.ToString(CultureInfo.InvariantCulture)).Append(';')
                    .Append(row.PauseMs.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }
            File.WriteAllText(path, csv.ToString(), Encoding.ASCII);
        }

        private static int ColumnIndex(string[] names, string wanted)
        {
            for (int i = 0; i < names.Length; i++)
                if (String.Equals(names[i].Trim().TrimStart('\uFEFF'), wanted, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static int ReadNonnegative(string value, string name, int line)
        {
            int number;
            if (!Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 0)
                throw new InvalidDataException("Строка " + line + ": " + name + " должен быть целым неотрицательным числом.");
            return number;
        }

        private static char DetectDelimiter(string header)
        {
            bool quoted = false;
            for (int i = 0; i < header.Length; i++)
            {
                char ch = header[i];
                if (ch == '"') quoted = !quoted;
                else if (!quoted && (ch == ';' || ch == ',' || ch == '\t')) return ch;
            }
            return ';';
        }

        private static string[] SplitCsvLine(string line, char delimiter)
        {
            List<string> fields = new List<string>();
            StringBuilder field = new StringBuilder();
            bool quoted = false;
            bool closedQuote = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (quoted)
                {
                    if (ch != '"') field.Append(ch);
                    else if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                    else { quoted = false; closedQuote = true; }
                }
                else if (ch == delimiter)
                {
                    fields.Add(field.ToString().Trim());
                    field.Length = 0;
                    closedQuote = false;
                }
                else if (ch == '"')
                {
                    if (closedQuote || field.ToString().Trim().Length != 0)
                        throw new InvalidDataException("Некорректные кавычки в CSV.");
                    field.Length = 0;
                    quoted = true;
                }
                else
                {
                    if (closedQuote && !Char.IsWhiteSpace(ch))
                        throw new InvalidDataException("После закрывающей кавычки CSV нужен разделитель.");
                    field.Append(ch);
                }
            }
            if (quoted) throw new InvalidDataException("Незакрытая кавычка в CSV.");
            fields.Add(field.ToString().Trim());
            return fields.ToArray();
        }

        private sealed class Reader
        {
            public byte[] Data;
            public int Position;
            public int End;
            public Reader(byte[] data, int start, int end) { Data = data; Position = start; End = end; }
            public int Byte()
            {
                if (Position >= End) throw new InvalidDataException("Unexpected end of MIDI data.");
                return Data[Position++];
            }
            public int Word() { return (Byte() << 8) | Byte(); }
            public int Length()
            {
                uint value = ((uint)Byte() << 24) | ((uint)Byte() << 16) | ((uint)Byte() << 8) | (uint)Byte();
                if (value > int.MaxValue) throw new InvalidDataException("MIDI chunk is too large.");
                return (int)value;
            }
            public int Vlq()
            {
                int value = 0;
                for (int i = 0; i < 4; i++)
                {
                    int b = Byte();
                    value = (value << 7) | (b & 127);
                    if ((b & 128) == 0) return value;
                }
                throw new InvalidDataException("Invalid MIDI variable-length value.");
            }
            public int DataByte()
            {
                int b = Byte();
                if (b >= 128) throw new InvalidDataException("Expected a MIDI data byte.");
                return b;
            }
            public string Tag() { return Encoding.ASCII.GetString(new byte[] { (byte)Byte(), (byte)Byte(), (byte)Byte(), (byte)Byte() }); }
            public void Skip(int length)
            {
                if (length < 0 || length > End - Position) throw new InvalidDataException("Invalid MIDI chunk/event length.");
                Position += length;
            }
            public string Text(int length)
            {
                int start = Position;
                Skip(length);
                return Encoding.UTF8.GetString(Data, start, length).TrimEnd('\0');
            }
        }

        private sealed class Event
        {
            public long Tick;
            public int Order;
            public int Key;
            public int Note;
            public bool On;
            public int Tempo;
        }

        private sealed class Segment
        {
            public int Frequency;
            public double Start;
            public double End;
            public bool StartsNote;
        }

        public static List<MidiTrackInfo> ReadMidiTracks(string path)
        {
            List<MidiTrackInfo> tracks;
            MidiTempoInfo tempo;
            ReadMidiCore(path, -1, true, out tracks, out tempo);
            return tracks;
        }

        public static List<ToneRow> ReadMidi(string path, int selectedTrack)
        {
            List<MidiTrackInfo> tracks;
            MidiTempoInfo tempo;
            return ReadMidiCore(path, selectedTrack, false, out tracks, out tempo);
        }

        public static MidiTempoInfo ReadMidiTempo(string path)
        {
            List<MidiTrackInfo> tracks;
            MidiTempoInfo tempo;
            ReadMidiCore(path, -1, true, out tracks, out tempo);
            return tempo;
        }

        // The timing/polyphony algorithm is the existing midi-to-csv converter.
        // Track names/counts are metadata; tempo events still apply globally.
        private static List<ToneRow> ReadMidiCore(string input, int selectedTrack, bool listOnly, out List<MidiTrackInfo> trackInfo, out MidiTempoInfo tempoInfo)
        {
            byte[] bytes = File.ReadAllBytes(input);
            Reader reader = new Reader(bytes, 0, bytes.Length);
            if (reader.Tag() != "MThd") throw new InvalidDataException("Expected a Standard MIDI file (.mid/.midi).");
            int headerLength = reader.Length();
            if (headerLength < 6 || headerLength > reader.End - reader.Position) throw new InvalidDataException("Invalid MIDI header length.");
            int format = reader.Word();
            int tracks = reader.Word();
            int division = reader.Word();
            reader.Skip(headerLength - 6);
            if (format == 2) throw new NotSupportedException("MIDI format 2 contains independent sequences; export format 0 or 1 instead.");
            if (format != 0 && format != 1) throw new NotSupportedException("Only Standard MIDI formats 0 and 1 are supported.");
            if ((division & 0x8000) != 0) throw new NotSupportedException("SMPTE MIDI timing is not supported; export with ticks per quarter note instead.");
            if (division == 0 || tracks == 0 || (format == 0 && tracks != 1)) throw new InvalidDataException("Invalid MIDI header values.");
            if (selectedTrack < -1 || selectedTrack >= tracks) throw new ArgumentOutOfRangeException("selectedTrack", "Track must be -1 (all) or an index from 0 to " + (tracks - 1) + ".");

            trackInfo = new List<MidiTrackInfo>();
            List<Event> events = new List<Event>();
            long lastTick = 0;
            int order = 0;
            int noteOnCount = 0;
            double? audioBarOffsetSeconds = null;
            for (int track = 0; track < tracks; )
            {
                string tag = reader.Tag();
                int length = reader.Length();
                if (length > reader.End - reader.Position) throw new InvalidDataException("Truncated MIDI track.");
                int end = reader.Position + length;
                if (tag != "MTrk") { reader.Skip(length); continue; }
                Reader part = new Reader(bytes, reader.Position, end);
                bool selected = selectedTrack == -1 || selectedTrack == track;
                MidiTrackInfo info = new MidiTrackInfo { Index = track, Name = String.Empty };
                long tick = 0;
                int runningStatus = 0;
                while (part.Position < part.End)
                {
                    tick += part.Vlq();
                    int status = part.Byte();
                    if (status < 128)
                    {
                        if (runningStatus == 0) throw new InvalidDataException("MIDI running status has no preceding channel message.");
                        part.Position--;
                        status = runningStatus;
                    }
                    if (status >= 0x80 && status <= 0xEF)
                    {
                        runningStatus = status;
                        int kind = status & 0xF0;
                        int channel = status & 15;
                        int a = part.DataByte();
                        int b = (kind == 0xC0 || kind == 0xD0) ? 0 : part.DataByte();
                        if (channel != 9 && kind == 0x90 && b != 0) info.NoteCount++;
                        if (selected && channel != 9 && (kind == 0x80 || kind == 0x90))
                        {
                            bool on = kind == 0x90 && b != 0;
                            events.Add(new Event { Tick = tick, Order = order++, Key = track * 2048 + channel * 128 + a, Note = a, On = on });
                            if (on) noteOnCount++;
                        }
                    }
                    else if (status == 0xFF)
                    {
                        runningStatus = 0;
                        int type = part.Byte();
                        int metaLength = part.Vlq();
                        if (type == 0x51)
                        {
                            if (metaLength != 3) throw new InvalidDataException("Invalid MIDI tempo event.");
                            int tempo = (part.Byte() << 16) | (part.Byte() << 8) | part.Byte();
                            if (tempo == 0) throw new InvalidDataException("MIDI tempo must be positive.");
                            events.Add(new Event { Tick = tick, Order = order++, Tempo = tempo });
                        }
                        else if (type == 0x03) info.Name = part.Text(metaLength);
                        else if (type == 0x06)
                        {
                            string marker = part.Text(metaLength);
                            const string prefix = "muscriptor:bar_offset=";
                            double offset;
                            if (!audioBarOffsetSeconds.HasValue && marker.StartsWith(prefix, StringComparison.Ordinal) &&
                                Double.TryParse(marker.Substring(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out offset) &&
                                !Double.IsNaN(offset) && !Double.IsInfinity(offset) && offset >= 0)
                                audioBarOffsetSeconds = offset;
                        }
                        else
                        {
                            part.Skip(metaLength);
                            if (type == 0x2F) break;
                        }
                    }
                    else if (status == 0xF0 || status == 0xF7)
                    {
                        runningStatus = 0;
                        part.Skip(part.Vlq());
                    }
                    else throw new InvalidDataException("Unsupported status byte in Standard MIDI track.");
                }
                trackInfo.Add(info);
                if (selected) lastTick = Math.Max(lastTick, tick);
                reader.Position = end;
                track++;
            }
            events.Sort(delegate(Event a, Event b) { int tickOrder = a.Tick.CompareTo(b.Tick); return tickOrder != 0 ? tickOrder : a.Order.CompareTo(b.Order); });
            tempoInfo = DescribeTempo(events);
            tempoInfo.AudioBarOffsetSeconds = audioBarOffsetSeconds;
            if (listOnly) return new List<ToneRow>();
            if (noteOnCount == 0) throw new InvalidDataException("No playable notes found. Channel 10 percussion is excluded; try another track.");

            int[] pitches = new int[128];
            Dictionary<int, int> voices = new Dictionary<int, int>();
            List<Segment> segments = new List<Segment>();
            int tempoUs = 500000;
            long previousTick = 0;
            double milliseconds = 0;
            int position = 0;
            bool startsNote = false;
            while (position < events.Count)
            {
                long tick = events[position].Tick;
                if (tick > lastTick) break;
                AddSegment(segments, pitches, milliseconds, milliseconds + (tick - previousTick) * (double)tempoUs / division / 1000.0, startsNote);
                milliseconds += (tick - previousTick) * (double)tempoUs / division / 1000.0;
                previousTick = tick;
                HashSet<int> attacks = new HashSet<int>();
                while (position < events.Count && events[position].Tick == tick)
                {
                    Event e = events[position++];
                    if (e.Tempo != 0) { tempoUs = e.Tempo; continue; }
                    int count;
                    voices.TryGetValue(e.Key, out count);
                    if (e.On) { voices[e.Key] = count + 1; pitches[e.Note]++; attacks.Add(e.Note); }
                    else if (count > 0) { voices[e.Key] = count - 1; pitches[e.Note]--; }
                }
                startsNote = attacks.Contains(HighestNote(pitches));
            }
            AddSegment(segments, pitches, milliseconds, milliseconds + (lastTick - previousTick) * (double)tempoUs / division / 1000.0, startsNote);

            List<ToneRow> rows = new List<ToneRow>();
            foreach (Segment segment in segments)
            {
                double rounded = Math.Round(segment.End, MidpointRounding.AwayFromZero) - Math.Round(segment.Start, MidpointRounding.AwayFromZero);
                if (rounded <= 0) continue;
                if (rounded > int.MaxValue) throw new InvalidDataException("A MIDI interval is too long for the player.");
                int duration = (int)rounded;
                if (!segment.StartsNote && rows.Count > 0 && rows[rows.Count - 1].Frequency == segment.Frequency)
                    rows[rows.Count - 1].DurationMs = checked(rows[rows.Count - 1].DurationMs + duration);
                else rows.Add(new ToneRow(segment.Frequency, duration, 0));
            }
            if (rows.Count == 0) throw new InvalidDataException("MIDI notes are too short to represent in milliseconds.");
            return rows;
        }

        private static MidiTempoInfo DescribeTempo(List<Event> sortedEvents)
        {
            MidiTempoInfo info = new MidiTempoInfo();
            int initialTempoUs = 500000;
            foreach (Event e in sortedEvents)
            {
                if (e.Tempo == 0) continue;
                info.TempoEventCount++;
                // Multiple tick-zero events have the same ordering as ReadMidi.
                if (e.Tick == 0) initialTempoUs = e.Tempo;
            }
            info.InitialBpm = 60000000.0 / initialTempoUs;
            int previousTempoUs = initialTempoUs;
            foreach (Event e in sortedEvents)
            {
                if (e.Tempo == 0 || e.Tick == 0) continue;
                if (e.Tempo != previousTempoUs) info.HasTempoChanges = true;
                previousTempoUs = e.Tempo;
            }
            return info;
        }

        private static int HighestNote(int[] pitches)
        {
            int note = 127;
            while (note >= 0 && pitches[note] == 0) note--;
            return note;
        }

        private static void AddSegment(List<Segment> segments, int[] pitches, double start, double end, bool startsNote)
        {
            if (end <= start) return;
            int note = HighestNote(pitches);
            int frequency = note < 0 ? 0 : (int)Math.Round(440.0 * Math.Pow(2.0, ((double)note - 69.0) / 12.0), MidpointRounding.AwayFromZero);
            startsNote = note >= 0 && startsNote;
            if (!startsNote && segments.Count > 0 && segments[segments.Count - 1].Frequency == frequency)
                segments[segments.Count - 1].End = end;
            else segments.Add(new Segment { Frequency = frequency, Start = start, End = end, StartsNote = startsNote });
        }
    }
}
