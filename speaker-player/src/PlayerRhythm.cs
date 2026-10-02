using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SpeakerPlayer
{
    public sealed partial class PlayerForm
    {
        private readonly ComboBox rhythmMode = new ComboBox();
        private readonly ComboBox rhythmGrid = new ComboBox();
        private readonly NumericUpDown rhythmSound = new NumericUpDown();
        private readonly Label rhythmSummary = new Label();
        private Button analyzeBpm;
        private double gridPhaseMs;
        private double? sourceAudioOffsetSeconds;
        private RhythmAnalysisResult lastRhythmAnalysis;

        private void BuildRhythmControls(TableLayoutPanel box, ToolTip hint)
        {
            TableLayoutPanel row = Table(6, 1);
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            row.Controls.Add(Label("Ритм", ink, 9.5F, FontStyle.Regular), 0, 0);
            ConfigureCombo(rhythmMode); rhythmMode.Margin = new Padding(0, 5, 10, 0);
            rhythmMode.Items.AddRange(new object[] { "Исходный ритм", "Подровнять — мягко", "Звук / тишина" });
            rhythmMode.SelectedIndex = 0;
            rhythmMode.SelectedIndexChanged += delegate { if (!loading) RefreshChart(); };
            row.Controls.Add(rhythmMode, 1, 0);
            row.Controls.Add(Label("Сетка", ink, 9F, FontStyle.Regular), 2, 0);
            ConfigureCombo(rhythmGrid); rhythmGrid.Margin = new Padding(0, 5, 10, 0);
            rhythmGrid.Items.AddRange(new object[] { "1/4", "1/8", "1/8 T", "1/16", "1/16 T", "1/32", "1/32 T", "1/64" }); rhythmGrid.SelectedIndex = 3;
            rhythmGrid.SelectedIndexChanged += delegate { if (!loading) RefreshChart(); };
            row.Controls.Add(rhythmGrid, 3, 0);
            row.Controls.Add(Label("Звук, %", ink, 9F, FontStyle.Regular), 4, 0);
            ConfigureNumber(rhythmSound, 0, 100, 90, 5); rhythmSound.Margin = new Padding(0, 5, 0, 0);
            rhythmSound.ValueChanged += delegate { if (!loading) RefreshChart(); };
            row.Controls.Add(rhythmSound, 5, 0); box.Controls.Add(row, 0, 3);
            TableLayoutPanel summary = Table(2, 1);
            summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            summary.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            rhythmSummary.ForeColor = muted; rhythmSummary.Dock = DockStyle.Fill;
            rhythmSummary.Font = new Font("Segoe UI", 8.7F); rhythmSummary.TextAlign = ContentAlignment.MiddleLeft;
            rhythmSummary.AutoEllipsis = true; summary.Controls.Add(rhythmSummary, 0, 0);
            analyzeBpm = Button("BPM по MP3…", false); analyzeBpm.Margin = new Padding(5, 2, 0, 2);
            analyzeBpm.Click += delegate { ChooseRhythmAudio(); }; summary.Controls.Add(analyzeBpm, 1, 0);
            box.Controls.Add(summary, 0, 4);
            hint.SetToolTip(rhythmMode, "Подровнять: сдвиг начал и концов нот на 50% к сетке. Звук / тишина: повторяющиеся разрывы внутри нот. Оба режима сохраняют общую длину трека.");
            hint.SetToolTip(rhythmGrid, "Частей на четверть: 1/4=1, 1/8=2, 1/8T=3, 1/16=4, 1/16T=6, 1/32=8, 1/32T=12, 1/64=16. T — триоль. Шаг = 60000 / исходный BPM / частей / скорость.");
            hint.SetToolTip(rhythmSound, "В режиме Звук / тишина: 90% клетки звучит, 10% — тишина. Это доля времени, не напряжение и не регулятор громкости.");
            hint.SetToolTip(analyzeBpm, "Для MP3: найти BPM. Для MIDI/CSV: выбрать соответствующий MP3 и сравнить темп. Анализ локальный, воспроизведение не запускается.");
        }

        private int GridParts() { int[] parts = { 1, 2, 3, 4, 6, 8, 12, 16 }; return parts[Math.Max(0, rhythmGrid.SelectedIndex)]; }

        private bool CanUseRhythmGrid()
        {
            return originalBpm.HasValue && !sourceTempoChanges &&
                60000 / originalBpm.Value / GridParts() / playbackSpeed >= 1;
        }

        private RhythmSettings RhythmOptions()
        {
            return new RhythmSettings {
                Mode = CanUseRhythmGrid() ? (RhythmMode)Math.Max(0, rhythmMode.SelectedIndex) : RhythmMode.Original,
                SourceBpm = originalBpm ?? 0,
                PartsPerBeat = GridParts(), PhaseMs = gridPhaseMs, SoundPercent = (int)rhythmSound.Value, GlideMs = (int)glide.Value, CurveResolutionMs = new[] { 5, 10, 20, 50 }[Math.Max(0, curveResolution.SelectedIndex)]
            };
        }

        private void RefreshRhythmControls()
        {
            if (analyzeBpm == null) return;
            bool active = !busy && !engine.IsPlaying;
            bool usable = CanUseRhythmGrid();
            rhythmMode.Enabled = active && usable && notes.Count > 0;
            rhythmGrid.Enabled = active && usable && rhythmMode.SelectedIndex > 0;
            rhythmSound.Enabled = active && usable && rhythmMode.SelectedIndex == 2;
            if (!originalBpm.HasValue) rhythmSummary.Text = "Для сетки задай BPM файла или определи его по MP3.";
            else if (sourceTempoChanges) rhythmSummary.Text = "В MIDI меняется темп: воспроизведение сохраняет его; ровная сетка выключена.";
            else if (!usable) rhythmSummary.Text = "При этом темпе шаг меньше 1 мс; выбери более крупную сетку.";
            else {
                double step = 60000 / originalBpm.Value / GridParts() / playbackSpeed;
                string explanation = rhythmMode.SelectedIndex == 2 ? " · звук " + rhythmSound.Value + "% клетки" :
                    rhythmMode.SelectedIndex == 1 ? " · выравнивание 50%" : " · обработка выключена";
                rhythmSummary.Text = "Шаг " + step.ToString("0.###", CultureInfo.CurrentCulture) + " мс · " + GridParts() + " части/долю" + explanation;
            }
        }

        private void ResetRhythmSource()
        {
            gridPhaseMs = 0; sourceAudioOffsetSeconds = null; lastRhythmAnalysis = null;
            rhythmMode.SelectedIndex = 0; rhythmGrid.SelectedIndex = 3; rhythmSound.Value = 90; glide.Value = 0;
        }

        private static double? ReadNumberSidecar(string path)
        {
            double value;
            return File.Exists(path) && Double.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value) && !Double.IsNaN(value) && !Double.IsInfinity(value)
                ? (double?)value : null;
        }

        private void LoadRhythmMetadata(string path)
        {
            double? phase = ReadNumberSidecar(path + ".grid-phase.txt");
            if (phase.HasValue && Math.Abs(phase.Value) < 1e12) gridPhaseMs = phase.Value;
            double? offset = ReadNumberSidecar(path + ".audio-offset.txt");
            if (offset.HasValue && offset.Value >= 0 && offset.Value < 1e9) sourceAudioOffsetSeconds = offset;
        }

        private static void SaveRhythmMetadata(string path, double phaseMs, double? audioOffset)
        {
            File.WriteAllText(path + ".grid-phase.txt", phaseMs.ToString("R", CultureInfo.InvariantCulture));
            if (audioOffset.HasValue) File.WriteAllText(path + ".audio-offset.txt", audioOffset.Value.ToString("R", CultureInfo.InvariantCulture));
            else if (File.Exists(path + ".audio-offset.txt")) File.Delete(path + ".audio-offset.txt");
        }

        private void ChooseRhythmAudio()
        {
            if (selected == null || busy || engine.IsPlaying) return;
            if (selected.Kind == "MP3" || selected.Kind == "WAV") { BeginRhythmAnalysis(selected.Path, true); return; }
            using (OpenFileDialog dialog = new OpenFileDialog {
                Title = "Выбери MP3, из которого получен этот MIDI / CSV",
                Filter = "Аудио|*.mp3;*.wav|Все файлы|*.*", Multiselect = false,
                InitialDirectory = Path.GetDirectoryName(selected.Path)
            }) if (dialog.ShowDialog(this) == DialogResult.OK) BeginRhythmAnalysis(dialog.FileName, true);
        }

        private void BeginRhythmAnalysis(string audio, bool showDialog)
        {
            if (selected == null || busy || engine.IsPlaying) return;
            string report = GeneratedFiles.CreateUniquePath(Path.Combine(root, "output"), Path.GetFileNameWithoutExtension(audio) + " — ритм", ".json");
            converter.Prepare(); busy = true; UpdateButtons();
            status.Text = "Определение BPM по аудио… Стоп отменяет анализ";
            Log("BPM   анализ " + Path.GetFileName(audio) + " · скорость воспроизведения сохраняется", mint);
            ThreadPool.QueueUserWorkItem(delegate {
                string error = null; RhythmAnalysisResult result = null;
                try {
                    converter.Run(Path.Combine(root, "runtime", "node.exe"),
                        new[] { Path.Combine(root, "converter", "analyze-rhythm.cjs"), "--input", audio, "--output", report }, root,
                        delegate(string message) { OnUi(delegate { Log(message, muted); }); });
                    result = RhythmAnalysisResult.Load(report);
                } catch (Exception e) { error = e.Message; }
                OnUi(delegate {
                    busy = false; UpdateButtons();
                    if (error != null) { status.Text = "Анализ BPM остановлен"; Log(error, Color.Salmon); return; }
                    lastRhythmAnalysis = result;
                    status.Text = "Оценка по аудио: " + result.Bpm.ToString("0.##", CultureInfo.CurrentCulture) + " BPM";
                    Log("BPM   " + result.Bpm.ToString("0.###", CultureInfo.InvariantCulture) + " · " + result.BeatCount + " долей · отчёт " + report, mint);
                    if (showDialog) using (Form dialog = CreateRhythmAnalysisDialog(result)) dialog.ShowDialog(this);
                });
            });
        }

        private Form CreateRhythmAnalysisDialog(RhythmAnalysisResult result)
        {
            Form dialog = new Form { Text = "BPM: аудио и выбранный трек", ClientSize = new Size(600, 370),
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, BackColor = surface, ForeColor = ink, Font = Font };
            bool midi = selected != null && IsMidi(selected.Path);
            double sourceSeconds = notes.Sum(n => (long)n.DurationMs + n.PauseMs) / 1000.0;
            double comparableSeconds = Math.Max(0, sourceSeconds - (sourceAudioOffsetSeconds ?? 0));
            string difference = originalBpm.HasValue ? ((result.Bpm / originalBpm.Value - 1) * 100).ToString("+0.###;-0.###;0", CultureInfo.CurrentCulture) + "%" : "BPM трека ещё не задан";
            string description = "Аудио: " + Path.GetFileName(result.SourcePath) + "\n" +
                "Найдено: " + result.Bpm.ToString("0.###", CultureInfo.CurrentCulture) + " BPM · " + result.BeatCount + " долей\n" +
                "Выбранный трек: " + (originalBpm.HasValue ? originalBpm.Value.ToString("0.###", CultureInfo.CurrentCulture) + " BPM · разница " + difference : difference) + "\n" +
                "Длительность аудио: " + result.AudioDurationSeconds.ToString("0.###", CultureInfo.CurrentCulture) + " с";
            if (notes.Count > 0) description += " · трек: " + comparableSeconds.ToString("0.###", CultureInfo.CurrentCulture) + " с";
            if (sourceAudioOffsetSeconds.HasValue) description += "\nУчтено MIDI выравнивание начала: " + sourceAudioOffsetSeconds.Value.ToString("0.####", CultureInfo.CurrentCulture) + " с";
            Label details = Label(description, ink, 10F, FontStyle.Regular); details.Dock = DockStyle.None;
            details.Location = new Point(18, 12); details.Size = new Size(564, 144); dialog.Controls.Add(details);
            Label caveat = Label((result.StableGrid ? "Доли образуют достаточно ровную сетку." : "Сетка долей неровная: BPM приблизительный.") +
                "\nРаспознавание может выбрать половинный или двойной темп." +
                (notes.Count > 0 && Math.Abs(comparableSeconds - result.AudioDurationSeconds) > 0.5 ? "\nДлины различаются: проверь, что выбран нужный MP3-фрагмент." : ""), muted, 9F, FontStyle.Regular);
            caveat.Dock = DockStyle.None; caveat.Location = new Point(18, 164); caveat.Size = new Size(564, 64); dialog.Controls.Add(caveat);
            Label choose = Label(midi ? "MIDI сохранит записанный в нём темп." : "Исходный BPM:", ink, 10F, FontStyle.Regular);
            choose.Dock = DockStyle.None; choose.Location = new Point(18, 240); choose.Size = new Size(260, 32); dialog.Controls.Add(choose);
            NumericUpDown value = new NumericUpDown(); ConfigureNumber(value, 1, 1000, 120, 1); value.DecimalPlaces = 3;
            value.Value = (decimal)result.Bpm; value.Dock = DockStyle.None; value.Location = new Point(154, 240); value.Size = new Size(120, 32);
            if (!midi) {
                dialog.Controls.Add(value);
                Button half = Button("÷ 2", false); half.Dock = DockStyle.None; half.Location = new Point(286, 238); half.Size = new Size(65, 34);
                half.Click += delegate { value.Value = Math.Max(value.Minimum, value.Value / 2); }; dialog.Controls.Add(half);
                Button twice = Button("× 2", false); twice.Dock = DockStyle.None; twice.Location = new Point(359, 238); twice.Size = new Size(65, 34);
                twice.Click += delegate { value.Value = Math.Min(value.Maximum, value.Value * 2); }; dialog.Controls.Add(twice);
            } else value.Dispose();
            Label footer = Label("Анализ сохраняет скорость и исходные времена. Звук не запускается.", muted, 9F, FontStyle.Regular);
            footer.Dock = DockStyle.None; footer.Location = new Point(18, 282); footer.Size = new Size(564, 28); dialog.Controls.Add(footer);
            Button close = Button(midi ? "Готово" : "Закрыть", midi); close.Dock = DockStyle.None;
            close.Location = new Point(midi ? 450 : 298, 320); close.Size = new Size(132, 36); close.DialogResult = DialogResult.Cancel; dialog.Controls.Add(close); dialog.CancelButton = close;
            if (!midi) {
                Button apply = Button("Применить BPM", true); apply.Dock = DockStyle.None; apply.Location = new Point(440, 320); apply.Size = new Size(142, 36);
                apply.Click += delegate { ApplyRhythmAnalysis(result, (double)value.Value); dialog.DialogResult = DialogResult.OK; };
                dialog.Controls.Add(apply); dialog.AcceptButton = apply;
            } else dialog.AcceptButton = close;
            return dialog;
        }

        private void ApplyRhythmAnalysis(RhythmAnalysisResult result, double bpm)
        {
            if (selected == null || IsMidi(selected.Path)) return;
            double sourceSeconds = notes.Sum(n => (long)n.DurationMs + n.PauseMs) / 1000.0;
            bool sameAudio = String.Equals(Path.GetFullPath(selected.Path), Path.GetFullPath(result.SourcePath), StringComparison.OrdinalIgnoreCase);
            bool matchingLength = notes.Count > 0 && Math.Abs(sourceSeconds - (sourceAudioOffsetSeconds ?? 0) - result.AudioDurationSeconds) <= 0.2;
            SetSourceTempo(bpm, "оценка аудио", false);
            if (result.StableGrid && (sameAudio || matchingLength))
                gridPhaseMs = (result.PhaseSeconds + (sourceAudioOffsetSeconds ?? 0)) * 1000;
            tapTimes.Clear(); RefreshChart();
            Log("BPM   ориентир " + bpm.ToString("0.###", CultureInfo.InvariantCulture) + " · скорость " + (playbackSpeed * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%", mint);
        }
    }
}
