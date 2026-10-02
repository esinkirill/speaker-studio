using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace SpeakerPlayer
{
    public sealed class SourceFile
    {
        public string Path;
        public string Title { get { return System.IO.Path.GetFileNameWithoutExtension(Path); } }
        public string Kind { get { return System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant(); } }
        public override string ToString() { return Title; }
    }

    public sealed partial class PlayerForm : Form
    {
        private readonly string root;
        private readonly PlaybackEngine engine = new PlaybackEngine();
        private readonly ChildProcessRunner converter = new ChildProcessRunner();
        private readonly Color background = Color.FromArgb(11, 17, 27);
        private readonly Color surface = Color.FromArgb(19, 29, 44);
        private readonly Color ink = Color.FromArgb(226, 235, 245);
        private readonly Color muted = Color.FromArgb(132, 151, 173);
        private readonly Color mint = Color.FromArgb(94, 226, 204);
        private readonly TrackList library = new TrackList();
        private readonly FrequencyChart chart = new FrequencyChart();
        private readonly LiveConsole console = new LiveConsole();
        private readonly TrackBar pitch = new TrackBar();
        private readonly NumericUpDown tempoValue = new NumericUpDown();
        private readonly ComboBox tempoUnit = new ComboBox();
        private readonly NumericUpDown gap = new NumericUpDown();
        private readonly Label tempoSummary = new Label();
        private readonly Label sourceTempoText = new Label();
        private Button sourceTempoButton;
        private double playbackSpeed = 1.0;
        private double? originalBpm;
        private bool sourceTempoChanges;
        private bool tapEstablishesSource;
        private string tempoOrigin;
        private readonly List<long> tapTimes = new List<long>();
        private readonly CheckBox loop = new CheckBox();
        private readonly ComboBox midiTrack = new ComboBox();
        private readonly ComboBox output = new ComboBox();
        private readonly Label pitchText = new Label();
        private readonly Label title = new Label();
        private readonly Label subtitle = new Label();
        private readonly Label frequency = new Label();
        private readonly Label position = new Label();
        private readonly Label status = new Label();
        private readonly MeterBar progress = new MeterBar();
        private Button play, pause, stop, convert, export;
        private Control settingsPanel;
        private List<ToneRow> notes = new List<ToneRow>();
        private SourceFile selected;
        private bool loading, busy, paused, closing, syncingTempo, userStopped;
        private int lastLogRow = -1;
        private long lastLogPosition;
        private long playbackSession;
        private long durationMs;
        public string[] LaunchArguments { get; set; }

        public PlayerForm(string baseDirectory, string initialFile)
        {
            root = baseDirectory;
            libraryStore = new LibraryStore(root);
            Text = "Speaker Studio — мини-плеер для пищалки";
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle available = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(1180, Math.Max(960, available.Width - 48)), Math.Min(820, Math.Max(640, available.Height - 80)));
            MinimumSize = new Size(960, 640);
            BackColor = background;
            ForeColor = ink;
            Font = new Font("Segoe UI", 10F);
            Icon = Icon.ExtractAssociatedIcon(System.IO.Path.Combine(root, "SpeakerStudio.exe"));
            AutoScaleMode = AutoScaleMode.Dpi;
            BuildInterface();
            engine.Progress += delegate(PlaybackProgress p) { OnUi(delegate { if (p.SessionId == playbackSession && playbackSession != 0 && p.PositionRevision == engine.PositionRevision) ShowProgress(p); }); };
            engine.SessionCompleted += delegate(long sessionId, string error) { OnUi(delegate {
                if (sessionId != playbackSession || playbackSession == 0) return;
                paused = false; UpdateButtons();
                status.Text = error == null ? (userStopped ? "Остановлено" : "Воспроизведение завершено") : "Ошибка воспроизведения";
                frequency.Text = "0 Hz";
                if (error == null && !userStopped) { chart.Finish(); progress.Fraction = 1; position.Text = PositionClock(durationMs) + " / " + PositionClock(durationMs); }
                if (error != null || !userStopped) Log(error == null ? "END   последовательность завершена" : "ERROR " + error, error == null ? mint : Color.Salmon);
            }); };
            Shown += delegate {
                LoadLibrary();
                if (initialFile != null) AddFile(initialFile, true);
                else if (library.Items.Count > 0) library.SelectedIndex = 0;
                ApplyLaunchArguments();
            };
            FormClosing += delegate { closing = true; playbackSession = 0; transcription.Cancel(); converter.Cancel(); engine.Stop(); };
            FormClosed += delegate { engine.Dispose(); converter.Dispose(); rhythmDialog.Dispose(); releaseHints.Dispose(); };
            AllowDrop = true;
            DragEnter += delegate(object sender, DragEventArgs e) {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += delegate(object sender, DragEventArgs e) {
                if (busy || engine.IsPlaying) return;
                foreach (string file in (string[])e.Data.GetData(DataFormats.FileDrop)) AddFile(file, true);
            };
            Log("READY MIDI / CSV / MP3 / WAV · перетащи файл в окно", muted);
        }

        private void BuildInterface() { BuildReleaseInterface(); }

        private Control BuildReadout()
        {
            TableLayoutPanel line = Table(3, 1);
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 164));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196));
            frequency.Text = "0 Hz"; frequency.Font = new Font("Consolas", 22F, FontStyle.Bold); frequency.ForeColor = mint;
            frequency.Dock = DockStyle.Fill; frequency.TextAlign = ContentAlignment.MiddleLeft; line.Controls.Add(frequency, 0, 0);
            progress.Dock = DockStyle.Fill; progress.Margin = new Padding(6, 20, 15, 19); line.Controls.Add(progress, 1, 0);
            position.Text = "00:00.000 / 00:00.000"; position.Font = new Font("Consolas", 9.5F); position.ForeColor = muted;
            position.Dock = DockStyle.Fill; position.TextAlign = ContentAlignment.MiddleRight; line.Controls.Add(position, 2, 0);
            return line;
        }

        private Control BuildTransport()
        {
            TableLayoutPanel line = Table(5, 1);
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 21));
            play = Button("▶  Играть", true); play.Click += delegate { Play(); };
            pause = Button("Ⅱ  Пауза", false); pause.Click += delegate {
                if (paused) { engine.Resume(); paused = false; status.Text = "Играет"; }
                else { engine.Pause(); paused = true; status.Text = "Пауза"; }
                pause.Text = paused ? "▶  Продолжить" : "Ⅱ  Пауза";
            };
            stop = Button("■  Стоп", false); stop.Click += delegate {
                if (busy) { converter.Cancel(); return; }
                userStopped = true; playbackSession = 0; engine.Stop(); paused = false; chart.Reset(); frequency.Text = "0 Hz";
                status.Text = "Остановлено"; UpdateButtons(); Log("STOP  воспроизведение остановлено", muted);
            };
            convert = Button("MIDI → CSV", false); convert.Click += delegate { ConvertSelected(); };
            export = Button("Сохранить CSV", false); export.Click += delegate { ExportCsv(); };
            Button[] buttons = { play, pause, stop, convert, export };
            for (int i = 0; i < buttons.Length; i++) { buttons[i].Margin = new Padding(0, 3, i == buttons.Length - 1 ? 0 : 8, 10); line.Controls.Add(buttons[i], i, 0); }
            return line;
        }

        private Control BuildConsole()
        {
            TableLayoutPanel box = Table(1, 2); box.BackColor = Color.FromArgb(7, 13, 22); box.Padding = new Padding(12, 9, 12, 9);
            box.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); box.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            box.Controls.Add(Label("LIVE CONSOLE    STEP / TIME / FREQUENCY / DURATION / PAUSE", muted, 8.3F, FontStyle.Bold), 0, 0);
            console.Dock = DockStyle.Fill; console.BackColor = box.BackColor; console.ForeColor = mint;
            console.Font = new Font("Consolas", 9F); box.Controls.Add(console, 0, 1);
            return box;
        }

        private void LoadLibrary() { LoadReleaseLibrary(); }

        public void AddFile(string path, bool select) { AddReleaseFile(path, select, true); }

        private void SelectFile()
        {
            if (loading || busy) return;
            playbackSession = 0; engine.Stop(); paused = false;
            selected = library.SelectedItem as SourceFile;
            notes = new List<ToneRow>(); selectionPositionMs = 0;
            if (selected == null) return;
            chart.PianoRoll = IsMidi(selected.Path);
            title.Text = selected.Title; subtitle.Text = selected.Kind + " · " + selected.Path;
            loading = true; midiTrack.Items.Clear(); midiTrack.Items.Add("MIDI: все дорожки");
            playbackSpeed = 1.0; tapTimes.Clear(); ResetRhythmSource(); SetSourceTempo(null, "", false);
            try {
                if (IsMidi(selected.Path)) {
                    foreach (MidiTrackInfo track in SequenceFiles.ReadMidiTracks(selected.Path)) if (track.NoteCount > 0) midiTrack.Items.Add(track);
                    midiTrack.Enabled = true; midiTrack.SelectedIndex = midiTrack.Items.Count > 1 ? 1 : 0;
                    notes = SequenceFiles.ReadMidi(selected.Path, SelectedMidiTrack());
                    MidiTempoInfo tempo = SequenceFiles.ReadMidiTempo(selected.Path);
                    sourceAudioOffsetSeconds = tempo.AudioBarOffsetSeconds;
                    bool unknownModelTempo = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(selected.Path)).Contains("speakerstudio:bpm_source=unknown");
                    SetSourceTempo(unknownModelTempo ? (double?)null : tempo.InitialBpm, unknownModelTempo ? "BPM не определён моделью" : tempo.TempoEventCount == 0 ? "стандарт MIDI" : "MIDI", tempo.HasTempoChanges);
                    Log(unknownModelTempo ? "TEMPO BPM не определён · 100% = исходные времена; можно указать ориентир вручную" : "BPM   из MIDI: " + tempo.InitialBpm.ToString("0.00", CultureInfo.InvariantCulture) + (tempo.HasTempoChanges ? " · опорный темп, изменения внутри MIDI сохраняются" : ""), muted);
                } else {
                    midiTrack.SelectedIndex = 0; midiTrack.Enabled = false;
                    if (selected.Kind == "CSV") notes = SequenceFiles.ReadCsv(selected.Path);
                    double savedBpm;
                    string bpmFile = selected.Path + ".bpm.txt";
                    if (selected.Kind == "CSV" && File.Exists(bpmFile) &&
                        Double.TryParse(File.ReadAllText(bpmFile).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out savedBpm) &&
                        !Double.IsNaN(savedBpm) && !Double.IsInfinity(savedBpm) && savedBpm >= 0.01 && savedBpm <= 1e12)
                        SetSourceTempo(savedBpm, "CSV", File.Exists(selected.Path + ".bpm-varies.txt"));
                    else Log("TEMPO BPM не задан · 100% = исходные времена; при необходимости укажи BPM файла", muted);
                    if (selected.Kind == "CSV") {
                        LoadRhythmMetadata(selected.Path);
                        if (File.Exists(selected.Path + ".processed.txt")) {
                            pitch.Value = 0; gap.Value = 0;
                            Log("LOAD  готовый экспорт · тон, скорость и ритм уже записаны в CSV", muted);
                        }
                    }
                }
                partButton.Visible = IsMidi(selected.Path) && midiTrack.Items.Count > 2;
                status.Text = notes.Count == 0 ? "Открой MP3 → MIDI для конвертации" : "Готово к воспроизведению";
                Log("LOAD  " + selected.Title + " · " + notes.Count + " строк", muted);
            } catch (Exception error) { ShowError(error); }
            finally { loading = false; RefreshChart(); UpdateButtons(); }
        }

        private void LoadMidiSelection()
        {
            try { notes = SequenceFiles.ReadMidi(selected.Path, SelectedMidiTrack()); RefreshChart(); Log("MIDI  выбрана дорожка · " + notes.Count + " строк", muted); }
            catch (Exception error) { ShowError(error); }
            UpdateButtons();
        }

        private int SelectedMidiTrack() { MidiTrackInfo track = midiTrack.SelectedItem as MidiTrackInfo; return track == null ? -1 : track.Index; }
        private static bool IsMidi(string path) { string ext = System.IO.Path.GetExtension(path).ToLowerInvariant(); return ext == ".mid" || ext == ".midi"; }
        private PlaybackSettings Settings() { return new PlaybackSettings { Transpose = pitch.Value, Speed = playbackSpeed, NoteGapMs = (int)gap.Value, Loop = loop.Checked, Output = (OutputMode)output.SelectedIndex, Rhythm = RhythmOptions(), StartPositionMs = selectionPositionMs }; }

        private void RefreshChart()
        {
            if (chart == null || engine.IsPlaying) return;
            PlaybackSettings settings = Settings();
            List<ToneRow> prepared;
            try { prepared = Transform(notes, settings); }
            catch (ArgumentException error) {
                if (rhythmMode.SelectedIndex > 0) rhythmMode.SelectedIndex = 0;
                ShowError(error); return;
            }
            chart.SetPreparedSequence(prepared);
            long previousDuration = durationMs;
            durationMs = prepared.Sum(n => (long)n.DurationMs + n.PauseMs);
            if (previousDuration > 0 && previousDuration != durationMs)
                selectionPositionMs = (long)Math.Round(selectionPositionMs * (durationMs / (double)previousDuration));
            SetTimelinePosition(selectionPositionMs, false);
            RefreshTempoSummary();
            RefreshRhythmControls();
            position.Text = PositionClock(selectionPositionMs) + " / " + PositionClock(durationMs); progress.Fraction = durationMs == 0 ? 0 : selectionPositionMs / (double)durationMs;
            if (selected != null && notes.Count > 0) subtitle.Text = selected.Kind + " · " + prepared.Count + " шагов · " + Clock(durationMs) + " · исходный файл сохранён";
        }

        public static List<ToneRow> Transform(IList<ToneRow> source, PlaybackSettings settings)
        {
            return SequenceTiming.Transform(source, settings.Transpose, settings.Speed, settings.NoteGapMs, settings.Rhythm);
        }

        private void Play()
        {
            if (notes.Count == 0 || busy) return;
            PlaybackSettings settings = Settings();
            if (settings.Output == OutputMode.Speaker && !IsAdministrator()) { Elevate(); return; }
            try {
                chart.Reset(); userStopped = false; lastLogRow = -1; lastLogPosition = 0;
                engine.Play(notes, settings, root); playbackSession = engine.LastSessionId; paused = false;
                status.Text = settings.Output == OutputMode.Visual ? "Диаграмма · без звука" : settings.Output == OutputMode.Preview ? "Предпросмотр · колонки" : "Играет · пищалка платы";
                Log("PLAY  " + selected.Title + " · тон " + settings.Transpose + " пт · скорость " + (settings.Speed * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%" +
                    (originalBpm.HasValue ? " · " + (originalBpm.Value * settings.Speed).ToString("0.##", CultureInfo.InvariantCulture) + " BPM" : "") +
                    " · старт " + PositionClock(settings.StartPositionMs) + " · переход " + settings.Rhythm.GlideMs + " мс · разделение " + settings.NoteGapMs + " мс · " + rhythmMode.Text +
                    (settings.Rhythm.Mode == RhythmMode.Original ? "" : " · шаг " + (60000 / settings.Rhythm.SourceBpm / settings.Rhythm.PartsPerBeat / settings.Speed).ToString("0.###", CultureInfo.InvariantCulture) + " мс"), mint);
                UpdateButtons();
            } catch (Exception error) { ShowError(error); }
        }

        private void ShowProgress(PlaybackProgress p)
        {
            frequency.Text = p.Frequency <= 0 ? "REST" : p.Frequency + " Hz";
            position.Text = PositionClock(p.PositionMs) + " / " + PositionClock(p.TotalMs);
            progress.Fraction = p.TotalMs == 0 ? 0 : p.PositionMs / (double)p.TotalMs;
            chart.UpdatePlayback(p);
            if (p.RowIndex != lastLogRow || p.PositionMs < lastLogPosition) {
                Log(String.Format(CultureInfo.InvariantCulture, "{0,4}/{1,-4} {2,8}  {3,6} Hz  {4,5} ms  +{5,3} ms", p.RowIndex + 1, p.RowCount, Clock(p.PositionMs), p.Frequency, p.DurationMs, p.PauseMs), p.Frequency > 0 ? mint : muted);
                lastLogRow = p.RowIndex;
            }
            lastLogPosition = p.PositionMs;
        }

        private void ConvertSelected()
        {
            if (selected == null || busy || engine.IsPlaying) return;
            if (IsMidi(selected.Path)) {
                try {
                    string folder = System.IO.Path.Combine(root, "output"); Directory.CreateDirectory(folder);
                    string target = GeneratedFiles.CreateUniquePath(folder, selected.Title + " — мелодия", ".csv");
                    SequenceFiles.WriteCsv(target, notes);
                    SaveTempoMetadata(target, originalBpm);
                    SaveRhythmMetadata(target, gridPhaseMs, sourceAudioOffsetSeconds);
                    Log("SAVE  MIDI → " + target, mint); AddFile(target, true);
                } catch (Exception error) { ShowError(error); }
                return;
            }
            if (selected.Kind == "MP3" || selected.Kind == "WAV") {
                transcription.AddAudio(selected.Path); studioTabs.SelectedTab = conversionTab;
            }
        }

        private void ExportCsv()
        {
            if (notes.Count == 0) return;
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "CSV для speaker|*.csv", FileName = Path.GetFileName(GeneratedFiles.CreateUniquePath(Path.Combine(root, "output"), selected.Title + " — speaker", ".csv")), InitialDirectory = System.IO.Path.Combine(root, "output") }) {
                Directory.CreateDirectory(dialog.InitialDirectory);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (String.Equals(dialog.FileName, selected.Path, StringComparison.OrdinalIgnoreCase)) { Log("Выбери новое имя файла: настройки сохраняются отдельной копией.", muted); return; }
                try { SaveAdjustedCsv(dialog.FileName); Log("SAVE  CSV с выбранным тоном, темпом и разделением нот: " + dialog.FileName, mint); }
                catch (Exception error) { ShowError(error); }
            }
        }

        private void SaveAdjustedCsv(string path)
        {
            SequenceFiles.WriteCsv(path, Transform(notes, Settings()));
            SaveTempoMetadata(path, originalBpm.HasValue ? (double?)(originalBpm.Value * playbackSpeed) : null);
            SaveRhythmMetadata(path, gridPhaseMs / playbackSpeed, playbackSpeed == 1 ? sourceAudioOffsetSeconds : null);
            File.WriteAllText(path + ".processed.txt", "Pitch, speed, note gap and rhythm are already applied to CSV values. Load with neutral settings.");
        }

        private void SaveTempoMetadata(string path, double? bpm)
        {
            string reference = path + ".bpm.txt";
            string varies = path + ".bpm-varies.txt";
            if (bpm.HasValue) File.WriteAllText(reference, bpm.Value.ToString("R", CultureInfo.InvariantCulture));
            else if (File.Exists(reference)) File.Delete(reference);
            if (bpm.HasValue && sourceTempoChanges) File.WriteAllText(varies, "Initial BPM is a reference; tempo changes are preserved in CSV timings.");
            else if (File.Exists(varies)) File.Delete(varies);
        }

        private void UpdateButtons()
        {
            if (play == null) return;
            bool playing = engine.IsPlaying;
            settingsPanel.Enabled = !playing && !busy;
            play.Enabled = !playing && !busy && notes.Count > 0;
            pause.Enabled = playing && !busy; stop.Enabled = playing || busy;
            pause.Text = paused ? "▶  Продолжить" : "Ⅱ  Пауза";
            convert.Text = selected != null && (selected.Kind == "MP3" || selected.Kind == "WAV") ? "MP3 → MIDI" : "Создать CSV";
            convert.Enabled = !playing && !busy && selected != null && (IsMidi(selected.Path) || selected.Kind == "MP3" || selected.Kind == "WAV");
            export.Enabled = !playing && !busy && notes.Count > 0;
            library.Enabled = !playing && !busy; pitch.Enabled = !playing && !busy; tempoValue.Enabled = !playing && !busy;
            gap.Enabled = !playing && !busy; loop.Enabled = !playing && !busy; output.Enabled = !playing && !busy;
            tempoUnit.Enabled = !playing && !busy && originalBpm.HasValue;
            sourceTempoButton.Enabled = !playing && !busy && selected != null && (!IsMidi(selected.Path) || !originalBpm.HasValue);
            midiTrack.Enabled = !playing && !busy && selected != null && IsMidi(selected.Path);
            partButton.Enabled = !playing && !busy;
            librarySearch.Enabled = !busy; libraryFormat.Enabled = !busy;
            removeTrack.Enabled = !playing && !busy && library.SelectedItem != null;
            timelinePosition.Enabled = notes.Count > 0 && !busy;
            analyzeBpm.Enabled = !playing && !busy && selected != null;
            RefreshRhythmControls();
        }

        private void Elevate()
        {
            if (busy || engine.IsPlaying) return;
            try {
                string args = selected == null ? "" : ChildProcessRunner.Quote(selected.Path) + " " + pitch.Value.ToString(CultureInfo.InvariantCulture)
                    + " " + (playbackSpeed * 100).ToString("R", CultureInfo.InvariantCulture) + " " + gap.Value.ToString(CultureInfo.InvariantCulture)
                    + " " + (originalBpm.HasValue ? originalBpm.Value.ToString("R", CultureInfo.InvariantCulture) : "0") + " 0 " + loop.Checked + " " + SelectedMidiTrack()
                    + " " + (tempoUnit.SelectedIndex == 1 ? "BPM" : "%")
                    + " " + rhythmMode.SelectedIndex + " " + rhythmGrid.SelectedIndex + " " + rhythmSound.Value.ToString(CultureInfo.InvariantCulture)
                    + " " + gridPhaseMs.ToString("R", CultureInfo.InvariantCulture)
                    + " " + glide.Value.ToString(CultureInfo.InvariantCulture) + " " + curveResolution.SelectedIndex + " " + selectionPositionMs.ToString(CultureInfo.InvariantCulture);
                ProcessStartInfo info = new ProcessStartInfo(Application.ExecutablePath, args);
                info.UseShellExecute = true; info.Verb = "runas"; info.WorkingDirectory = root;
                Process.Start(info); Close();
            } catch (System.ComponentModel.Win32Exception) { Log("Запуск с правами администратора отменён.", muted); }
            catch (Exception error) { ShowError(error); }
        }

        public static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        private void ApplyLaunchArguments()
        {
            if (LaunchArguments == null || LaunchArguments.Length < 8) return;
            try {
                pitch.Value = Int32.Parse(LaunchArguments[1], CultureInfo.InvariantCulture);
                double restoredBpm = Double.Parse(LaunchArguments[4], CultureInfo.InvariantCulture);
                string restoredOrigin = String.IsNullOrEmpty(tempoOrigin) || !originalBpm.HasValue || restoredBpm != originalBpm.Value ? "вручную" : tempoOrigin;
                SetSourceTempo(restoredBpm > 0 ? (double?)restoredBpm : null, restoredOrigin, sourceTempoChanges);
                SetPlaybackSpeed(Double.Parse(LaunchArguments[2], CultureInfo.InvariantCulture) / 100.0);
                if (LaunchArguments.Length > 8 && originalBpm.HasValue) tempoUnit.SelectedIndex = LaunchArguments[8] == "BPM" ? 1 : 0;
                gap.Value = Decimal.Parse(LaunchArguments[3], CultureInfo.InvariantCulture);
                output.SelectedIndex = Int32.Parse(LaunchArguments[5], CultureInfo.InvariantCulture);
                loop.Checked = Boolean.Parse(LaunchArguments[6]);
                int track = Int32.Parse(LaunchArguments[7], CultureInfo.InvariantCulture);
                for (int i = 0; i < midiTrack.Items.Count; i++) { MidiTrackInfo item = midiTrack.Items[i] as MidiTrackInfo; if (item != null && item.Index == track) midiTrack.SelectedIndex = i; }
                if (track == -1) midiTrack.SelectedIndex = 0;
                if (LaunchArguments.Length >= 13) {
                    int restoredGrid = Int32.Parse(LaunchArguments[10], CultureInfo.InvariantCulture);
                    rhythmGrid.SelectedIndex = LaunchArguments.Length >= 16 ? restoredGrid : restoredGrid == 0 ? 1 : restoredGrid == 2 ? 5 : 3;
                    rhythmSound.Value = Decimal.Parse(LaunchArguments[11], CultureInfo.InvariantCulture);
                    gridPhaseMs = Double.Parse(LaunchArguments[12], CultureInfo.InvariantCulture);
                    rhythmMode.SelectedIndex = Int32.Parse(LaunchArguments[9], CultureInfo.InvariantCulture);
                }
                if (LaunchArguments.Length >= 16) {
                    glide.Value = Decimal.Parse(LaunchArguments[13], CultureInfo.InvariantCulture);
                    curveResolution.SelectedIndex = Int32.Parse(LaunchArguments[14], CultureInfo.InvariantCulture);
                    selectionPositionMs = Int64.Parse(LaunchArguments[15], CultureInfo.InvariantCulture);
                }
                RefreshChart();
            } catch (Exception error) { ShowError(error); }
        }
        private void OnUi(Action action) { if (closing || IsDisposed || !IsHandleCreated) return; try { BeginInvoke(new Action(delegate { if (!closing && !IsDisposed) action(); })); } catch (InvalidOperationException) { } }
        private void Log(string message, Color color)
        {
            console.AppendLine(message, color);
        }
        private void ShowError(Exception error) { status.Text = "Ошибка: " + error.Message; Log("ERROR " + error.Message, Color.Salmon); }
        private static string Clock(long ms) { return String.Format("{0:00}:{1:00}", ms / 60000, (ms / 1000) % 60); }
        private static string SafeName(string name) { foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_'); return name; }
        private void SetSourceTempo(double? bpm, string origin, bool varies)
        {
            if (bpm.HasValue && (Double.IsNaN(bpm.Value) || Double.IsInfinity(bpm.Value) || bpm.Value < 0.01 || bpm.Value > 1e12))
                throw new ArgumentException("Исходный BPM должен быть положительным конечным числом.");
            originalBpm = bpm; tempoOrigin = origin; sourceTempoChanges = varies && bpm.HasValue;
            syncingTempo = true;
            try {
                tempoUnit.Items.Clear(); tempoUnit.Items.Add("%");
                if (bpm.HasValue) tempoUnit.Items.Add("BPM");
                tempoUnit.SelectedIndex = bpm.HasValue ? 1 : 0;
            }
            finally { syncingTempo = false; }
            RefreshTempoControls();
        }

        private void SetPlaybackSpeed(double value)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value <= 0)
                throw new ArgumentException("Скорость должна быть больше нуля.");
            playbackSpeed = Math.Max(0.25, Math.Min(3.0, value));
            RefreshTempoControls(); RefreshChart();
        }

        private void TempoValueChanged()
        {
            if (syncingTempo || loading) return;
            double value = (double)tempoValue.Value;
            SetPlaybackSpeed(tempoUnit.SelectedIndex == 1 && originalBpm.HasValue ? value / originalBpm.Value : value / 100.0);
        }

        private void RefreshTempoControls()
        {
            syncingTempo = true;
            try {
                bool useBpm = originalBpm.HasValue && tempoUnit.SelectedIndex == 1;
                decimal minimum = useBpm ? Math.Max(0.01M, Math.Round((decimal)(originalBpm.Value * 0.25), 2)) : 25M;
                decimal maximum = useBpm ? Math.Round((decimal)(originalBpm.Value * 3.0), 2) : 300M;
                decimal display = Math.Round((decimal)(useBpm ? originalBpm.Value * playbackSpeed : playbackSpeed * 100.0), 2);
                tempoValue.Minimum = 0.01M; tempoValue.Maximum = Math.Max(minimum, maximum); tempoValue.Minimum = minimum;
                tempoValue.Value = Math.Max(minimum, Math.Min(tempoValue.Maximum, display));
                tempoUnit.Enabled = originalBpm.HasValue && !busy && !engine.IsPlaying;
                sourceTempoText.Text = originalBpm.HasValue ? "Исх.: " + originalBpm.Value.ToString("0.##", CultureInfo.CurrentCulture) + " BPM" +
                    (sourceTempoChanges ? "\nТемп меняется" : " · " + tempoOrigin) : "BPM не задан";
            } finally { syncingTempo = false; }
            RefreshTempoSummary();
            RefreshRhythmControls();
        }

        private void RefreshTempoSummary()
        {
            string alternate = tempoUnit.SelectedIndex == 1
                ? (playbackSpeed * 100).ToString("0.##", CultureInfo.CurrentCulture) + "%"
                : originalBpm.HasValue ? (originalBpm.Value * playbackSpeed).ToString("0.##", CultureInfo.CurrentCulture) + " BPM" : "Исходный темп = 100%";
            tempoSummary.Text = alternate + " · " + (durationMs / 1000.0).ToString("0.##", CultureInfo.CurrentCulture) + " с";
        }

        private void EditSourceTempo()
        {
            if (selected == null || IsMidi(selected.Path) && originalBpm.HasValue || busy || engine.IsPlaying) return;
            using (Form dialog = new Form { Text = "Исходный BPM", ClientSize = new Size(440, 180), StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = surface, ForeColor = ink, Font = Font }) {
                Label explanation = Label("Укажи BPM исходного трека, если знаешь его.\nВремена файла сохранятся; 100% останется оригиналом.", ink, 9.5F, FontStyle.Regular);
                explanation.Dock = DockStyle.None; explanation.Location = new Point(16, 10); explanation.Size = new Size(408, 52); dialog.Controls.Add(explanation);
                NumericUpDown value = new NumericUpDown(); ConfigureNumber(value, 1, 1000, 120, 1); value.DecimalPlaces = 2;
                value.Dock = DockStyle.None; value.Location = new Point(16, 72); value.Size = new Size(112, 30);
                if (originalBpm.HasValue) value.Value = Math.Max(value.Minimum, Math.Min(value.Maximum, (decimal)originalBpm.Value));
                dialog.Controls.Add(value);
                Button clear = Button("BPM неизвестен", false); clear.Dock = DockStyle.None; clear.Location = new Point(142, 70); clear.Size = new Size(154, 34);
                clear.Click += delegate { SetSourceTempo(null, "", false); tapTimes.Clear(); RefreshChart(); dialog.DialogResult = DialogResult.Ignore; }; dialog.Controls.Add(clear);
                Button cancel = Button("Отмена", false); cancel.Dock = DockStyle.None; cancel.Location = new Point(186, 126); cancel.Size = new Size(112, 36); cancel.DialogResult = DialogResult.Cancel; dialog.Controls.Add(cancel);
                Button apply = Button("Применить", true); apply.Dock = DockStyle.None; apply.Location = new Point(308, 126); apply.Size = new Size(116, 36); apply.DialogResult = DialogResult.OK; dialog.Controls.Add(apply);
                dialog.AcceptButton = apply; dialog.CancelButton = cancel;
                if (dialog.ShowDialog(this) == DialogResult.OK) { SetSourceTempo((double)value.Value, "вручную", sourceTempoChanges); tapTimes.Clear(); RefreshChart(); }
            }
        }

        private void TapBpm()
        {
            if (busy || engine.IsPlaying) return;
            long now = Stopwatch.GetTimestamp();
            if (tapTimes.Count > 0 && (now - tapTimes[tapTimes.Count - 1]) / (double)Stopwatch.Frequency > 3) tapTimes.Clear();
            if (tapTimes.Count == 0) tapEstablishesSource = !originalBpm.HasValue;
            tapTimes.Add(now); if (tapTimes.Count > 9) tapTimes.RemoveAt(0);
            if (tapTimes.Count < 3) { status.Text = "Настучи хотя бы три доли в ритм трека"; return; }
            List<double> intervals = new List<double>();
            for (int i = 1; i < tapTimes.Count; i++) intervals.Add((tapTimes[i] - tapTimes[i - 1]) / (double)Stopwatch.Frequency);
            intervals.Sort(); double seconds = intervals[intervals.Count / 2];
            if (seconds > 0) ApplyTappedTempo(60 / seconds);
        }

        private void ApplyTappedTempo(double bpm)
        {
            if (tapEstablishesSource) SetSourceTempo(bpm, "настукан", false);
            else if (originalBpm.HasValue) SetPlaybackSpeed(bpm / originalBpm.Value);
            RefreshChart();
            status.Text = (tapEstablishesSource ? "Исходный BPM: " : "Темп воспроизведения: ") +
                (originalBpm.HasValue ? originalBpm.Value * (tapEstablishesSource ? 1 : playbackSpeed) : bpm).ToString("0.##", CultureInfo.CurrentCulture) + " BPM";
        }
        private TableLayoutPanel Table(int columns, int rows) { return new TableLayoutPanel { ColumnCount = columns, RowCount = rows, Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.Transparent }; }
        private Label Label(string text, Color color, float size, FontStyle style) { return new Label { Text = text, ForeColor = color, Font = new Font("Segoe UI", size, style), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }; }
        private Button Button(string text, bool primary)
        {
            Button button = new Button { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = primary ? mint : surface, ForeColor = primary ? background : ink, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9.5F, primary ? FontStyle.Bold : FontStyle.Regular) };
            button.FlatAppearance.BorderColor = primary ? mint : Color.FromArgb(47, 64, 84); button.FlatAppearance.BorderSize = 1;
            return button;
        }
        private void ConfigureNumber(NumericUpDown control, int min, int max, int value, int increment)
        {
            control.Minimum = min; control.Maximum = max; control.Value = value; control.Increment = increment;
            control.BackColor = background; control.ForeColor = ink; control.BorderStyle = BorderStyle.FixedSingle;
            control.Dock = DockStyle.Fill; control.Margin = new Padding(0, 5, 13, 0);
        }
        private void ConfigureCombo(ComboBox combo)
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList; combo.BackColor = background; combo.ForeColor = ink;
            combo.FlatStyle = FlatStyle.Flat; combo.Dock = DockStyle.Fill; combo.Font = new Font("Segoe UI", 9F);
            combo.DrawMode = DrawMode.OwnerDrawFixed; combo.ItemHeight = 22;
            combo.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                using (SolidBrush brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
                if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, combo.Items[e.Index].ToString(), combo.Font, e.Bounds, combo.Enabled ? ink : muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
        }

        // In-process UI verification: never opens an output backend or loads InpOut.
        public void PrepareScreenshot(string csv)
        {
            AddFile(csv, true);
            output.SelectedIndex = 2;
            pitch.Value = -2; gap.Value = 10;
            List<ToneRow> preview = Transform(notes, Settings());
            int row = Math.Min(20, preview.Count - 1);
            long elapsed = preview.Take(row).Sum(n => (long)n.DurationMs + n.PauseMs);
            ToneRow current = preview[row];
            for (int i = Math.Max(0, row - 4); i <= row; i++) Log(String.Format("{0,4}/{1,-4}  {2,6} Hz  {3,5} ms  +{4,3} ms", i + 1, preview.Count, preview[i].Frequency, preview[i].DurationMs, preview[i].PauseMs), mint);
            chart.UpdatePlayback(new PlaybackProgress { RowIndex = row, RowCount = notes.Count, Frequency = current.Frequency, DurationMs = current.DurationMs, PauseMs = current.PauseMs, PositionMs = elapsed, TotalMs = durationMs });
            frequency.Text = current.Frequency + " Hz"; position.Text = Clock(elapsed) + " / " + Clock(durationMs); progress.Fraction = elapsed / (double)durationMs;
        }
    }

    public sealed class TrackList : ListBox
    {
        public TrackList() { DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 58; IntegralHeight = false; }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            SourceFile file = Items[e.Index] as SourceFile; bool selected = (e.State & DrawItemState.Selected) != 0;
            using (SolidBrush brush = new SolidBrush(selected ? Color.FromArgb(31, 53, 65) : BackColor)) e.Graphics.FillRectangle(brush, e.Bounds);
            if (selected) using (Pen pen = new Pen(Color.FromArgb(94, 226, 204), 3)) e.Graphics.DrawLine(pen, e.Bounds.Left + 1, e.Bounds.Top + 8, e.Bounds.Left + 1, e.Bounds.Bottom - 8);
            TextRenderer.DrawText(e.Graphics, file.Title, Font, new Rectangle(e.Bounds.X + 12, e.Bounds.Y + 8, e.Bounds.Width - 22, 24), ForeColor, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using (Font small = new Font("Segoe UI", 8.5F)) TextRenderer.DrawText(e.Graphics, file.Kind + "  ·  локальный файл", small, new Point(e.Bounds.X + 13, e.Bounds.Y + 33), Color.FromArgb(132, 151, 173));
        }
    }

    public sealed class MeterBar : Control
    {
        private double fraction;
        public double Fraction { get { return fraction; } set { fraction = Math.Max(0, Math.Min(1, value)); Invalidate(); } }
        public MeterBar() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(Color.FromArgb(30, 43, 59)); using (SolidBrush brush = new SolidBrush(Color.FromArgb(94, 226, 204))) e.Graphics.FillRectangle(brush, 0, 0, (float)(Width * fraction), Height); }
    }
}
