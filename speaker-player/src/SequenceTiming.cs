using System;
using System.Collections.Generic;

namespace SpeakerPlayer
{
    public static class SequenceTiming
    {
        // Scale the cumulative boundaries, not each row separately. This keeps the
        // total equal to round(sourceTotal / speed), including very short notes.
        public static List<ToneRow> Transform(IEnumerable<ToneRow> rows, int transpose,
            double speed, int noteGapMs)
        {
            return Transform(rows, transpose, speed, noteGapMs, null);
        }

        public static List<ToneRow> Transform(IEnumerable<ToneRow> rows, int transpose,
            double speed, int noteGapMs, RhythmSettings rhythm)
        {
            if (rows == null) throw new ArgumentNullException("rows");
            if (Double.IsNaN(speed) || Double.IsInfinity(speed) || speed <= 0)
                throw new ArgumentException("Скорость должна быть больше нуля.", "speed");
            if (noteGapMs < 0)
                throw new ArgumentException("Разрыв между нотами не может быть отрицательным.", "noteGapMs");
            double pitchMultiplier = Math.Pow(2.0, transpose / 12.0);
            if (Double.IsNaN(pitchMultiplier) || Double.IsInfinity(pitchMultiplier) || pitchMultiplier <= 0)
                throw new ArgumentException("Изменение тона слишком велико.", "transpose");

            rhythm = rhythm == null ? new RhythmSettings() : rhythm.Copy();
            double sourceStep = ValidateRhythm(rhythm, speed);
            double phase = sourceStep > 0 ? NormalizePhase(rhythm.PhaseMs, sourceStep) : 0;
            List<ToneRow> source = new List<ToneRow>();
            long sourceTotal = 0;
            foreach (ToneRow row in rows)
            {
                if (row == null || row.Frequency < 0 || row.DurationMs < 0 || row.PauseMs < 0)
                    throw new ArgumentException("Строки должны содержать неотрицательные частоты и длительности.", "rows");
                sourceTotal = checked(sourceTotal + row.DurationMs + row.PauseMs);
                source.Add(new ToneRow(row.Frequency, row.DurationMs, row.PauseMs));
            }

            List<ToneRow> result = new List<ToneRow>();
            long sourceBoundary = 0, scaledBoundary = 0;
            foreach (ToneRow row in source)
            {
                double pitch = Math.Round(row.Frequency * pitchMultiplier);
                if (Double.IsNaN(pitch) || Double.IsInfinity(pitch) || pitch > Int32.MaxValue)
                    throw new ArgumentException("Частота после изменения тона слишком велика.", "transpose");
                int frequency = row.Frequency > 0 ? Math.Max(1, (int)pitch) : 0;

                sourceBoundary = checked(sourceBoundary + row.DurationMs);
                long soundEnd = ScaleBoundary(AdjustBoundary(sourceBoundary, sourceTotal,
                    rhythm.Mode, sourceStep, phase), speed);
                int duration = SegmentLength(scaledBoundary, soundEnd);
                sourceBoundary = checked(sourceBoundary + row.PauseMs);
                long rowEnd = ScaleBoundary(AdjustBoundary(sourceBoundary, sourceTotal,
                    rhythm.Mode, sourceStep, phase), speed);
                int pause = SegmentLength(soundEnd, rowEnd);
                scaledBoundary = rowEnd;

                // Articulation consumes the end of the tone; it never moves the
                // next note. Leave one millisecond of a nonempty tone available.
                int gap = frequency > 0 ? Math.Min(noteGapMs, Math.Max(0, duration - 1)) : 0;
                if (pause > Int32.MaxValue - gap)
                    throw new ArgumentException("Пауза после разрыва ноты слишком велика.", "rows");
                result.Add(new ToneRow(frequency, duration - gap, pause + gap));
            }
            result = Glide(result, rhythm.GlideMs, rhythm.CurveResolutionMs);
            return rhythm.Mode == RhythmMode.Chop ? Chop(result, sourceStep / speed,
                phase / speed, rhythm.SoundPercent) : result;
        }

        private static double ValidateRhythm(RhythmSettings rhythm, double speed)
        {
            if (!Enum.IsDefined(typeof(RhythmMode), rhythm.Mode))
                throw new ArgumentException("Неизвестный режим ритма.", "rhythm");
            if (rhythm.GlideMs < 0)
                throw new ArgumentException("Длительность плавного перехода не может быть отрицательной.", "rhythm");
            if (rhythm.GlideMs > 0 && rhythm.CurveResolutionMs < 1)
                throw new ArgumentException("Шаг плавного перехода должен быть не меньше 1 мс.", "rhythm");
            if (rhythm.Mode == RhythmMode.Original) return 0;
            if (Double.IsNaN(rhythm.SourceBpm) || Double.IsInfinity(rhythm.SourceBpm) || rhythm.SourceBpm <= 0)
                throw new ArgumentException("Для сетки нужен известный положительный BPM файла.", "rhythm");
            if (rhythm.PartsPerBeat != 1 && rhythm.PartsPerBeat != 2 && rhythm.PartsPerBeat != 3 &&
                rhythm.PartsPerBeat != 4 && rhythm.PartsPerBeat != 6 && rhythm.PartsPerBeat != 8 &&
                rhythm.PartsPerBeat != 12 && rhythm.PartsPerBeat != 16)
                throw new ArgumentException("Выберите 1, 2, 3, 4, 6, 8, 12 или 16 частей на долю.", "rhythm");
            if (Double.IsNaN(rhythm.PhaseMs) || Double.IsInfinity(rhythm.PhaseMs))
                throw new ArgumentException("Смещение сетки должно быть конечным числом.", "rhythm");
            if (rhythm.SoundPercent < 0 || rhythm.SoundPercent > 100)
                throw new ArgumentException("Заполнение звуком должно быть от 0 до 100%.", "rhythm");
            double sourceStep = 60000.0 / rhythm.SourceBpm / rhythm.PartsPerBeat;
            double step = sourceStep / speed;
            if (Double.IsInfinity(sourceStep) || Double.IsInfinity(step) || step < 1)
                throw new ArgumentException("Шаг сетки при таком темпе должен быть не меньше 1 мс.", "rhythm");
            return sourceStep;
        }

        private static double NormalizePhase(double phase, double step)
        {
            double normalized = phase % step;
            return normalized < 0 ? normalized + step : normalized;
        }

        private static double AdjustBoundary(long boundary, long total, RhythmMode mode,
            double step, double phase)
        {
            if (mode != RhythmMode.Quantize || boundary == 0 || boundary == total) return boundary;
            double nearest = phase + Math.Round((boundary - phase) / step,
                MidpointRounding.AwayFromZero) * step;
            // Halfway snapping stays monotone and keeps rests as rests. Their
            // boundaries may move together with neighboring note boundaries.
            return Math.Max(0, Math.Min(total, boundary * 0.5 + nearest * 0.5));
        }

        private static List<ToneRow> Chop(List<ToneRow> rows, double step, double phase, int soundPercent)
        {
            if (rows.Count > 200000)
                throw new ArgumentException("Дробление создаёт больше 200 000 участков. Увеличьте шаг сетки или сократите трек.");
            if (soundPercent == 100) return rows;
            List<ToneRow> chopped = new List<ToneRow>();
            long position = 0;
            foreach (ToneRow row in rows)
            {
                if (row.Frequency == 0 || row.DurationMs == 0 || soundPercent == 0)
                {
                    AddChopped(chopped, new ToneRow(soundPercent == 0 ? 0 : row.Frequency, row.DurationMs, row.PauseMs));
                    position = checked(position + row.DurationMs + row.PauseMs);
                    continue;
                }
                long end = checked(position + row.DurationMs);
                double cursor = position;
                long previousBoundary = position;
                int before = chopped.Count;
                while (cursor < end)
                {
                    // A computed cell end may divide back to n-epsilon. Treat
                    // that floating-point residue as the next exact boundary.
                    double cell = Math.Floor((cursor - phase) / step + 1E-10);
                    double cellStart = phase + cell * step;
                    double soundEnd = cellStart + step * soundPercent / 100.0;
                    double cellEnd = cellStart + step;
                    bool sounding = cursor < soundEnd;
                    double next = Math.Min(end, sounding ? soundEnd : cellEnd);
                    if (next <= cursor)
                        throw new ArgumentException("Точности временной сетки недостаточно для дробления.");
                    long boundary = Math.Max(previousBoundary, Math.Min(end, (long)Math.Round(next)));
                    int duration = SegmentLength(previousBoundary, boundary);
                    if (duration > 0) AddChopped(chopped, new ToneRow(sounding ? row.Frequency : 0, duration, 0));
                    previousBoundary = boundary;
                    cursor = next;
                }
                if (chopped.Count == before) AddChopped(chopped, new ToneRow(row.Frequency, row.DurationMs, 0));
                ToneRow last = chopped[chopped.Count - 1];
                last.PauseMs = checked(last.PauseMs + row.PauseMs);
                position = checked(end + row.PauseMs);
            }
            return chopped;
        }

        private static List<ToneRow> Glide(List<ToneRow> rows, int glideMs, int resolutionMs)
        {
            if (glideMs == 0) return rows;
            List<ToneRow> result = new List<ToneRow>();
            for (int i = 0; i < rows.Count; i++)
            {
                ToneRow row = rows[i];
                ToneRow previous = i == 0 ? null : rows[i - 1];
                int duration = Math.Min(glideMs, row.DurationMs);
                // A real rest or articulation pause ends the previous note;
                // glide never writes a ramp into that silence. Apply before
                // Chop so artificial gate slices do not become new note glides.
                if (previous == null || previous.Frequency == 0 || previous.DurationMs == 0 || previous.PauseMs > 0 ||
                    row.Frequency == 0 || row.Frequency == previous.Frequency || duration < 2)
                {
                    AddChopped(result, new ToneRow(row.Frequency, row.DurationMs, row.PauseMs));
                    continue;
                }
                int pieces = Math.Min(duration, Math.Max(2, (int)Math.Ceiling(duration / (double)resolutionMs)));
                int previousBoundary = 0;
                double startPitch = Math.Log(previous.Frequency);
                double pitchDistance = Math.Log(row.Frequency) - startPitch;
                for (int piece = 0; piece < pieces; piece++)
                {
                    double progress = piece / (double)(pieces - 1);
                    double eased = progress * progress * (3 - 2 * progress);
                    int frequency = piece == 0 ? previous.Frequency : piece == pieces - 1 ? row.Frequency :
                        (int)Math.Round(Math.Exp(startPitch + pitchDistance * eased));
                    int boundary = (int)Math.Round(duration * (piece + 1) / (double)pieces);
                    AddChopped(result, new ToneRow(Math.Max(1, frequency), boundary - previousBoundary, 0));
                    previousBoundary = boundary;
                }
                ToneRow last = result[result.Count - 1];
                last.DurationMs = checked(last.DurationMs + row.DurationMs - duration);
                last.PauseMs = row.PauseMs;
            }
            return result;
        }

        private static void AddChopped(List<ToneRow> rows, ToneRow row)
        {
            if (rows.Count >= 200000)
                throw new ArgumentException("Обработка создаёт больше 200 000 участков. Увеличьте шаг или сократите трек.");
            rows.Add(row);
        }

        private static long ScaleBoundary(double milliseconds, double speed)
        {
            double scaled = Math.Round(milliseconds / speed);
            if (Double.IsNaN(scaled) || Double.IsInfinity(scaled) || scaled >= 9223372036854775808.0)
                throw new ArgumentException("Длительность при такой скорости слишком велика.", "speed");
            return (long)scaled;
        }

        private static int SegmentLength(long start, long end)
        {
            long length = end - start;
            if (length < 0 || length > Int32.MaxValue)
                throw new ArgumentException("Длительность строки при такой скорости слишком велика.");
            return (int)length;
        }
    }
}
