using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VB = Microsoft.VisualBasic.FileIO;

namespace ScreenPen
{
    // Snapshot strip, like ScreenBrush: saved drawings that can be put back on screen,
    // plus a tab with region screenshots. Each snapshot is a folder:
    //   image.png  - the screen with the drawing,  thumb.png - its preview,
    //   strokes.isf - brush strokes,  shapes.xaml - arrows, lines, shapes and text.
    public partial class OverlayWindow
    {
        const double StripHeight = 196;
        const int ThumbWidth = 320;
        const int MaxScreenshotTiles = 60;

        static readonly Brush BadgeBrush = Frozen(new SolidColorBrush(Color.FromArgb(190, 20, 20, 22)));
        static readonly Brush BadgeHoverBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)));

        readonly Border strip = new Border();
        readonly StackPanel stripItems = new StackPanel { Orientation = Orientation.Horizontal };
        readonly ScrollViewer stripScroll = new ScrollViewer();
        Border stripButton, snapshotsTab, screenshotsTab;
        bool stripOpen, showingScreenshots, snapshotBusy;

        public static string SnapshotFolder
        {
            get { return Settings.SnapshotFolder; }
        }

        // ---------- strip UI ----------

        void BuildStrip()
        {
            var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
            snapshotsTab = MakeTab("Снимки", () => ShowStripTab(false));
            screenshotsTab = MakeTab("Скриншоты", () => ShowStripTab(true));
            header.Children.Add(snapshotsTab);
            header.Children.Add(screenshotsTab);

            var close = MakeButton(MakeIcon(Icons.Close), "Закрыть ленту  (G или Esc)", ToggleStrip);
            var folder = MakeButton(MakeIcon(Icons.Folder), "Открыть папку", OpenStripFolder);
            close.Width = close.Height = folder.Width = folder.Height = 30;
            DockPanel.SetDock(close, Dock.Right);
            DockPanel.SetDock(folder, Dock.Right);
            header.Children.Add(close);
            header.Children.Add(folder);

            stripScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            stripScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            stripScroll.Content = stripItems;
            stripScroll.PreviewMouseWheel += (s, e) =>
            {
                stripScroll.ScrollToHorizontalOffset(stripScroll.HorizontalOffset - e.Delta);
                e.Handled = true;
            };

            var body = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            body.Children.Add(header);
            body.Children.Add(stripScroll);

            strip.Child = body;
            strip.Height = StripHeight;
            strip.Background = PanelBrush;
            strip.CornerRadius = new CornerRadius(12);
            strip.Padding = new Thickness(10, 6, 10, 8);
            strip.BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)));
            strip.BorderThickness = new Thickness(1);
            strip.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.5 };
            strip.Visibility = Visibility.Collapsed;
            uiLayer.Children.Add(strip);
        }

        Border MakeTab(string text, Action onClick)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var tab = MakeButton(label, null, onClick);
            tab.Width = double.NaN;
            tab.Height = 30;
            tab.Padding = new Thickness(14, 0, 14, 0);
            tab.Margin = new Thickness(0, 0, 4, 0);
            return tab;
        }

        void ToggleStrip()
        {
            stripOpen = !stripOpen;
            if (stripOpen)
            {
                Rect area = ToWindowRect(System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea);
                strip.Width = Math.Max(300, area.Width - 32);
                Canvas.SetLeft(strip, area.Left + 16);
                Canvas.SetTop(strip, area.Bottom - StripHeight - 16);
                ShowStripTab(showingScreenshots);
            }
            strip.Visibility = stripOpen ? Visibility.Visible : Visibility.Collapsed;
            SetActive(stripButton, stripOpen);
        }

        void ShowStripTab(bool screenshots)
        {
            showingScreenshots = screenshots;
            SetActive(snapshotsTab, !screenshots);
            SetActive(screenshotsTab, screenshots);
            RefreshStrip();
        }

        void RefreshStrip()
        {
            stripItems.Children.Clear();
            if (showingScreenshots)
            {
                var files = Files(ScreenshotFolder, "*.png")
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(MaxScreenshotTiles);
                foreach (var file in files) AddTile(MakeScreenshotTile(file.FullName, file.LastWriteTime));
                if (stripItems.Children.Count == 0)
                    stripItems.Children.Add(MakeNote("Скриншотов пока нет. Кнопка с камерой или клавиша S."));
            }
            else
            {
                stripItems.Children.Add(MakeAddTile());
                foreach (var dir in Directories(SnapshotFolder).OrderByDescending(d => d.Name))
                    AddTile(MakeSnapshotTile(dir.FullName, dir.CreationTime));
            }
            stripScroll.ScrollToHorizontalOffset(0);
        }

        void AddTile(Border tile)
        {
            if (tile != null) stripItems.Children.Add(tile);
        }

        // Toolbar and strip are hidden while the screen is being captured.
        void SetChromeVisible(bool visible)
        {
            toolbar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            strip.Visibility = visible && stripOpen ? Visibility.Visible : Visibility.Collapsed;
        }

        void OpenStripFolder()
        {
            string folder = showingScreenshots ? ScreenshotFolder : SnapshotFolder;
            Directory.CreateDirectory(folder);
            HideOverlay(); // Explorer would open underneath the topmost overlay
            Process.Start("explorer.exe", "\"" + folder + "\"");
        }

        // ---------- tiles ----------

        Border MakeTile(UIElement preview, string caption, string tip, Action onClick, Action onDelete, ContextMenu menu)
        {
            var content = new StackPanel();
            if (onDelete != null)
            {
                var frame = new Grid();
                frame.Children.Add(preview);
                frame.Children.Add(MakeDeleteBadge(onDelete));
                content.Children.Add(frame);
            }
            else content.Children.Add(preview);
            content.Children.Add(new TextBlock
            {
                Text = caption,
                Foreground = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8))),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            });
            var tile = MakeButton(content, tip, onClick);
            tile.Width = 180;
            tile.Height = double.NaN;
            tile.Padding = new Thickness(6);
            tile.Margin = new Thickness(0, 0, 6, 0);
            tile.ContextMenu = menu;
            return tile;
        }

        // Round × in the top-right corner of a tile; its click does not reach the tile.
        static Border MakeDeleteBadge(Action onDelete)
        {
            var icon = MakeIcon(Icons.Close);
            icon.StrokeThickness = 2.4;
            icon.RenderTransform = new ScaleTransform(0.6, 0.6, 9, 9);
            var badge = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = BadgeBrush,
                Child = icon,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 3, 0),
                Cursor = Cursors.Hand,
                ToolTip = "Удалить (в Корзину)"
            };
            badge.MouseEnter += (s, e) => badge.Background = BadgeHoverBrush;
            badge.MouseLeave += (s, e) => badge.Background = BadgeBrush;
            badge.MouseLeftButtonDown += (s, e) => e.Handled = true;
            badge.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                onDelete();
            };
            return badge;
        }

        Border MakeAddTile()
        {
            var box = new Grid { Width = 168, Height = 100 };
            box.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Stroke = Frozen(new SolidColorBrush(Color.FromArgb(130, 255, 255, 255))),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                RadiusX = 8,
                RadiusY = 8
            });
            box.Children.Add(new TextBlock
            {
                Text = "+",
                FontSize = 44,
                Foreground = IconBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -8, 0, 0)
            });
            return MakeTile(box, "Сделать снимок", "Сохранить текущий рисунок  (Ctrl+S)", TakeSnapshot, null, null);
        }

        Border MakeSnapshotTile(string dir, DateTime time)
        {
            ImageSource thumb = LoadImage(Path.Combine(dir, "thumb.png"), 0);
            if (thumb == null) return null; // incomplete or foreign folder
            string image = Path.Combine(dir, "image.png");

            var menu = new ContextMenu();
            AddMenuItem(menu, "Вернуть на экран", () => RestoreSnapshot(dir));
            AddMenuItem(menu, "Копировать картинку", () => CopyImage(image));
            AddMenuItem(menu, "Сохранить картинку как…", () => SaveImageAs(image));
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "Удалить (в Корзину)", () => DeleteToRecycleBin(dir, true));

            return MakeTile(Preview(thumb), time.ToString("dd.MM  HH:mm"),
                "Клик — вернуть рисунок на экран. Правая кнопка — меню.", () => RestoreSnapshot(dir),
                () => DeleteToRecycleBin(dir, true), menu);
        }

        Border MakeScreenshotTile(string file, DateTime time)
        {
            ImageSource thumb = LoadImage(file, ThumbWidth);
            if (thumb == null) return null;

            var menu = new ContextMenu();
            AddMenuItem(menu, "Копировать", () => CopyImage(file));
            AddMenuItem(menu, "Открыть", () =>
            {
                HideOverlay();
                Process.Start(file);
            });
            AddMenuItem(menu, "Показать в папке", () =>
            {
                HideOverlay();
                Process.Start("explorer.exe", "/select,\"" + file + "\"");
            });
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "Удалить (в Корзину)", () => DeleteToRecycleBin(file, false));

            return MakeTile(Preview(thumb), time.ToString("dd.MM  HH:mm"),
                "Клик — копировать в буфер обмена. Правая кнопка — меню.", () => CopyImage(file),
                () => DeleteToRecycleBin(file, false), menu);
        }

        static Image Preview(ImageSource source)
        {
            return new Image { Source = source, Width = 168, Height = 100, Stretch = Stretch.Uniform };
        }

        static TextBlock MakeNote(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Frozen(new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0))),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
        }

        static void AddMenuItem(ContextMenu menu, string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => action();
            menu.Items.Add(item);
        }

        // ---------- saving and restoring drawings ----------

        void TakeSnapshot()
        {
            if (snapshotBusy || selecting) return;
            snapshotBusy = true;
            FinishText();
            CancelShape();
            HidePalette();
            Toast.Dismiss();
            SetChromeVisible(false);
            // Let the screen redraw without the toolbar and strip before capturing it.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                SaveSnapshot();
            };
            timer.Start();
        }

        void SaveSnapshot()
        {
            string error = null;
            try
            {
                string dir = UniquePath(SnapshotFolder, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"), "");
                Directory.CreateDirectory(dir);
                using (var shot = CaptureVirtualScreen())
                {
                    shot.Save(Path.Combine(dir, "image.png"), System.Drawing.Imaging.ImageFormat.Png);
                    using (var thumb = MakeThumbnail(shot, ThumbWidth))
                        thumb.Save(Path.Combine(dir, "thumb.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                using (var stream = File.Create(Path.Combine(dir, "strokes.isf")))
                    ink.Strokes.Save(stream);
                File.WriteAllText(Path.Combine(dir, "shapes.xaml"), SerializeShapes());
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            snapshotBusy = false;
            SetChromeVisible(true);

            if (error != null)
                MessageBox.Show(this, "Не удалось сохранить снимок:\n" + error, "ScreenPen",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            else if (stripOpen)
                ShowStripTab(false);
            else
                Toast.Notify("Снимок сохранён", "Лента снимков — клавиша G или кнопка на панели.", null);
        }

        // Shapes and text are cloned through XAML into a Canvas so the originals stay on screen.
        string SerializeShapes()
        {
            var canvas = new Canvas();
            foreach (UIElement child in ink.Children)
                canvas.Children.Add((UIElement)XamlReader.Parse(XamlWriter.Save(child)));
            return XamlWriter.Save(canvas);
        }

        void RestoreSnapshot(string dir)
        {
            try
            {
                StrokeCollection strokes = null;
                string strokesPath = Path.Combine(dir, "strokes.isf");
                if (File.Exists(strokesPath))
                {
                    using (var stream = File.OpenRead(strokesPath))
                        strokes = new StrokeCollection(stream);
                }
                Canvas shapes = null;
                string shapesPath = Path.Combine(dir, "shapes.xaml");
                if (File.Exists(shapesPath))
                    shapes = (Canvas)XamlReader.Parse(File.ReadAllText(shapesPath));

                ClearAll();
                if (strokes != null)
                {
                    ink.Strokes.Add(strokes);
                    foreach (Stroke stroke in strokes) history.Add(stroke);
                }
                if (shapes != null)
                {
                    List<UIElement> children = shapes.Children.Cast<UIElement>().ToList();
                    shapes.Children.Clear();
                    foreach (UIElement child in children)
                    {
                        ink.Children.Add(child);
                        history.Add(child);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось открыть снимок:\n" + ex.Message, "ScreenPen",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (stripOpen) ToggleStrip(); // show the whole drawing
            if (passThrough) RefreshEditLayer();
        }

        // ---------- image actions ----------

        void CopyImage(string file)
        {
            ImageSource image = LoadImage(file, 0);
            if (image == null) return;
            try
            {
                Clipboard.SetImage((BitmapSource)image);
                Toast.Notify("Скопировано", "Картинка в буфере обмена — вставьте Ctrl+V.", null);
            }
            catch (COMException)
            {
                Toast.Notify("Не скопировано", "Буфер обмена занят другой программой, попробуйте ещё раз.", null);
            }
        }

        void SaveImageAs(string file)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Картинка PNG|*.png",
                FileName = "ScreenPen_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".png"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                File.Copy(file, dialog.FileName, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось сохранить:\n" + ex.Message, "ScreenPen",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void DeleteToRecycleBin(string path, bool isDirectory)
        {
            try
            {
                if (isDirectory)
                    VB.FileSystem.DeleteDirectory(path, VB.UIOption.OnlyErrorDialogs, VB.RecycleOption.SendToRecycleBin);
                else
                    VB.FileSystem.DeleteFile(path, VB.UIOption.OnlyErrorDialogs, VB.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось удалить:\n" + ex.Message, "ScreenPen",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            double offset = stripScroll.HorizontalOffset; // keep the strip where it was
            RefreshStrip();
            stripScroll.ScrollToHorizontalOffset(offset);
        }

        // ---------- helpers ----------

        // Loads fully into memory (the file is not locked, so it can be deleted); null if unreadable.
        static BitmapImage LoadImage(string path, int decodeWidth)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static System.Drawing.Bitmap MakeThumbnail(System.Drawing.Bitmap source, int width)
        {
            int height = Math.Max(1, source.Height * width / source.Width);
            var thumb = new System.Drawing.Bitmap(width, height);
            using (var g = System.Drawing.Graphics.FromImage(thumb))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, 0, 0, width, height);
            }
            return thumb;
        }

        static string UniquePath(string folder, string name, string extension)
        {
            string path = Path.Combine(folder, name + extension);
            for (int i = 2; File.Exists(path) || Directory.Exists(path); i++)
                path = Path.Combine(folder, name + "_" + i + extension);
            return path;
        }

        static IEnumerable<FileInfo> Files(string folder, string pattern)
        {
            return Directory.Exists(folder) ? new DirectoryInfo(folder).GetFiles(pattern) : new FileInfo[0];
        }

        static IEnumerable<DirectoryInfo> Directories(string folder)
        {
            return Directory.Exists(folder) ? new DirectoryInfo(folder).GetDirectories() : new DirectoryInfo[0];
        }
    }
}
