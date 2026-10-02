using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SpeakerPlayer
{
    public sealed partial class PlayerForm
    {
        private readonly List<SourceFile> allSources = new List<SourceFile>();
        private readonly TextBox librarySearch = new TextBox();
        private readonly ComboBox libraryFormat = new ComboBox();
        private readonly Label libraryCount = new Label();
        private Button removeTrack;
        private LibraryStore libraryStore;

        private Control BuildReleaseLibrary()
        {
            TableLayoutPanel box = Table(1, 7); box.BackColor = surface; box.Padding = new Padding(12);
            foreach (int height in new[] { 26, 36, 36, 38 }) box.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            box.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            box.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            box.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            box.Controls.Add(Label("БИБЛИОТЕКА", muted, 9F, FontStyle.Bold), 0, 0);
            librarySearch.Dock = DockStyle.Fill; librarySearch.BackColor = background; librarySearch.ForeColor = ink;
            librarySearch.BorderStyle = BorderStyle.FixedSingle; librarySearch.Margin = new Padding(0, 3, 0, 6);
            librarySearch.TextChanged += delegate { RefreshLibrary(null); }; box.Controls.Add(librarySearch, 0, 1);
            ConfigureCombo(libraryFormat); libraryFormat.Margin = new Padding(0, 1, 0, 6);
            libraryFormat.Items.AddRange(new object[] { "Все форматы", "MIDI", "CSV", "MP3 / WAV" }); libraryFormat.SelectedIndex = 0;
            libraryFormat.SelectedIndexChanged += delegate { RefreshLibrary(null); }; box.Controls.Add(libraryFormat, 0, 2);
            Button add = Button("+ Добавить треки", false); add.Margin = new Padding(0, 0, 0, 7);
            add.Click += delegate { AddTracksDialog(); }; box.Controls.Add(add, 0, 3);
            library.Dock = DockStyle.Fill; library.BackColor = surface; library.ForeColor = ink;
            library.BorderStyle = BorderStyle.None; library.SelectedIndexChanged += delegate { SelectFile(); };
            library.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Delete) { RemoveLibraryTrack(); e.Handled = true; } };
            ContextMenuStrip menu = new ContextMenuStrip(); menu.Items.Add("Убрать из библиотеки", null, delegate { RemoveLibraryTrack(); });
            library.ContextMenuStrip = menu; box.Controls.Add(library, 0, 4);
            libraryCount.ForeColor = muted; libraryCount.Dock = DockStyle.Fill; libraryCount.Font = new Font("Segoe UI", 8.5F);
            libraryCount.TextAlign = ContentAlignment.MiddleLeft; box.Controls.Add(libraryCount, 0, 5);
            removeTrack = Button("Убрать из списка", false); removeTrack.Click += delegate { RemoveLibraryTrack(); }; box.Controls.Add(removeTrack, 0, 6);
            releaseHints.SetToolTip(librarySearch, "Поиск по имени добавленных треков. Результаты обновляются во время ввода; текущий трек продолжает играть.");
            releaseHints.SetToolTip(libraryFormat, "Фильтр библиотеки по формату. MIDI содержит ноты, CSV — готовые частоты и времена, MP3/WAV открывается для конвертации.");
            releaseHints.SetToolTip(removeTrack, "Удаляет запись из библиотеки. Файл на диске сохраняется. Можно добавить его снова; клавиша Delete делает то же самое.");
            return box;
        }

        private void AddTracksDialog()
        {
            if (busy || engine.IsPlaying) return;
            using (OpenFileDialog dialog = new OpenFileDialog { Title = "Добавить музыку", Filter = "MIDI / CSV / аудио|*.mid;*.midi;*.csv;*.mp3;*.wav|Все файлы|*.*", Multiselect = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) foreach (string file in dialog.FileNames) AddFile(file, true);
        }

        private void LoadReleaseLibrary()
        {
            if (libraryStore == null) libraryStore = new LibraryStore(root);
            foreach (string path in libraryStore.Paths) if (File.Exists(path)) AddReleaseFile(path, false, false);
            if (!String.IsNullOrEmpty(libraryStore.LoadWarning)) Log(libraryStore.LoadWarning, Color.Salmon);
            RefreshLibrary(null);
        }

        private void AddReleaseFile(string path, bool select, bool persist)
        {
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) { Log("Нет файла: " + full, Color.Salmon); return; }
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (!new[] { ".mid", ".midi", ".csv", ".mp3", ".wav" }.Contains(ext)) return;
            SourceFile entry = allSources.FirstOrDefault(p => String.Equals(p.Path, full, StringComparison.OrdinalIgnoreCase));
            if (entry == null) { entry = new SourceFile { Path = full }; allSources.Add(entry); }
            if (persist) try { libraryStore.Add(full); } catch (Exception error) {
                if (!(error is IOException) && !(error is UnauthorizedAccessException)) throw;
                Log("Запись библиотеки: " + error.Message, Color.Salmon);
            }
            if (select) { librarySearch.Text = ""; libraryFormat.SelectedIndex = 0; }
            RefreshLibrary(select ? entry : null);
            if (select && (entry.Kind == "MP3" || entry.Kind == "WAV")) { transcription.AddAudio(entry.Path); studioTabs.SelectedTab = conversionTab; }
        }

        private void RefreshLibrary(SourceFile select)
        {
            SourceFile keep = select ?? library.SelectedItem as SourceFile;
            bool previous = loading; loading = true;
            try {
                library.BeginUpdate(); library.Items.Clear();
                string query = librarySearch.Text.Trim(); int format = libraryFormat.SelectedIndex;
                foreach (SourceFile file in allSources.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)) {
                    bool type = format <= 0 || format == 1 && IsMidi(file.Path) || format == 2 && file.Kind == "CSV" || format == 3 && (file.Kind == "MP3" || file.Kind == "WAV");
                    if (type && (query.Length == 0 || file.Title.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)) library.Items.Add(file);
                }
                if (keep != null && library.Items.Contains(keep)) library.SelectedItem = keep;
                libraryCount.Text = library.Items.Count + " из " + allSources.Count + " треков · поиск по имени";
            } finally { library.EndUpdate(); loading = previous; }
            if (select != null && !previous) SelectFile();
            if (removeTrack != null) removeTrack.Enabled = !busy && !engine.IsPlaying && library.SelectedItem != null;
        }

        private void RemoveLibraryTrack()
        {
            if (busy || engine.IsPlaying) return;
            SourceFile entry = library.SelectedItem as SourceFile; if (entry == null) return;
            try { libraryStore.Remove(entry.Path); } catch (Exception error) {
                if (!(error is IOException) && !(error is UnauthorizedAccessException)) throw;
                ShowError(error); return;
            }
            allSources.Remove(entry);
            if (selected == entry) {
                selected = null; notes.Clear(); playbackSession = 0; engine.Stop();
                partButton.Visible = false; midiTrack.Items.Clear(); midiTrack.Enabled = false; chart.PianoRoll = false;
                selectionPositionMs = 0; title.Text = "Добавь MIDI или CSV"; subtitle.Text = "Выбери трек в библиотеке";
                SetSourceTempo(null, "", false); RefreshChart();
            }
            RefreshLibrary(null); UpdateButtons(); Log("LIBRARY убран из списка: " + entry.Title, muted);
        }
    }
}
