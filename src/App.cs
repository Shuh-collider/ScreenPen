using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace ScreenPen
{
    public static class App
    {
        static Mutex singleInstance;
        static Forms.NotifyIcon tray;
        static OverlayWindow overlay;

        [STAThread]
        public static void Main()
        {
            bool createdNew;
            singleInstance = new Mutex(true, "ScreenPen.SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("ScreenPen уже запущен — значок в трее у часов.", "ScreenPen");
                return;
            }

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            overlay = new OverlayWindow();
            string busyHotkeys = overlay.RegisterGlobalHotkeys();
            overlay.ScreenshotFinished += OnScreenshotFinished;

            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Рисовать  (Ctrl+Alt+D)", null, (s, e) => overlay.Toggle());
            menu.Items.Add("Скриншот области  (Ctrl+Alt+S)", null, (s, e) => overlay.StartScreenshot());
            menu.Items.Add("Длинный скриншот  (Ctrl+Alt+L)", null, (s, e) => overlay.StartLongScreenshot());
            menu.Items.Add("Открыть папку скриншотов", null, (s, e) => OpenScreenshotFolder(null));
            menu.Items.Add("Очистить рисунок", null, (s, e) => overlay.ClearAll());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, (s, e) => Exit());

            tray = new Forms.NotifyIcon();
            tray.Icon = Icons.CreateTrayIcon();
            tray.Text = "ScreenPen — рисование поверх экрана";
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) overlay.Toggle(); };
            tray.Visible = true;
            Toast.Notify("ScreenPen запущен",
                busyHotkeys == null
                    ? "Ctrl+Alt+D — рисовать, Ctrl+Alt+S — скриншот, Ctrl+Alt+L — длинный скриншот."
                    : "Сочетание " + busyHotkeys + " занято другой программой. Используйте меню значка в трее.",
                null);

            app.Run();
        }

        static void OnScreenshotFinished(string path, string error, string title)
        {
            if (error != null)
            {
                MessageBox.Show("Не удалось сделать скриншот:\n" + error, "ScreenPen",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (path == null)
            {
                // The "save as" dialog was cancelled.
                if (title != null) Toast.Notify(title, "Он остался в буфере обмена — вставьте Ctrl+V.", null);
                return;
            }
            Toast.Notify(title ?? "Скриншот сохранён",
                "Скопирован в буфер обмена — вставьте Ctrl+V.\n" + Path.GetFileName(path) + "\nНажмите, чтобы открыть папку.",
                () => OpenScreenshotFolder(path));
        }

        // Opens the snapshot folder, selecting the given file if it still exists.
        static void OpenScreenshotFolder(string file)
        {
            if (file != null && File.Exists(file))
            {
                Process.Start("explorer.exe", "/select,\"" + file + "\"");
                return;
            }
            Directory.CreateDirectory(OverlayWindow.ScreenshotFolder);
            Process.Start("explorer.exe", "\"" + OverlayWindow.ScreenshotFolder + "\"");
        }

        static void Exit()
        {
            tray.Visible = false;
            tray.Dispose();
            overlay.UnregisterGlobalHotkeys();
            overlay.AllowClose = true;
            Application.Current.Shutdown();
        }
    }
}
