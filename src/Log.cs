using System;
using System.IO;

namespace ScreenPen
{
    // Plain-text diagnostics in ScreenPen.log next to the exe (restarted when it grows past 1 MB).
    static class Log
    {
        static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ScreenPen.log");
        const long MaxBytes = 1024 * 1024;

        public static void Write(string format, params object[] args)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) File.WriteAllText(FilePath, "");
                File.AppendAllText(FilePath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  ") + string.Format(format, args) + Environment.NewLine);
            }
            catch (Exception)
            {
                // Logging must never break the app.
            }
        }
    }
}
