using System.Drawing;
using System.IO;

namespace ScreenPen
{
    // Toolbar icons as WPF path data in an 18x18 box, plus the tray icon.
    static class Icons
    {
        public const string Pen = "M2,13 C4,3 7,3 8.5,8.5 S12.5,15 16,4";
        public const string Arrow = "M3,15 L15,3 M8,3 L15,3 L15,10";
        public const string Line = "M3,15 L15,3";
        public const string Rect = "M2.5,4 H15.5 V14 H2.5 Z";
        public const string Ellipse = "M1.5,9 A7.5,5.5 0 1 1 16.5,9 A7.5,5.5 0 1 1 1.5,9 Z";
        public const string Text = "M3,3.5 H15 M9,3.5 V15.5";
        public const string Moon = "M11,2.5 A7,7 0 1 0 15.5,12.5 A5.5,5.5 0 1 1 11,2.5 Z";
        public const string Flashlight = "M9,1.5 A7.5,7.5 0 1 1 9,16.5 A7.5,7.5 0 1 1 9,1.5 Z M9,6 A3,3 0 1 1 9,12 A3,3 0 1 1 9,6 Z";
        public const string Undo = "M6.5,3.5 L2.5,7.5 L6.5,11.5 M2.5,7.5 H11 A4,4 0 0 1 11,15.5 H7";
        public const string Trash = "M3,5 H15 M7,5 V2.5 H11 V5 M4.5,5 L5.5,16 H12.5 L13.5,5";
        public const string Close = "M4,4 L14,14 M14,4 L4,14";
        public const string Strip = "M2,4.5 H16 V13.5 H2 Z M6.5,4.5 V13.5 M11.5,4.5 V13.5";
        public const string Folder = "M2,5 H7 L8.5,6.5 H16 V14.5 H2 Z";
        public const string EyeOpen = "M1.5,9 C4,4.5 14,4.5 16.5,9 C14,13.5 4,13.5 1.5,9 Z M9,6.5 A2.5,2.5 0 1 1 9,11.5 A2.5,2.5 0 1 1 9,6.5 Z";
        public const string EyeClosed = "M1.5,8 C4,12 14,12 16.5,8 M4,10.5 L2.8,12.5 M9,12 V14.3 M14,10.5 L15.2,12.5";
        public const string LongPage ="M4,1.5 H14 V16.5 H4 Z M9,5 V12 M6.5,9.5 L9,12 L11.5,9.5";
        public const string Camera ="M2,6 H5.5 L7,4 H11 L12.5,6 H16 V15 H2 Z M9,7.5 A3,3 0 1 1 9,13.5 A3,3 0 1 1 9,7.5 Z";

        // The app icon (ScreenPen.ico, embedded by build.cmd) at the tray's size for the current DPI.
        public static Icon CreateTrayIcon()
        {
            using (Stream stream = typeof(Icons).Assembly.GetManifestResourceStream("ScreenPen.ico"))
            {
                return stream != null
                    ? new Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize)
                    : SystemIcons.Application;
            }
        }
    }
}
