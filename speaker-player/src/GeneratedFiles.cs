using System;
using System.Globalization;
using System.IO;

namespace SpeakerPlayer
{
    public static class GeneratedFiles
    {
        public static string CreateUniquePath(string directory, string title, string extension)
        {
            if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Нужна папка результата.", "directory");
            directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
            title = String.IsNullOrWhiteSpace(title) ? "Новый трек" : title.Trim();
            foreach (char character in Path.GetInvalidFileNameChars()) title = title.Replace(character, '_');
            title = title.Trim(' ', '.');
            if (title.Length == 0) title = "Новый трек";
            if (title.Length > 120) title = title.Substring(0, 120).Trim(' ', '.');
            if (!extension.StartsWith(".")) extension = "." + extension;
            if (extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || extension.Length > 12)
                throw new ArgumentException("Некорректное расширение.", "extension");
            string candidate = Path.Combine(directory, title + extension);
            int suffix = 2;
            while (File.Exists(candidate) || Directory.Exists(candidate))
                candidate = Path.Combine(directory, title + " (" + (suffix++).ToString(CultureInfo.InvariantCulture) + ")" + extension);
            return candidate;
        }
    }
}
