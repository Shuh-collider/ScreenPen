using System;
using System.IO;

namespace ScreenPen
{
    // "key=value" settings in ScreenPen.ini next to the exe. Read on every use, so edits apply without a restart.
    static class Settings
    {
        static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ScreenPen.ini");

        // Region screenshots (PNG).
        public static string ScreenshotFolder
        {
            get { return Folder("ScreenshotFolder", "ScreenPen"); }
        }

        // Saved drawings shown in the snapshot strip.
        public static string SnapshotFolder
        {
            get { return Folder("SnapshotFolder", Path.Combine("ScreenPen", "Snapshots")); }
        }

        static string Folder(string key, string defaultUnderPictures)
        {
            string value = Read(key);
            return string.IsNullOrWhiteSpace(value)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), defaultUnderPictures)
                : Environment.ExpandEnvironmentVariables(value.Trim());
        }

        static string Read(string key)
        {
            if (!File.Exists(FilePath)) return null;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith(";") || trimmed.StartsWith("#")) continue;
                int eq = trimmed.IndexOf('=');
                if (eq > 0 && trimmed.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return trimmed.Substring(eq + 1);
            }
            return null;
        }
    }
}
