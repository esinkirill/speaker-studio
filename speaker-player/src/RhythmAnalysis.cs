using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace SpeakerPlayer
{
    /// <summary>Estimated beats in source-audio time; confidence is not a probability.</summary>
    public sealed class RhythmAnalysisResult
    {
        public string SourcePath { get; internal set; }
        public string ReportPath { get; internal set; }
        public double Bpm { get; internal set; }
        public double RawBpm { get; internal set; }
        public double? FittedBpm { get; internal set; }
        public bool UsedFittedBpm { get; internal set; }
        public bool StableGrid { get; internal set; }
        public double Confidence { get; internal set; }
        public double[] BeatTicksSeconds { get; internal set; }
        public int BeatCount { get { return BeatTicksSeconds.Length; } }
        public double AudioDurationSeconds { get; internal set; }
        public double AnalysisSeconds { get; internal set; }
        public double DecodeSeconds { get; internal set; }
        public double TotalSeconds { get; internal set; }
        public double PeriodSeconds { get; internal set; }
        public double PhaseSeconds { get; internal set; }
        public double? FittedPeriodSeconds { get; internal set; }
        public double? FittedPhaseSeconds { get; internal set; }
        public double? RmsResidualMs { get; internal set; }
        public double? MaxResidualMs { get; internal set; }

        public static RhythmAnalysisResult Load(string jsonPath) { return RhythmAnalysis.Load(jsonPath); }
    }

    public static class RhythmAnalysis
    {
        public static RhythmAnalysisResult Load(string jsonPath)
        {
            if (String.IsNullOrWhiteSpace(jsonPath)) throw new ArgumentException("Укажите JSON отчёт анализа ритма.", "jsonPath");
            string reportPath = Path.GetFullPath(jsonPath);
            string json = File.ReadAllText(reportPath);
            if (json.Length > 16 * 1024 * 1024) throw Invalid("Слишком большой отчёт анализа ритма.");
            Dictionary<string, object> values;
            try {
                JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 32 };
                values = serializer.DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (ArgumentException ex) { throw new InvalidDataException("Некорректный JSON отчёта анализа ритма.", ex); }
            catch (InvalidOperationException ex) { throw new InvalidDataException("Некорректный JSON отчёта анализа ритма.", ex); }
            if (values == null || Text(values, "format") != "speaker-rhythm-analysis-v1") throw Invalid("Неизвестный формат отчёта анализа ритма.");

            RhythmAnalysisResult result = new RhythmAnalysisResult {
                SourcePath = Text(values, "source"),
                ReportPath = reportPath,
                Bpm = Positive(values, "bpm"),
                RawBpm = Positive(values, "rawBpm"),
                FittedBpm = OptionalNumber(values, "fittedBpm"),
                UsedFittedBpm = Boolean(values, "usedFittedBpm"),
                StableGrid = Boolean(values, "stableGrid"),
                Confidence = Nonnegative(values, "confidence"),
                AudioDurationSeconds = Positive(values, "audioDurationSeconds"),
                AnalysisSeconds = Nonnegative(values, "analysisSeconds"),
                DecodeSeconds = Nonnegative(values, "decodeSeconds"),
                TotalSeconds = Nonnegative(values, "totalSeconds"),
                PeriodSeconds = Positive(values, "periodSeconds"),
                PhaseSeconds = Number(values, "phaseSeconds"),
                FittedPeriodSeconds = OptionalNumber(values, "fittedPeriodSeconds"),
                FittedPhaseSeconds = OptionalNumber(values, "fittedPhaseSeconds"),
                RmsResidualMs = OptionalNumber(values, "rmsResidualMs"),
                MaxResidualMs = OptionalNumber(values, "maxResidualMs"),
            };
            if (String.IsNullOrWhiteSpace(result.SourcePath)) throw Invalid("Отсутствует путь исходного аудио.");
            if (Boolean(values, "confidenceIsProbability")) throw Invalid("Confidence анализа ритма не является вероятностью.");
            object rawTicks = Required(values, "beatTicksSeconds");
            IList ticks = rawTicks as IList;
            if (ticks == null || ticks.Count < 2) throw Invalid("Недостаточно найденных долей.");
            result.BeatTicksSeconds = new double[ticks.Count];
            for (int i = 0; i < ticks.Count; i++) {
                double tick = NumericValue(ticks[i], "beatTicksSeconds");
                if (tick < 0 || tick > result.AudioDurationSeconds || (i > 0 && tick <= result.BeatTicksSeconds[i - 1]))
                    throw Invalid("Позиции долей должны возрастать и находиться в пределах аудио.");
                result.BeatTicksSeconds[i] = tick;
            }
            if (Number(values, "beatCount") != result.BeatCount) throw Invalid("Число долей не совпадает с их списком.");
            ValidateFit(result);
            return result;
        }

        private static void ValidateFit(RhythmAnalysisResult result)
        {
            bool hasFit = result.FittedBpm.HasValue;
            if (hasFit != result.FittedPeriodSeconds.HasValue || hasFit != result.FittedPhaseSeconds.HasValue ||
                hasFit != result.RmsResidualMs.HasValue || hasFit != result.MaxResidualMs.HasValue)
                throw Invalid("Неполные параметры регулярной сетки.");
            if (hasFit && (result.FittedBpm.Value <= 0 || result.FittedPeriodSeconds.Value <= 0 ||
                result.RmsResidualMs.Value < 0 || result.MaxResidualMs.Value < result.RmsResidualMs.Value))
                throw Invalid("Некорректные параметры регулярной сетки.");
            bool stable = hasFit && result.BeatCount >= 8 && result.RmsResidualMs.Value <= 20 && result.MaxResidualMs.Value <= 50;
            if (result.StableGrid != stable || result.UsedFittedBpm != stable) throw Invalid("Некорректный выбор оценки BPM.");
            double selected = result.UsedFittedBpm ? result.FittedBpm.Value : result.RawBpm;
            if (!Close(result.Bpm, selected) || !Close(result.PeriodSeconds * result.Bpm, 60))
                throw Invalid("BPM и период доли не согласованы.");
            if (hasFit && !Close(result.FittedPeriodSeconds.Value * result.FittedBpm.Value, 60))
                throw Invalid("Период рассчитанной сетки не согласован с BPM.");
            double phase = result.UsedFittedBpm ? result.FittedPhaseSeconds.Value : result.BeatTicksSeconds[0];
            if (!Close(result.PhaseSeconds, phase) || Math.Abs(result.PhaseSeconds) > result.AudioDurationSeconds + result.PeriodSeconds)
                throw Invalid("Некорректное смещение сетки.");
            if (result.TotalSeconds + 0.001 < result.DecodeSeconds + result.AnalysisSeconds)
                throw Invalid("Время анализа не согласовано с общим временем.");
        }

        private static bool Close(double left, double right) { return Math.Abs(left - right) <= 1e-8 * Math.Max(1, Math.Abs(right)); }
        private static InvalidDataException Invalid(string message) { return new InvalidDataException(message); }
        private static object Required(Dictionary<string, object> values, string key)
        {
            object value;
            if (!values.TryGetValue(key, out value)) throw Invalid("В отчёте отсутствует " + key + ".");
            return value;
        }
        private static string Text(Dictionary<string, object> values, string key)
        {
            string value = Required(values, key) as string;
            if (value == null) throw Invalid("Поле " + key + " должно быть строкой.");
            return value;
        }
        private static bool Boolean(Dictionary<string, object> values, string key)
        {
            object value = Required(values, key);
            if (!(value is bool)) throw Invalid("Поле " + key + " должно быть boolean.");
            return (bool)value;
        }
        private static double NumericValue(object value, string key)
        {
            if (!(value is int) && !(value is long) && !(value is double) && !(value is decimal))
                throw Invalid("Поле " + key + " должно быть числом.");
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Double.IsNaN(number) || Double.IsInfinity(number)) throw Invalid("Поле " + key + " должно быть конечным числом.");
            return number;
        }
        private static double Number(Dictionary<string, object> values, string key) { return NumericValue(Required(values, key), key); }
        private static double Positive(Dictionary<string, object> values, string key)
        {
            double value = Number(values, key);
            if (value <= 0) throw Invalid("Поле " + key + " должно быть положительным.");
            return value;
        }
        private static double Nonnegative(Dictionary<string, object> values, string key)
        {
            double value = Number(values, key);
            if (value < 0) throw Invalid("Поле " + key + " не должно быть отрицательным.");
            return value;
        }
        private static double? OptionalNumber(Dictionary<string, object> values, string key)
        {
            object value = Required(values, key);
            return value == null ? (double?)null : NumericValue(value, key);
        }
    }
}
