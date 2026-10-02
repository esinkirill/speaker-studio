using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SpeakerPlayer
{
    public sealed class TranscriptionPanel : UserControl
    {
        private readonly string root;
        private readonly MidiConversion converter;
        private readonly TextBox audio = new TextBox();
        private readonly Button browse = new Button(), convert = new Button(), cancel = new Button();
        private readonly Label fileName = new Label(), status = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly LiveConsole journal = new LiveConsole();
        private bool running, closing;
        public event Action<string> MidiCreated;
        public event Action<string> LogMessage;
        public bool Busy { get { return running; } }
        public string InputPath { get { return audio.Text; } }

        public TranscriptionPanel(string baseDirectory)
        {
            root = Path.GetFullPath(baseDirectory);
            converter = new MidiConversion(root);
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(11, 17, 27);
            ForeColor = Color.FromArgb(226, 235, 245);
            Font = new Font("Segoe UI", 10F);
            Padding = new Padding(28);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
            foreach (int height in new[] { 98, 32, 43, 65, 34, 36 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            Label heading = new Label { Dock = DockStyle.Fill, ForeColor = ForeColor,
                Text = "MP3 → MIDI\nЛокальная модель MuScriptor small распознаёт ноты и инструментальные дорожки.\nГотовый MIDI появится в библиотеке; нужную мелодическую дорожку выберешь в плеере.",
                Font = new Font("Segoe UI", 12F), AutoEllipsis = true };
            layout.Controls.Add(heading, 0, 0);
            fileName.Text = "Добавь аудиофайл"; fileName.Dock = DockStyle.Fill; fileName.ForeColor = Color.FromArgb(94, 226, 204);
            fileName.Font = new Font("Segoe UI", 11F, FontStyle.Bold); fileName.AutoEllipsis = true;
            layout.Controls.Add(fileName, 0, 1);
            TableLayoutPanel file = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            file.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); file.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 152));
            audio.Dock = DockStyle.Fill; audio.ReadOnly = true; audio.BackColor = Color.FromArgb(19, 29, 44); audio.ForeColor = ForeColor;
            browse.Text = "Добавить MP3…"; StyleButton(browse); browse.Dock = DockStyle.Fill;
            browse.Click += delegate { PickAudio(); };
            file.Controls.Add(audio, 0, 0); file.Controls.Add(browse, 1, 0); layout.Controls.Add(file, 0, 2);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 8) };
            convert.Text = "Создать MIDI"; cancel.Text = "Отменить"; cancel.Enabled = false;
            StyleButton(convert); StyleButton(cancel); convert.BackColor = Color.FromArgb(94, 226, 204); convert.ForeColor = Color.FromArgb(11, 17, 27);
            convert.Size = new Size(174, 36); cancel.Size = new Size(132, 36);
            convert.Click += delegate { StartConversion(); }; cancel.Click += delegate { Cancel(); };
            buttons.Controls.Add(convert); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 3);
            status.Text = "Без интернета и установки программ. На CPU обработка может занять время.";
            status.Dock = DockStyle.Fill; status.ForeColor = Color.FromArgb(132, 151, 173); status.AutoEllipsis = true;
            layout.Controls.Add(status, 0, 4);
            progress.Dock = DockStyle.Fill; progress.Margin = new Padding(0, 4, 0, 14); progress.Minimum = 0; progress.Maximum = 100;
            layout.Controls.Add(progress, 0, 5);
            journal.Dock = DockStyle.Fill; journal.BackColor = Color.FromArgb(7, 13, 22); journal.ForeColor = Color.FromArgb(94, 226, 204);
            journal.Font = new Font("Consolas", 9F); layout.Controls.Add(journal, 0, 6);
            AllowDrop = true;
            DragEnter += delegate(object sender, DragEventArgs e) { if (!running && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object sender, DragEventArgs e) { if (!running) { string[] files = (string[])e.Data.GetData(DataFormats.FileDrop); if (files.Length > 0) AddAudio(files[0]); } };
        }

        private void StyleButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat; button.ForeColor = ForeColor; button.BackColor = Color.FromArgb(19, 29, 44);
            button.FlatAppearance.BorderColor = Color.FromArgb(47, 62, 79); button.Cursor = Cursors.Hand;
            button.Margin = new Padding(0, 0, 12, 0);
        }
        private void PickAudio()
        {
            if (running) return;
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "Аудио|*.mp3;*.wav;*.flac;*.ogg;*.m4a|Все файлы|*.*", Title = "Добавить MP3 для создания MIDI" })
                if (dialog.ShowDialog(this) == DialogResult.OK) AddAudio(dialog.FileName);
        }
        public void AddAudio(string path)
        {
            if (running) { Log("Сначала дождись завершения или отмени конвертацию."); return; }
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) { Log("Аудиофайл не найден."); return; }
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".mp3" && extension != ".wav" && extension != ".flac" && extension != ".ogg" && extension != ".m4a") {
                Log("Выбери MP3, WAV, FLAC, OGG или M4A."); return;
            }
            audio.Text = Path.GetFullPath(path); fileName.Text = Path.GetFileNameWithoutExtension(path);
            status.Text = "Файл выбран. Создать MIDI распознает все партии, без выбора одной частоты.";
            progress.Value = 0;
            Log("Добавлен файл: " + Path.GetFileName(path));
        }
        public void Cancel()
        {
            converter.Cancel();
            if (!closing && !IsDisposed && running) { status.Text = "Отмена…"; cancel.Enabled = false; }
        }
        public void StartConversion()
        {
            if (running) return;
            string input = audio.Text;
            if (!File.Exists(input)) { PickAudio(); return; }
            if (!converter.RuntimeReady) { Log("В комплекте нет portable runtime и модели MuScriptor small."); return; }
            string output;
            try {
                output = GeneratedFiles.CreateUniquePath(Path.Combine(root, "output"), Path.GetFileNameWithoutExtension(input) + " — MIDI", ".mid");
                converter.Prepare();
            }
            catch (Exception error) { Log(error.Message); return; }
            running = true; audio.Enabled = false; browse.Enabled = false; convert.Enabled = false; cancel.Enabled = true;
            progress.Value = 0; progress.Style = ProgressBarStyle.Marquee;
            status.Text = "Подготовка и определение BPM…"; Log("Начинаем MP3 → MIDI: " + Path.GetFileName(input));
            ThreadPool.QueueUserWorkItem(delegate {
                MidiConversionResult result = null;
                string error = null;
                try { result = converter.Convert(input, output, ProcessLog); }
                catch (OperationCanceledException) { error = "Конвертация отменена."; }
                catch (Exception exception) { error = exception.Message; }
                OnUi(delegate {
                    running = false; audio.Enabled = true; browse.Enabled = true; convert.Enabled = true; cancel.Enabled = false;
                    progress.Style = ProgressBarStyle.Continuous;
                    if (result == null) { status.Text = error; Log(error); return; }
                    progress.Value = 100;
                    status.Text = String.Format(CultureInfo.InvariantCulture, "Готово: {0} нот · {1} инструментов · {2:0.0} с", result.NoteCount, result.InstrumentCount, result.AudioDurationSeconds);
                    Log("Создан MIDI: " + Path.GetFileName(result.OutputPath));
                    Action<string> created = MidiCreated;
                    if (created != null) created(result.OutputPath);
                });
            });
        }
        private void ProcessLog(string line)
        {
            OnUi(delegate {
                if (line.StartsWith("PROGRESS ", StringComparison.Ordinal)) {
                    string[] parts = line.Split(' ');
                    int completed, total;
                    if (parts.Length == 3 && Int32.TryParse(parts[1], out completed) && Int32.TryParse(parts[2], out total) && total > 0) {
                        progress.Style = ProgressBarStyle.Continuous;
                        progress.Value = Math.Max(0, Math.Min(100, (int)(100.0 * completed / total)));
                        status.Text = "Распознавание: фрагмент " + Math.Min(total, completed + 1) + " / " + total;
                    }
                }
                else if (line.StartsWith("STATUS ", StringComparison.Ordinal)) status.Text = line.Substring(7);
                Log(line);
            });
        }
        private void Log(string line)
        {
            if (String.IsNullOrWhiteSpace(line)) return;
            journal.AppendLine(line, line.StartsWith("ERROR", StringComparison.Ordinal) ? Color.Salmon : Color.FromArgb(94, 226, 204));
            Action<string> log = LogMessage;
            if (log != null) log(line);
        }
        private void OnUi(Action action)
        {
            if (closing || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(delegate { if (!closing && !IsDisposed) action(); })); }
            catch (InvalidOperationException) { }
        }
        protected override void Dispose(bool disposing)
        {
            closing = true;
            if (disposing) converter.Dispose();
            base.Dispose(disposing);
        }
    }
}
