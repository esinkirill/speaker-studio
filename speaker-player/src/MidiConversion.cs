using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace SpeakerPlayer
{
    public sealed class MidiConversionResult
    {
        public string OutputPath { get; internal set; }
        public string ReportPath { get; internal set; }
        public string RhythmReportPath { get; internal set; }
        public int NoteCount { get; internal set; }
        public int InstrumentCount { get; internal set; }
        public int DrumCount { get; internal set; }
        public double AudioDurationSeconds { get; internal set; }
        public double Bpm { get; internal set; }
        public bool BpmWasEstimated { get; internal set; }
        public double InferenceSeconds { get; internal set; }
    }

    /// <summary>Runs local bundled processes. Prepare is called before queuing a worker.</summary>
    public sealed class MidiConversion : IDisposable
    {
        private readonly string root;
        private readonly ChildProcessRunner runner = new ChildProcessRunner();
        public MidiConversion(string baseDirectory) { root = Path.GetFullPath(baseDirectory); }
        public bool RuntimeReady {
            get {
                return File.Exists(Path.Combine(root, "runtime", "transcription", "python.exe")) &&
                    File.Exists(Path.Combine(root, "converter", "transcribe-midi.py")) &&
                    File.Exists(Path.Combine(root, "models", "muscriptor-small", "model.safetensors")) &&
                    File.Exists(Path.Combine(root, "models", "muscriptor-small", "config.json")) &&
                    File.Exists(Path.Combine(root, "assets", "ffmpeg", "ffmpeg.exe"));
            }
        }
        public void Prepare() { runner.Prepare(); }
        public void Cancel() { runner.Cancel(); }
        public void Dispose() { runner.Dispose(); }

        public MidiConversionResult Convert(string audioPath, string outputPath, Action<string> log)
        {
            if (!RuntimeReady) throw new FileNotFoundException("MP3 → MIDI не подготовлен. Запусти Prepare-Audio.cmd рядом с приложением; для сборки из исходников добавь runtime и MuScriptor Small по docs/MODEL.md.");
            audioPath = Path.GetFullPath(audioPath);
            outputPath = Path.GetFullPath(outputPath);
            if (!File.Exists(audioPath)) throw new FileNotFoundException("Аудиофайл не найден.", audioPath);
            if (String.Equals(audioPath, outputPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Результат не может заменять исходное аудио.");
            if (File.Exists(outputPath)) throw new IOException("MIDI с таким именем уже существует.");
            Action<string> write = log ?? delegate(string ignored) { };
            string reportPath = Path.ChangeExtension(outputPath, ".transcription.json");
            string rhythmPath = Path.ChangeExtension(outputPath, ".rhythm.json");
            double bpm = 120;
            bool estimated = false;
            string node = Path.Combine(root, "runtime", "node.exe");
            string rhythmScript = Path.Combine(root, "converter", "analyze-rhythm.cjs");
            if (File.Exists(node) && File.Exists(rhythmScript)) {
                write("Определяем ритм исходного аудио…");
                try {
                    runner.Run(node, new[] { rhythmScript, "--input", audioPath, "--output", rhythmPath }, root, write);
                    RhythmAnalysisResult rhythm = RhythmAnalysis.Load(rhythmPath);
                    bpm = rhythm.Bpm;
                    estimated = true;
                    write(String.Format(CultureInfo.InvariantCulture, "BPM ≈ {0:0.00}; найдено {1} долей. Это оценка ритма.", bpm, rhythm.BeatCount));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { write("BPM не определён; MIDI сохранит исходные времена. " + error.Message); }
            }
            string python = Path.Combine(root, "runtime", "transcription", "python.exe");
            string script = Path.Combine(root, "converter", "transcribe-midi.py");
            runner.Run(python, new[] { "-I", "-B", script, "--input", audioPath, "--output", outputPath,
                "--report", reportPath, "--bpm", bpm.ToString("R", CultureInfo.InvariantCulture),
                "--bpm-source", estimated ? "estimated" : "unknown" }, root, write);
            MidiConversionResult result = ReadResult(reportPath);
            if (!String.Equals(Path.GetFullPath(result.OutputPath), outputPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(outputPath))
                throw new InvalidDataException("Конвертер не создал ожидаемый MIDI.");
            // Reuse the player's actual MIDI reader; successful process exit alone is insufficient.
            SequenceFiles.ReadMidiTracks(outputPath);
            result.RhythmReportPath = File.Exists(rhythmPath) ? rhythmPath : null;
            return result;
        }

        public static MidiConversionResult ReadResult(string reportPath)
        {
            JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 32 };
            Dictionary<string, object> values = json.DeserializeObject(File.ReadAllText(reportPath)) as Dictionary<string, object>;
            if (values == null || Text(values, "format") != "speaker-midi-transcription-v1") throw new InvalidDataException("Некорректный отчёт конвертации MIDI.");
            MidiConversionResult result = new MidiConversionResult {
                OutputPath = Text(values, "output"), ReportPath = Path.GetFullPath(reportPath),
                NoteCount = Count(values, "noteCount"), InstrumentCount = Count(values, "instrumentCount"),
                DrumCount = Count(values, "drumCount"), AudioDurationSeconds = Number(values, "audioDurationSeconds"),
                Bpm = Number(values, "bpm"), BpmWasEstimated = Text(values, "bpmSource") == "estimated",
                InferenceSeconds = Number(values, "inferenceSeconds"),
            };
            if (result.AudioDurationSeconds <= 0 || result.Bpm <= 0 || result.InferenceSeconds < 0 || result.DrumCount > result.NoteCount)
                throw new InvalidDataException("Некорректные значения отчёта конвертации.");
            return result;
        }
        private static object Required(Dictionary<string, object> values, string key)
        {
            object value;
            if (!values.TryGetValue(key, out value)) throw new InvalidDataException("В отчёте отсутствует " + key + ".");
            return value;
        }
        private static string Text(Dictionary<string, object> values, string key)
        {
            string value = Required(values, key) as string;
            if (String.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Некорректное поле " + key + ".");
            return value;
        }
        private static double Number(Dictionary<string, object> values, string key)
        {
            object raw = Required(values, key);
            if (!(raw is int) && !(raw is long) && !(raw is double) && !(raw is decimal)) throw new InvalidDataException("Некорректное число " + key + ".");
            double value = System.Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new InvalidDataException("Некорректное число " + key + ".");
            return value;
        }
        private static int Count(Dictionary<string, object> values, string key)
        {
            double value = Number(values, key);
            if (value < 0 || value > Int32.MaxValue || value != Math.Floor(value)) throw new InvalidDataException("Некорректный счётчик " + key + ".");
            return (int)value;
        }
    }
}
