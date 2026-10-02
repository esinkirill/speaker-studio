using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace SpeakerPlayer
{
    public sealed class LibraryStore
    {
        private readonly string root;
        private readonly string file;
        private readonly HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string LoadWarning { get; private set; }
        public IEnumerable<string> Paths { get { return paths.ToArray(); } }

        public LibraryStore(string directory)
        {
            root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            file = Path.Combine(root, "data", "library.json");
            if (!File.Exists(file)) return;
            try {
                string[] saved = new JavaScriptSerializer().Deserialize<string[]>(File.ReadAllText(file, Encoding.UTF8));
                if (saved == null) throw new InvalidDataException("Пустая запись библиотеки.");
                foreach (string item in saved) {
                    if (String.IsNullOrWhiteSpace(item)) continue;
                    string full = Path.GetFullPath(Path.IsPathRooted(item) ? item : Path.Combine(root, item));
                    paths.Add(full);
                }
            } catch (Exception error) {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is ArgumentException) && !(error is InvalidOperationException)) throw;
                LoadWarning = "Библиотека не прочитана: " + error.Message;
            }
        }

        public void Add(string path)
        {
            string full = Path.GetFullPath(path); bool added = paths.Add(full);
            try { Save(); } catch { if (added) paths.Remove(full); throw; }
        }
        public void Remove(string path)
        {
            string full = Path.GetFullPath(path); bool removed = paths.Remove(full);
            try { Save(); } catch { if (removed) paths.Add(full); throw; }
        }

        private void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string[] saved = paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p =>
                p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? p.Substring(root.Length) : p).ToArray();
            string temporary = file + ".new";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(saved), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(temporary, file, null);
            else File.Move(temporary, file);
        }
    }
}
