using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace SpeakerPlayer
{
    public sealed partial class PlayerForm
    {
        private readonly ToolTip releaseHints = new ToolTip { InitialDelay = 400, ReshowDelay = 100, AutoPopDelay = 20000, ShowAlways = true };
        private readonly TabControl studioTabs = new TabControl();
        private TabPage conversionTab;
        private TranscriptionPanel transcription;
        private readonly TrackBar timelinePosition = new TrackBar();
        private readonly Label selectionText = new Label();
        private readonly NumericUpDown glide = new NumericUpDown();
        private readonly ComboBox curveResolution = new ComboBox();
        private readonly ContextMenuStrip midiPartMenu = new ContextMenuStrip();
        private Button partButton;
        private Form rhythmDialog;
        private long selectionPositionMs;
        private bool syncingPosition;

        private void BuildReleaseInterface()
        {
            Disposed += delegate { midiPartMenu.Dispose(); };
            TableLayoutPanel outer = Table(1, 3); outer.Padding = new Padding(16, 8, 16, 8);
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); Controls.Add(outer);
            Label brand = Label("SPEAKER STUDIO", ink, 17F, FontStyle.Bold); outer.Controls.Add(brand, 0, 0);
            studioTabs.Dock = DockStyle.Fill; studioTabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            studioTabs.ItemSize = new Size(170, 32); studioTabs.SizeMode = TabSizeMode.Fixed;
            studioTabs.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                Rectangle area = studioTabs.GetTabRect(e.Index); bool active = studioTabs.SelectedIndex == e.Index;
                using (Brush brush = new SolidBrush(active ? surface : background)) e.Graphics.FillRectangle(brush, area);
                TextRenderer.DrawText(e.Graphics, studioTabs.TabPages[e.Index].Text, Font, area, active ? ink : muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            TabPage playerTab = new TabPage("Плеер") { BackColor = background, Padding = new Padding(2, 8, 2, 0) };
            conversionTab = new TabPage("MP3 → MIDI") { BackColor = background, Padding = new Padding(8) };
            studioTabs.TabPages.Add(playerTab); studioTabs.TabPages.Add(conversionTab); outer.Controls.Add(studioTabs, 0, 1);
            TableLayoutPanel body = Table(2, 1); body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 246));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); body.Controls.Add(BuildReleaseLibrary(), 0, 0);
            Panel viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(14, 0, 0, 0) };
            TableLayoutPanel main = Table(1, 7); main.Dock = DockStyle.Top; main.Height = 640;
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            Panel file = new Panel { Dock = DockStyle.Fill };
            title.Font = new Font("Segoe UI", 15F, FontStyle.Bold); title.ForeColor = ink;
            title.Text = "Добавь MIDI или CSV"; title.Dock = DockStyle.Top; title.Height = 29;
            subtitle.ForeColor = muted; subtitle.Dock = DockStyle.Bottom; subtitle.Height = 22;
            subtitle.Text = "Библиотека пустая · добавь трек или открой вкладку MP3 → MIDI";
            file.Controls.Add(title); file.Controls.Add(subtitle);
            partButton = Button("Выбрать партию", false); partButton.Dock = DockStyle.Right; partButton.Width = 152;
            partButton.Visible = false; partButton.Click += delegate { ChooseMidiPart(); }; file.Controls.Add(partButton); main.Controls.Add(file, 0, 0);
            settingsPanel = BuildReleaseSettings(); main.Controls.Add(settingsPanel, 0, 1);
            chart.Dock = DockStyle.Fill; chart.Margin = Padding.Empty;
            chart.SeekRequested += delegate(long milliseconds) { SetTimelinePosition(milliseconds, true); };
            main.Controls.Add(chart, 0, 2); main.Controls.Add(BuildTimelineSelector(), 0, 3);
            main.Controls.Add(BuildReadout(), 0, 4); main.Controls.Add(BuildTransport(), 0, 5);
            main.Controls.Add(BuildConsole(), 0, 6); viewport.Controls.Add(main);
            viewport.SizeChanged += delegate { main.Height = Math.Max(640, viewport.ClientSize.Height); main.Width = Math.Max(1, viewport.ClientSize.Width - (viewport.ClientSize.Height < 640 ? SystemInformation.VerticalScrollBarWidth : 0)); };
            body.Controls.Add(viewport, 1, 0); playerTab.Controls.Add(body);
            transcription = new TranscriptionPanel(root) { Dock = DockStyle.Fill };
            transcription.MidiCreated += delegate(string path) { OnUi(delegate { bool selectNew = !engine.IsPlaying; AddFile(path, selectNew); if (selectNew) studioTabs.SelectedTab = playerTab; Log("MIDI  добавлен в библиотеку: " + Path.GetFileName(path), mint); }); };
            transcription.LogMessage += delegate(string message) { OnUi(delegate { Log(message, muted); }); };
            conversionTab.Controls.Add(transcription);
            status.Dock = DockStyle.Fill; status.ForeColor = muted; status.Text = "Готово";
            status.TextAlign = ContentAlignment.MiddleLeft; outer.Controls.Add(status, 0, 2);
            BuildRhythmWindow(); UpdateButtons();
        }

        private Control BuildReleaseSettings()
        {
            TableLayoutPanel box = Table(1, 3); box.BackColor = surface; box.Padding = new Padding(12, 5, 12, 5);
            box.Margin = new Padding(0, 0, 0, 8);
            box.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); box.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            box.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            TableLayoutPanel tone = Table(4, 1);
            tone.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58)); tone.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tone.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76)); tone.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            tone.Controls.Add(Label("Тон", ink, 9.5F, FontStyle.Regular), 0, 0);
            pitch.Minimum = -24; pitch.Maximum = 24; pitch.TickFrequency = 12; pitch.SmallChange = 1;
            pitch.BackColor = surface; pitch.Dock = DockStyle.Fill;
            pitch.ValueChanged += delegate { pitchText.Text = (pitch.Value > 0 ? "+" : "") + pitch.Value + " пт"; RefreshChart(); };
            tone.Controls.Add(pitch, 1, 0); pitchText.Text = "0 пт"; pitchText.ForeColor = mint;
            pitchText.Dock = DockStyle.Fill; pitchText.TextAlign = ContentAlignment.MiddleCenter; tone.Controls.Add(pitchText, 2, 0);
            Button reset = Button("Сброс", false); reset.Click += delegate { pitch.Value = 0; SetPlaybackSpeed(1); gap.Value = 0; rhythmMode.SelectedIndex = 0; glide.Value = 0; };
            tone.Controls.Add(reset, 3, 0); box.Controls.Add(tone, 0, 0);
            TableLayoutPanel tempo = Table(6, 1);
            foreach (int width in new[] { 58, 102, 75 }) tempo.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
            tempo.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tempo.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88)); tempo.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            tempo.Controls.Add(Label("Темп", ink, 9.5F, FontStyle.Regular), 0, 0);
            ConfigureNumber(tempoValue, 25, 300, 100, 1); tempoValue.DecimalPlaces = 2;
            tempoValue.ValueChanged += delegate { TempoValueChanged(); }; tempo.Controls.Add(tempoValue, 1, 0);
            ConfigureCombo(tempoUnit); tempoUnit.Items.Add("%"); tempoUnit.SelectedIndex = 0;
            tempoUnit.SelectedIndexChanged += delegate { if (!syncingTempo) RefreshTempoControls(); }; tempo.Controls.Add(tempoUnit, 2, 0);
            sourceTempoText.ForeColor = muted; sourceTempoText.Dock = DockStyle.Fill; sourceTempoText.AutoEllipsis = true;
            sourceTempoText.Font = new Font("Segoe UI", 8.7F); sourceTempoText.TextAlign = ContentAlignment.MiddleLeft; tempo.Controls.Add(sourceTempoText, 3, 0);
            Button original = Button("Оригинал", false); original.Click += delegate { SetPlaybackSpeed(1); }; tempo.Controls.Add(original, 4, 0);
            Button tap = Button("Настучать", false); tap.Click += delegate { TapBpm(); }; tempo.Controls.Add(tap, 5, 0); box.Controls.Add(tempo, 0, 1);
            TableLayoutPanel mode = Table(3, 1); mode.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            mode.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82)); mode.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 174));
            ConfigureCombo(output); output.Items.AddRange(new object[] { "Пищалка материнской платы", "Колонки — предпросмотр", "Диаграмма — без звука" }); output.SelectedIndex = 0;
            mode.Controls.Add(output, 0, 0); loop.Text = "Повтор"; loop.ForeColor = ink; loop.Dock = DockStyle.Fill; mode.Controls.Add(loop, 1, 0);
            Button rhythm = Button("Ритм и переходы…", false); rhythm.Click += delegate { if (!busy && !engine.IsPlaying) rhythmDialog.ShowDialog(this); };
            mode.Controls.Add(rhythm, 2, 0); box.Controls.Add(mode, 0, 2);
            ConfigureCombo(midiTrack); midiTrack.Items.Add("Все музыкальные партии"); midiTrack.SelectedIndex = 0;
            midiTrack.SelectedIndexChanged += delegate { if (!loading && selected != null && IsMidi(selected.Path)) LoadMidiSelection(); };
            releaseHints.SetToolTip(pitch, "Высота нот в полутонах. Новая частота = исходная × 2^(полутона / 12). +12 — октава выше, −12 — ниже. Длительность сохраняется.");
            releaseHints.SetToolTip(tempoValue, "BPM задаёт желаемый темп при известном исходном. Скорость = желаемый BPM / исходный BPM; новые времена = исходные / скорость. В процентах 100% — исходный таймлайн. Высота нот сохраняется.");
            releaseHints.SetToolTip(tempoUnit, "Переключает представление одного коэффициента скорости. BPM ↔ % не меняет трек и не округляет его времена заново.");
            releaseHints.SetToolTip(sourceTempoText, "BPM из MIDI либо сохранённых метаданных CSV. Он служит исходным ориентиром; при изменяющемся темпе MIDI сохраняются относительные изменения карты темпа.");
            releaseHints.SetToolTip(original, "Возвращает точный коэффициент скорости 1. Округлённое число BPM на экране не влияет на исходные времена.");
            releaseHints.SetToolTip(tap, "Нажми не менее трёх раз в ритм четвертей. BPM = 60 / медиана промежутков между нажатиями в секундах. При неизвестном BPM задаёт исходный ориентир, при известном — желаемую скорость.");
            releaseHints.SetToolTip(rhythm, "Музыкальная сетка, разрывы внутри нот и плавные переходы частоты. Расчёт шага, заполнения и интерполяции описан в подсказках каждого параметра.");
            releaseHints.SetToolTip(output, "Пищалка: InpOut и аппаратный speaker; права администратора запрашиваются при Играть. Колонки дают проверку нот. Диаграмма воспроизводит только визуальный таймлайн.");
            releaseHints.SetToolTip(reset, "Возвращает исходный тон, скорость 100%, выключает сетку, дополнительное разделение нот и плавные переходы.");
            return box;
        }

        private void BuildRhythmWindow()
        {
            rhythmDialog = new Form { Text = "Ритм и плавные переходы", ClientSize = new Size(680, 310),
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, BackColor = surface, ForeColor = ink, Font = Font };
            TableLayoutPanel box = Table(1, 6); box.Padding = new Padding(16);
            foreach (int height in new[] { 40, 40, 40, 40, 40, 40 }) box.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            TableLayoutPanel separation = Table(3, 1); separation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
            separation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); separation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            separation.Controls.Add(Label("Разделение нот, мс", ink, 9.5F, FontStyle.Regular), 0, 0);
            ConfigureNumber(gap, 0, 500, 0, 5); gap.ValueChanged += delegate { RefreshChart(); }; separation.Controls.Add(gap, 1, 0);
            sourceTempoButton = Button("Указать BPM файла…", false); sourceTempoButton.Click += delegate { EditSourceTempo(); }; separation.Controls.Add(sourceTempoButton, 2, 0);
            box.Controls.Add(separation, 0, 0);
            TableLayoutPanel smooth = Table(4, 1); smooth.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
            smooth.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); smooth.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            smooth.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            smooth.Controls.Add(Label("Плавный переход, мс", ink, 9.5F, FontStyle.Regular), 0, 0);
            ConfigureNumber(glide, 0, 1000, 0, 10); glide.ValueChanged += delegate { RefreshChart(); }; smooth.Controls.Add(glide, 1, 0);
            smooth.Controls.Add(Label("Шаг кривой, мс", ink, 9.5F, FontStyle.Regular), 2, 0);
            ConfigureCombo(curveResolution); curveResolution.Items.AddRange(new object[] { "5", "10", "20", "50" }); curveResolution.SelectedIndex = 1;
            curveResolution.SelectedIndexChanged += delegate { RefreshChart(); }; smooth.Controls.Add(curveResolution, 3, 0); box.Controls.Add(smooth, 0, 1);
            Label info = Label("Плавный переход работает между соседними нотами при Разделении = 0.", muted, 9F, FontStyle.Regular); box.Controls.Add(info, 0, 2);
            BuildRhythmControls(box, releaseHints);
            Button done = Button("Готово", true); done.DialogResult = DialogResult.OK;
            done.Dock = DockStyle.None; done.Anchor = AnchorStyles.Top | AnchorStyles.Right; done.Size = new Size(132, 36); done.Margin = new Padding(0, 3, 0, 0);
            box.Controls.Add(done, 0, 5); rhythmDialog.AcceptButton = done; rhythmDialog.Controls.Add(box);
            releaseHints.SetToolTip(gap, "Тишина вырезается из конца ноты: звук = D − min(разделение, D−1), пауза увеличивается на ту же величину. Следующая нота и общая длина не сдвигаются. Это артикуляция, не замедление.");
            releaseHints.SetToolTip(glide, "0 — мгновенная смена высоты. Иначе первые миллисекунды следующей ноты проходят от предыдущей частоты до новой. f(t) = exp(log(f0) + (log(f1) − log(f0)) × (3u²−2u³)), u=t/длительность перехода. Паузы не заполняются звуком.");
            releaseHints.SetToolTip(curveResolution, "Период пересчёта частоты во время плавного перехода. Меньший шаг создаёт больше точек кривой. Физический результат зависит от задержек Windows; это не повышение частоты дискретизации исходного MP3.");
        }

        private Control BuildTimelineSelector()
        {
            TableLayoutPanel row = Table(3, 1); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            selectionText.Text = "Старт 00:00.000"; selectionText.ForeColor = muted; selectionText.Dock = DockStyle.Fill;
            selectionText.TextAlign = ContentAlignment.MiddleLeft; selectionText.Font = new Font("Consolas", 9F); row.Controls.Add(selectionText, 0, 0);
            timelinePosition.Minimum = 0; timelinePosition.Maximum = 1000000; timelinePosition.TickStyle = TickStyle.None;
            timelinePosition.BackColor = background; timelinePosition.Dock = DockStyle.Fill; timelinePosition.SmallChange = 100; timelinePosition.LargeChange = 10000;
            timelinePosition.ValueChanged += delegate { if (!syncingPosition && durationMs > 0) SetTimelinePosition((long)Math.Round(durationMs * timelinePosition.Value / 1000000.0), true); };
            row.Controls.Add(timelinePosition, 1, 0);
            Button beginning = Button("С начала", false); beginning.Click += delegate { SetTimelinePosition(0, true); }; row.Controls.Add(beginning, 2, 0);
            releaseHints.SetToolTip(timelinePosition, "Выбери место запуска трека. При воспроизведении перематывает в выбранную позицию; пауза сохраняется. Можно также нажать или перетащить маркер прямо на нотах/диаграмме.");
            releaseHints.SetToolTip(beginning, "Переносит выбранную позицию к началу; во время проигрывания перематывает на 0.");
            return row;
        }

        private void SetTimelinePosition(long milliseconds, bool seekPlaying)
        {
            selectionPositionMs = Math.Max(0, Math.Min(durationMs, milliseconds));
            chart.SelectPosition(selectionPositionMs); selectionText.Text = "Старт " + PositionClock(selectionPositionMs);
            syncingPosition = true;
            try { timelinePosition.Value = durationMs <= 0 ? 0 : (int)Math.Max(0, Math.Min(1000000, Math.Round(selectionPositionMs * 1000000.0 / durationMs))); }
            finally { syncingPosition = false; }
            if (seekPlaying && engine.IsPlaying) engine.Seek(selectionPositionMs);
        }

        private static string PositionClock(long ms) { return String.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}.{2:000}", ms / 60000, ms / 1000 % 60, ms % 1000); }

        private void ChooseMidiPart()
        {
            if (closing || IsDisposed || busy || engine.IsPlaying || midiTrack.Items.Count < 3 || midiPartMenu.Visible) return;
            for (int index = midiPartMenu.Items.Count - 1; index >= 0; index--) midiPartMenu.Items[index].Dispose();
            for (int index = 0; index < midiTrack.Items.Count; index++) {
                int choice = index; ToolStripMenuItem item = new ToolStripMenuItem(midiTrack.Items[index].ToString());
                item.Checked = choice == midiTrack.SelectedIndex; item.Click += delegate { midiTrack.SelectedIndex = choice; };
                midiPartMenu.Items.Add(item);
            }
            // Closed runs inside WinForms' item-click handling; the menu stays alive until its owner is disposed.
            midiPartMenu.Show(partButton, new Point(0, partButton.Height));
        }
    }
}
