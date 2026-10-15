using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ScreenPen
{
    enum Tool { Pen, Arrow, Line, Rect, Ellipse, Text }

    // Transparent full-screen window over all monitors: drawing surface + toolbar.
    public partial class OverlayWindow : Window
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll", SetLastError = true)]
        static extern bool BitBlt(IntPtr hdcDest, int x, int y, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        const int SRCCOPY = 0x00CC0020;
        const int CAPTUREBLT = 0x40000000; // include layered windows (our drawings) in the capture

        const int WM_HOTKEY = 0x0312;
        const int WM_DISPLAYCHANGE = 0x007E;
        const int HOTKEY_TOGGLE_ID = 0x5150;
        const int HOTKEY_SCREENSHOT_ID = 0x5151;
        const int HOTKEY_LONG_ID = 0x5152;
        const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
        const uint VK_D = 0x44, VK_S = 0x53, VK_L = 0x4C;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;

        static readonly Color[] Palette =
        {
            Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xFF, 0x95, 0x00), Color.FromRgb(0xFF, 0xD6, 0x0A),
            Color.FromRgb(0x30, 0xD1, 0x58), Color.FromRgb(0x00, 0xE5, 0xFF), Color.FromRgb(0x0A, 0x84, 0xFF),
            Color.FromRgb(0xBF, 0x5A, 0xF2), Color.FromRgb(0xFF, 0x37, 0x8F), Color.FromRgb(0xFF, 0xFF, 0xFF),
            Color.FromRgb(0x1C, 0x1C, 0x1E)
        };
        static readonly double[] Sizes = { 3, 6, 12 };

        static readonly Brush PanelBrush = Frozen(new SolidColorBrush(Color.FromArgb(235, 32, 33, 36)));
        static readonly Brush HoverBrush = Frozen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)));
        static readonly Brush ActiveBrush = Frozen(new SolidColorBrush(Color.FromArgb(110, 10, 132, 255)));
        static readonly Brush IconBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)));
        // Alpha 1 instead of 0: fully transparent pixels of a layered window let clicks through.
        static readonly Brush ClearBackdrop = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
        static readonly Brush DimBackdrop = Frozen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)));

        readonly Grid root = new Grid();
        readonly InkCanvas ink = new InkCanvas();
        readonly Path flashlight = new Path();
        readonly EllipseGeometry flashHole = new EllipseGeometry(new Point(0, 0), 150, 150);
        readonly Canvas uiLayer = new Canvas();
        readonly Border toolbar = new Border();
        readonly Border palettePanel = new Border();
        readonly Ellipse colorDot = new Ellipse();
        readonly Ellipse sizeDot = new Ellipse();
        readonly Dictionary<Tool, Border> toolButtons = new Dictionary<Tool, Border>();
        // Strokes and shapes in the order they were drawn, for undo.
        readonly List<object> history = new List<object>();

        Border colorButton, dimButton, flashButton, eyeButton;
        Path eyeIcon;
        bool drawingsHidden;
        IntPtr hwnd;
        Tool tool = Tool.Pen;
        Color color;
        int sizeIndex = 1;
        bool dimmed, flashlightOn, flashlightHeld, toolbarPlaced;
        Path currentShape;
        Point shapeStart;
        bool drawingShape;
        TextBox editingText;
        bool draggingToolbar;
        Point dragOffset;
        double dpiScale = 1.0; // device pixels per WPF unit

        // Screenshot: the screen is frozen into a bitmap, then a region is selected on top of it.
        readonly Grid snapLayer = new Grid();
        readonly Image frozenImage = new Image();
        readonly RectangleGeometry selectionRect = new RectangleGeometry(new Rect(0, 0, 0, 0));
        readonly Path selectionMask = new Path();
        readonly TextBlock selectionLabel = new TextBlock();
        readonly Border snapHint = new Border();
        System.Drawing.Bitmap frozenShot;
        bool selecting, selectionDragging, screenshotReturnHidden;
        bool longMode;               // the selection is for a long (scrolling) screenshot
        ScrollCapture scrollCapture; // running long screenshot
        Point selectionStart;
        static string lastSaveFolder; // where the previous screenshot was saved

        public bool AllowClose { get; set; }

        // Raised after a screenshot: saved file path or null, error message or null, notification title or null.
        public event Action<string, string, string> ScreenshotFinished;

        public static string ScreenshotFolder
        {
            get { return Settings.ScreenshotFolder; }
        }

        public OverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Title = "ScreenPen";
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            color = Palette[0];

            root.Background = ClearBackdrop;
            root.Focusable = true;
            root.FocusVisualStyle = null;

            ink.Background = Brushes.Transparent;
            ink.UseCustomCursor = true;
            ink.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 1.5, Opacity = 0.45 };
            ink.StrokeCollected += (s, e) => history.Add(e.Stroke);
            ink.PreviewMouseLeftButtonDown += Ink_MouseDown;
            ink.PreviewMouseMove += Ink_MouseMove;
            ink.PreviewMouseLeftButtonUp += Ink_MouseUp;
            root.Children.Add(ink);

            flashlight.Fill = Frozen(new SolidColorBrush(Color.FromArgb(205, 0, 0, 0)));
            flashlight.IsHitTestVisible = false;
            flashlight.Visibility = Visibility.Collapsed;
            flashlight.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, Width, Height)), flashHole);
            root.Children.Add(flashlight);

            root.Children.Add(uiLayer);
            BuildToolbar();
            BuildPalette();
            BuildStrip();
            BuildSnapLayer();
            root.Children.Add(snapLayer);
            Content = root;

            ApplyPen();
            UpdateBackdrop();

            PreviewMouseMove += (s, e) =>
            {
                if (flashlight.Visibility == Visibility.Visible) flashHole.Center = e.GetPosition(root);
            };
            PreviewKeyDown += OnKeyDown;
            PreviewKeyUp += OnKeyUp;
        }

        static Brush Frozen(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }

        // ---------- global hotkey and show/hide ----------

        // Returns the hotkeys that could not be registered (taken by another app), or null.
        public string RegisterGlobalHotkeys()
        {
            hwnd = new WindowInteropHelper(this).EnsureHandle();
            var source = HwndSource.FromHwnd(hwnd);
            source.AddHook(WndProc);
            dpiScale = source.CompositionTarget.TransformToDevice.M11;
            // Tool window: keeps the overlay out of Alt+Tab.
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

            var failed = new List<string>();
            if (!RegisterHotKey(hwnd, HOTKEY_TOGGLE_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_D)) failed.Add("Ctrl+Alt+D");
            if (!RegisterHotKey(hwnd, HOTKEY_SCREENSHOT_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_S)) failed.Add("Ctrl+Alt+S");
            if (!RegisterHotKey(hwnd, HOTKEY_LONG_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_L)) failed.Add("Ctrl+Alt+L");
            return failed.Count == 0 ? null : string.Join(", ", failed);
        }

        public void UnregisterGlobalHotkeys()
        {
            if (hwnd == IntPtr.Zero) return;
            UnregisterHotKey(hwnd, HOTKEY_TOGGLE_ID);
            UnregisterHotKey(hwnd, HOTKEY_SCREENSHOT_ID);
            UnregisterHotKey(hwnd, HOTKEY_LONG_ID);
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DISPLAYCHANGE)
            {
                // A monitor was added, removed or changed resolution; let Windows finish, then cover the screens again.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FitToScreens));
                return IntPtr.Zero;
            }
            if (msg != WM_HOTKEY) return IntPtr.Zero;
            int id = wParam.ToInt32();
            if (id == HOTKEY_TOGGLE_ID)
            {
                Toggle();
                handled = true;
            }
            else if (id == HOTKEY_SCREENSHOT_ID)
            {
                StartScreenshot();
                handled = true;
            }
            else if (id == HOTKEY_LONG_ID)
            {
                StartLongScreenshot();
                handled = true;
            }
            else if (id == HOTKEY_SPACE_ID)
            {
                handled = true; // a repeat of the held Space: swallowed on purpose
            }
            return IntPtr.Zero;
        }

        public void Toggle()
        {
            if (scrollCapture != null)
            {
                scrollCapture.Stop(); // showing the overlay now would catch the wheel
                return;
            }
            if (IsVisible) HideOverlay();
            else ShowOverlay();
        }

        public void ShowOverlay()
        {
            if (!toolbarPlaced) PlaceToolbar();
            else KeepToolbarOnScreen();
            Show();
            Activate();
            Keyboard.Focus(root);
        }

        public void HideOverlay()
        {
            ExitPassThrough();
            if (selecting)
            {
                screenshotReturnHidden = true;
                EndScreenshot();
                return;
            }
            FinishText();
            CancelShape();
            flashlightHeld = false;
            UpdateBackdrop();
            HidePalette();
            if (stripOpen) ToggleStrip();
            Hide();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Alt+F4 only hides the overlay; the app exits from the tray menu.
            if (!AllowClose)
            {
                e.Cancel = true;
                HideOverlay();
            }
            base.OnClosing(e);
        }

        // First show: toolbar at the left edge of the monitor under the cursor.
        void PlaceToolbar()
        {
            toolbarPlaced = true;
            Rect area = ToWindowRect(System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea);
            toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = area.Left + 16;
            double y = area.Top + Math.Max(10, (area.Height - toolbar.DesiredSize.Height) / 2);
            Canvas.SetLeft(toolbar, x);
            Canvas.SetTop(toolbar, y);
        }

        // Keeps the whole toolbar inside the working area of the monitor nearest to it.
        void KeepToolbarOnScreen()
        {
            double x = Canvas.GetLeft(toolbar), y = Canvas.GetTop(toolbar);
            if (double.IsNaN(x) || double.IsNaN(y)) return;
            toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var box = new Rect(new Point(x, y), toolbar.DesiredSize);
            Point position = ClampInto(WorkAreaAt(new Point(box.X + box.Width / 2, box.Y + box.Height / 2)), box);
            Canvas.SetLeft(toolbar, position.X);
            Canvas.SetTop(toolbar, position.Y);
        }

        // Top-left corner that puts the box inside the area (top-left aligned if the box is bigger).
        static Point ClampInto(Rect area, Rect box)
        {
            double x = Math.Max(area.Left, Math.Min(box.X, area.Right - box.Width));
            double y = Math.Max(area.Top, Math.Min(box.Y, area.Bottom - box.Height));
            return new Point(x, y);
        }

        // Monitors were added, removed or changed resolution: cover the new virtual screen. The drawings stay
        // where they were on the screen, and the toolbar is brought back onto a monitor.
        void FitToScreens()
        {
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            double left = screen.X / dpiScale, top = screen.Y / dpiScale;
            double width = screen.Width / dpiScale, height = screen.Height / dpiScale;
            if (left == Left && top == Top && width == Width && height == Height) return;
            Log.Write("Screens changed: virtual screen {0}", screen);

            // Window coordinates start at the virtual screen's corner; if the corner moved, move everything back.
            var shift = new Vector(Left - left, Top - top);
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            var whole = new RectangleGeometry(new Rect(0, 0, width, height));
            flashlight.Data = new CombinedGeometry(GeometryCombineMode.Exclude, whole, flashHole);
            selectionMask.Data = new CombinedGeometry(GeometryCombineMode.Exclude, whole, selectionRect);
            if (shift.X != 0 || shift.Y != 0)
            {
                OffsetDrawings(shift);
                Canvas.SetLeft(toolbar, Canvas.GetLeft(toolbar) + shift.X);
                Canvas.SetTop(toolbar, Canvas.GetTop(toolbar) + shift.Y);
            }
            KeepToolbarOnScreen();
            HidePalette();
            if (stripOpen) ToggleStrip(); // reopening places it on a monitor again
            if (passThrough) RefreshEditLayer();
        }

        // ---------- toolbar ----------

        void BuildToolbar()
        {
            var stack = new StackPanel();

            var grip = new Border
            {
                Height = 16,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                ToolTip = "Перетащить панель",
                Child = new Border
                {
                    Width = 18,
                    Height = 3,
                    CornerRadius = new CornerRadius(1.5),
                    Background = Frozen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255))),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            grip.MouseLeftButtonDown += Grip_Down;
            grip.MouseMove += Grip_Move;
            grip.MouseLeftButtonUp += Grip_Up;
            stack.Children.Add(grip);

            AddTool(stack, Tool.Pen, Icons.Pen, "Кисть  (1)");
            AddTool(stack, Tool.Arrow, Icons.Arrow, "Стрелка  (2)");
            AddTool(stack, Tool.Line, Icons.Line, "Линия  (3)");
            AddTool(stack, Tool.Rect, Icons.Rect, "Прямоугольник  (4)");
            AddTool(stack, Tool.Ellipse, Icons.Ellipse, "Овал  (5)");
            AddTool(stack, Tool.Text, Icons.Text, "Текст  (6)");
            stack.Children.Add(MakeSeparator());

            colorDot.Width = 20;
            colorDot.Height = 20;
            colorDot.Stroke = Frozen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)));
            colorDot.StrokeThickness = 1.5;
            colorDot.HorizontalAlignment = HorizontalAlignment.Center;
            colorDot.VerticalAlignment = VerticalAlignment.Center;
            colorButton = MakeButton(colorDot, "Цвет  (C)", TogglePalette);
            stack.Children.Add(colorButton);

            sizeDot.Fill = IconBrush;
            sizeDot.HorizontalAlignment = HorizontalAlignment.Center;
            sizeDot.VerticalAlignment = VerticalAlignment.Center;
            stack.Children.Add(MakeButton(sizeDot, "Толщина  ( [  ] )", CycleSize));
            stack.Children.Add(MakeSeparator());

            dimButton = MakeButton(MakeIcon(Icons.Moon), "Затемнить экран  (D)", ToggleDim);
            flashButton = MakeButton(MakeIcon(Icons.Flashlight), "Фонарик  (F, или держать Shift)", ToggleFlashlight);
            stack.Children.Add(dimButton);
            stack.Children.Add(flashButton);
            stack.Children.Add(MakeButton(MakeIcon(Icons.Camera), "Скриншот области  (S, или Ctrl+Alt+S)", StartScreenshot));
            stack.Children.Add(MakeButton(MakeIcon(Icons.LongPage), "Длинный скриншот с прокруткой  (L, или Ctrl+Alt+L)", StartLongScreenshot));
            stripButton = MakeButton(MakeIcon(Icons.Strip), "Лента снимков  (G).  Сохранить снимок — Ctrl+S", ToggleStrip);
            stack.Children.Add(stripButton);
            stack.Children.Add(MakeSeparator());

            eyeIcon = MakeIcon(Icons.EyeOpen);
            eyeButton = MakeButton(eyeIcon, "Скрыть рисунки  (H)", ToggleDrawings);
            stack.Children.Add(eyeButton);
            stack.Children.Add(MakeButton(MakeIcon(Icons.Undo), "Отменить  (Ctrl+Z)", Undo));
            stack.Children.Add(MakeButton(MakeIcon(Icons.Trash), "Очистить всё  (Delete)", ClearAll));
            stack.Children.Add(MakeButton(MakeIcon(Icons.Close), "Скрыть  (Esc)", HideOverlay));

            toolbar.Child = stack;
            toolbar.Background = PanelBrush;
            toolbar.CornerRadius = new CornerRadius(12);
            toolbar.Padding = new Thickness(5, 0, 5, 6);
            toolbar.BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)));
            toolbar.BorderThickness = new Thickness(1);
            toolbar.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.5 };
            uiLayer.Children.Add(toolbar);

            SelectTool(Tool.Pen);
        }

        void AddTool(Panel panel, Tool t, string iconData, string tip)
        {
            Tool selected = t;
            var button = MakeButton(MakeIcon(iconData), tip, () => SelectTool(selected));
            toolButtons[t] = button;
            panel.Children.Add(button);
        }

        Border MakeButton(UIElement content, string tip, Action onClick)
        {
            var b = new Border
            {
                Width = 38,
                Height = 38,
                Margin = new Thickness(0, 1, 0, 1),
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                ToolTip = tip,
                Child = content,
                Tag = false // "active" flag
            };
            b.MouseEnter += (s, e) => { if (!(bool)b.Tag) b.Background = HoverBrush; };
            b.MouseLeave += (s, e) => { b.Background = (bool)b.Tag ? ActiveBrush : Brushes.Transparent; };
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
            return b;
        }

        static void SetActive(Border b, bool active)
        {
            b.Tag = active;
            b.Background = active ? ActiveBrush : (b.IsMouseOver ? HoverBrush : Brushes.Transparent);
        }

        static Path MakeIcon(string data)
        {
            return new Path
            {
                Data = Geometry.Parse(data),
                Stroke = IconBrush,
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        static Border MakeSeparator()
        {
            return new Border
            {
                Height = 1,
                Margin = new Thickness(6, 4, 6, 4),
                Background = Frozen(new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)))
            };
        }

        void Grip_Down(object sender, MouseButtonEventArgs e)
        {
            HidePalette();
            draggingToolbar = true;
            dragOffset = e.GetPosition(toolbar);
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        void Grip_Move(object sender, MouseEventArgs e)
        {
            if (!draggingToolbar) return;
            Point p = e.GetPosition(uiLayer);
            Canvas.SetLeft(toolbar, p.X - dragOffset.X);
            Canvas.SetTop(toolbar, p.Y - dragOffset.Y);
        }

        void Grip_Up(object sender, MouseButtonEventArgs e)
        {
            draggingToolbar = false;
            ((UIElement)sender).ReleaseMouseCapture();
            KeepToolbarOnScreen(); // dropped partly off-screen: slide it back fully onto the monitor
        }

        // ---------- colors and sizes ----------

        void BuildPalette()
        {
            var wrap = new WrapPanel { Width = 5 * 30 };
            foreach (Color c in Palette)
            {
                Color swatch = c;
                var dot = new Ellipse
                {
                    Width = 22,
                    Height = 22,
                    Margin = new Thickness(4),
                    Cursor = Cursors.Hand,
                    Fill = Frozen(new SolidColorBrush(swatch)),
                    Stroke = Frozen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255))),
                    StrokeThickness = 1
                };
                dot.MouseLeftButtonDown += (s, e) => e.Handled = true;
                dot.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    color = swatch;
                    ApplyPen();
                    HidePalette();
                };
                wrap.Children.Add(dot);
            }
            palettePanel.Child = wrap;
            palettePanel.Background = PanelBrush;
            palettePanel.CornerRadius = new CornerRadius(10);
            palettePanel.Padding = new Thickness(4);
            palettePanel.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.5 };
            palettePanel.Visibility = Visibility.Collapsed;
            uiLayer.Children.Add(palettePanel);
        }

        void TogglePalette()
        {
            if (palettePanel.Visibility == Visibility.Visible)
            {
                HidePalette();
                return;
            }
            // Next to the toolbar on the side where it fits on this monitor (right by default), never off-screen.
            // Visible before measuring: a collapsed element measures as zero.
            palettePanel.Visibility = Visibility.Visible;
            palettePanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size size = palettePanel.DesiredSize;
            Point p = colorButton.TranslatePoint(new Point(0, 0), uiLayer);
            Rect monitor = MonitorRectAt(p);
            double toolbarLeft = Canvas.GetLeft(toolbar), gap = 8;
            double x = toolbarLeft + toolbar.ActualWidth + gap;
            if (x + size.Width > monitor.Right) x = toolbarLeft - size.Width - gap;
            double y = Math.Max(monitor.Top + gap, Math.Min(p.Y - 8, monitor.Bottom - size.Height - gap));
            Canvas.SetLeft(palettePanel, x);
            Canvas.SetTop(palettePanel, y);
        }

        void HidePalette()
        {
            palettePanel.Visibility = Visibility.Collapsed;
        }

        void ApplyPen()
        {
            double size = Sizes[sizeIndex];
            ink.DefaultDrawingAttributes = new DrawingAttributes
            {
                Color = color,
                Width = size,
                Height = size,
                FitToCurve = true,
                IgnorePressure = false,
                StylusTip = StylusTip.Ellipse
            };
            colorDot.Fill = new SolidColorBrush(color);
            sizeDot.Width = sizeDot.Height = 4 + sizeIndex * 5;
        }

        void CycleSize()
        {
            sizeIndex = (sizeIndex + 1) % Sizes.Length;
            ApplyPen();
        }

        void ChangeSize(int delta)
        {
            sizeIndex = Math.Max(0, Math.Min(Sizes.Length - 1, sizeIndex + delta));
            ApplyPen();
        }

        // ---------- tools ----------

        void SelectTool(Tool t)
        {
            FinishText();
            tool = t;
            foreach (var pair in toolButtons) SetActive(pair.Value, pair.Key == t);
            // While Space is held drawings are moved, not drawn: the brush stays off until Space is released.
            ink.EditingMode = t == Tool.Pen && !passThrough ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
            ink.Cursor = t == Tool.Pen ? Cursors.Pen : (t == Tool.Text ? Cursors.IBeam : Cursors.Cross);
            HidePalette();
        }

        void Ink_MouseDown(object sender, MouseButtonEventArgs e)
        {
            HidePalette();
            if (passThrough) return; // Space held: moving drawings, not drawing
            if (tool == Tool.Pen) return; // InkCanvas draws the stroke itself

            Point p = e.GetPosition(ink);
            if (tool == Tool.Text)
            {
                if (editingText != null && IsInside(e.OriginalSource as DependencyObject, editingText)) return;
                FinishText();
                StartText(p);
                e.Handled = true;
                return;
            }

            shapeStart = p;
            currentShape = new Path
            {
                Stroke = new SolidColorBrush(color),
                StrokeThickness = Sizes[sizeIndex],
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            };
            UpdateShape(p);
            ink.Children.Add(currentShape);
            drawingShape = true;
            ink.CaptureMouse();
            e.Handled = true;
        }

        void Ink_MouseMove(object sender, MouseEventArgs e)
        {
            if (drawingShape) UpdateShape(e.GetPosition(ink));
        }

        void Ink_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!drawingShape) return;
            Point p = e.GetPosition(ink);
            UpdateShape(p);
            drawingShape = false;
            ink.ReleaseMouseCapture();
            if ((p - shapeStart).Length < 4) ink.Children.Remove(currentShape); // just a click
            else history.Add(currentShape);
            currentShape = null;
            e.Handled = true;
        }

        void CancelShape()
        {
            if (!drawingShape) return;
            drawingShape = false;
            ink.ReleaseMouseCapture();
            ink.Children.Remove(currentShape);
            currentShape = null;
        }

        void UpdateShape(Point p)
        {
            switch (tool)
            {
                case Tool.Line:
                    currentShape.Data = new LineGeometry(shapeStart, p);
                    break;
                case Tool.Rect:
                    currentShape.Data = new RectangleGeometry(new Rect(shapeStart, p), 4, 4);
                    break;
                case Tool.Ellipse:
                    currentShape.Data = new EllipseGeometry(new Rect(shapeStart, p));
                    break;
                case Tool.Arrow:
                    currentShape.Data = ArrowGeometry(shapeStart, p, Sizes[sizeIndex]);
                    break;
            }
        }

        static Geometry ArrowGeometry(Point from, Point to, double thickness)
        {
            Vector direction = to - from;
            double length = direction.Length;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(from, false, false);
                ctx.LineTo(to, true, true);
                if (length > 1)
                {
                    direction.Normalize();
                    double head = Math.Min(length * 0.5, 14 + thickness * 2.5);
                    var normal = new Vector(-direction.Y, direction.X);
                    Point back = to - direction * head;
                    ctx.BeginFigure(back + normal * head * 0.55, false, false);
                    ctx.LineTo(to, true, true);
                    ctx.LineTo(back - normal * head * 0.55, true, true);
                }
            }
            // PathGeometry, not StreamGeometry: XamlWriter saves StreamGeometry empty, losing arrows in snapshots.
            PathGeometry path = PathGeometry.CreateFromGeometry(geometry);
            path.Freeze();
            return path;
        }

        // ---------- text ----------

        void StartText(Point p)
        {
            double fontSize = 18 + Sizes[sizeIndex] * 2.2;
            editingText = new TextBox
            {
                FontSize = fontSize,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(color),
                CaretBrush = new SolidColorBrush(color),
                Background = Frozen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0))),
                BorderBrush = new SolidColorBrush(Color.FromArgb(160, color.R, color.G, color.B)),
                BorderThickness = new Thickness(1),
                MinWidth = 40,
                Padding = new Thickness(4, 0, 4, 0)
            };
            InkCanvas.SetLeft(editingText, p.X);
            InkCanvas.SetTop(editingText, p.Y - fontSize * 0.7);
            ink.Children.Add(editingText);
            editingText.Focus();
            Keyboard.Focus(editingText);
        }

        void FinishText()
        {
            if (editingText == null) return;
            TextBox box = editingText;
            editingText = null;
            ink.Children.Remove(box);
            Keyboard.Focus(root);
            if (box.Text.Trim().Length == 0) return;

            var block = new TextBlock
            {
                Text = box.Text,
                FontSize = box.FontSize,
                FontFamily = box.FontFamily,
                FontWeight = box.FontWeight,
                Foreground = box.Foreground,
                Padding = new Thickness(5, 1, 5, 1),
                IsHitTestVisible = false
            };
            InkCanvas.SetLeft(block, InkCanvas.GetLeft(box));
            InkCanvas.SetTop(block, InkCanvas.GetTop(box));
            ink.Children.Add(block);
            history.Add(block);
        }

        void CancelText()
        {
            if (editingText == null) return;
            ink.Children.Remove(editingText);
            editingText = null;
            Keyboard.Focus(root);
        }

        static bool IsInside(DependencyObject node, DependencyObject container)
        {
            while (node != null)
            {
                if (node == container) return true;
                node = node is Visual || node is Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        // ---------- undo, clear, dim, flashlight ----------

        public void Undo()
        {
            if (editingText != null)
            {
                CancelText();
                return;
            }
            if (history.Count == 0) return;
            object last = history[history.Count - 1];
            history.RemoveAt(history.Count - 1);
            var edit = last as EditRecord;
            var stroke = last as Stroke;
            if (edit != null) edit.Undo(); // a move or reshape
            else if (stroke != null) ink.Strokes.Remove(stroke);
            else ink.Children.Remove((UIElement)last);
            if (passThrough) RefreshEditLayer();
        }

        // Clears the drawing; one undo step brings all of it back, with its own undo history.
        public void ClearAll()
        {
            CancelText();
            CancelShape();
            if (ink.Strokes.Count > 0 || ink.Children.Count > 0)
            {
                var strokes = new StrokeCollection(ink.Strokes);
                var children = new List<UIElement>();
                foreach (UIElement child in ink.Children) children.Add(child);
                var before = new List<object>(history);
                ink.Strokes.Clear();
                ink.Children.Clear();
                history.Clear();
                history.Add(new EditRecord(() =>
                {
                    ink.Strokes.Add(strokes);
                    foreach (UIElement child in children) ink.Children.Add(child);
                    history.Clear();
                    history.AddRange(before);
                }));
            }
            if (passThrough) RefreshEditLayer();
        }

        // The eye: hides all drawings without deleting them (and shows them again).
        void ToggleDrawings()
        {
            FinishText();
            CancelShape();
            drawingsHidden = !drawingsHidden;
            ink.Visibility = drawingsHidden ? Visibility.Hidden : Visibility.Visible;
            eyeIcon.Data = Geometry.Parse(drawingsHidden ? Icons.EyeClosed : Icons.EyeOpen);
            eyeButton.ToolTip = drawingsHidden ? "Показать рисунки  (H)" : "Скрыть рисунки  (H)";
            SetActive(eyeButton, drawingsHidden); // highlighted while the drawings are hidden
            if (passThrough) RefreshEditLayer(); // hidden drawings must not keep catching clicks
        }

        void ToggleDim()
        {
            dimmed = !dimmed;
            UpdateBackdrop();
        }

        void ToggleFlashlight()
        {
            flashlightOn = !flashlightOn;
            UpdateBackdrop();
        }

        void UpdateBackdrop()
        {
            bool flash = !passThrough && (flashlightOn || flashlightHeld);
            // While Space is held the backdrop is fully transparent, so clicks outside the drawings go through.
            root.Background = passThrough ? Brushes.Transparent : (dimmed && !flash ? DimBackdrop : ClearBackdrop);
            flashlight.Visibility = flash ? Visibility.Visible : Visibility.Collapsed;
            if (flash) flashHole.Center = Mouse.GetPosition(root);
            SetActive(dimButton, dimmed);
            SetActive(flashButton, flashlightOn);
        }

        // ---------- screenshot ----------

        void BuildSnapLayer()
        {
            snapLayer.Visibility = Visibility.Collapsed;
            snapLayer.Background = Brushes.Transparent;
            snapLayer.Cursor = Cursors.Cross;

            frozenImage.Stretch = Stretch.Fill;
            snapLayer.Children.Add(frozenImage);

            // Darken everything except the selected rectangle.
            selectionMask.Fill = Frozen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)));
            selectionMask.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, Width, Height)), selectionRect);
            selectionMask.IsHitTestVisible = false;
            snapLayer.Children.Add(selectionMask);
            snapLayer.Children.Add(new Path
            {
                Data = selectionRect,
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                IsHitTestVisible = false
            });

            selectionLabel.Foreground = Brushes.White;
            selectionLabel.Background = PanelBrush;
            selectionLabel.Padding = new Thickness(6, 2, 6, 2);
            selectionLabel.FontSize = 12;
            selectionLabel.HorizontalAlignment = HorizontalAlignment.Left;
            selectionLabel.VerticalAlignment = VerticalAlignment.Top;
            selectionLabel.IsHitTestVisible = false;
            selectionLabel.Visibility = Visibility.Collapsed;
            snapLayer.Children.Add(selectionLabel);

            snapHint.Child = new TextBlock
            {
                Text = "Выделите область мышью   ·   клик или Enter — весь монитор   ·   Esc — отмена",
                Foreground = Brushes.White,
                FontSize = 14
            };
            snapHint.Background = PanelBrush;
            snapHint.CornerRadius = new CornerRadius(8);
            snapHint.Padding = new Thickness(14, 8, 14, 8);
            snapHint.HorizontalAlignment = HorizontalAlignment.Left;
            snapHint.VerticalAlignment = VerticalAlignment.Top;
            snapHint.IsHitTestVisible = false;
            snapLayer.Children.Add(snapHint);

            snapLayer.MouseLeftButtonDown += Snap_Down;
            snapLayer.MouseMove += Snap_Move;
            snapLayer.MouseLeftButtonUp += Snap_Up;
            snapLayer.MouseRightButtonUp += (s, e) =>
            {
                EndScreenshot();
                e.Handled = true;
            };
        }

        public void StartScreenshot()
        {
            BeginScreenshot(false);
        }

        // Same selection as a screenshot; then the chosen area is scrolled and stitched.
        public void StartLongScreenshot()
        {
            BeginScreenshot(true);
        }

        void BeginScreenshot(bool longScreenshot)
        {
            if (selecting || scrollCapture != null) return;
            selecting = true;
            longMode = longScreenshot;
            FinishText();
            CancelShape();
            HidePalette();
            if (flashlightHeld)
            {
                flashlightHeld = false;
                UpdateBackdrop();
            }

            screenshotReturnHidden = !IsVisible;
            bool hadToast = Toast.Dismiss();
            if (screenshotReturnHidden && !hadToast)
            {
                BeginSelection();
                return;
            }
            // Hide the toolbar, strip and any toast; give the screen a moment to redraw before freezing it.
            SetChromeVisible(false);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (selecting) BeginSelection();
            };
            timer.Start();
        }

        void BeginSelection()
        {
            try
            {
                frozenShot = CaptureVirtualScreen();
            }
            catch (Exception ex)
            {
                EndScreenshot();
                RaiseScreenshotFinished(null, ex.Message);
                return;
            }
            frozenImage.Source = ToBitmapSource(frozenShot);
            selectionRect.Rect = new Rect(0, 0, 0, 0);
            selectionLabel.Visibility = Visibility.Collapsed;

            Rect monitor = ToWindowRect(System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds);
            ((TextBlock)snapHint.Child).Text = longMode
                ? "Длинный скриншот: выделите область для прокрутки   ·   клик — окно под курсором   ·   Esc — отмена"
                : "Выделите область мышью   ·   клик или Enter — весь монитор   ·   Esc — отмена";
            snapHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            snapHint.Margin = new Thickness(monitor.X + (monitor.Width - snapHint.DesiredSize.Width) / 2, monitor.Y + 24, 0, 0);
            snapHint.Visibility = Visibility.Visible;

            SetChromeVisible(false);
            snapLayer.Visibility = Visibility.Visible;
            if (!IsVisible) Show();
            Activate();
            Keyboard.Focus(root);
        }

        void Snap_Down(object sender, MouseButtonEventArgs e)
        {
            selectionStart = e.GetPosition(snapLayer);
            selectionDragging = true;
            snapHint.Visibility = Visibility.Collapsed;
            snapLayer.CaptureMouse();
            UpdateSelection(selectionStart);
            e.Handled = true;
        }

        void Snap_Move(object sender, MouseEventArgs e)
        {
            if (selectionDragging) UpdateSelection(e.GetPosition(snapLayer));
        }

        void Snap_Up(object sender, MouseButtonEventArgs e)
        {
            if (!selectionDragging) return;
            selectionDragging = false;
            snapLayer.ReleaseMouseCapture();
            Point p = e.GetPosition(snapLayer);
            var area = new Rect(selectionStart, p);
            bool click = area.Width < 5 || area.Height < 5;
            // A click without dragging takes the whole monitor (long screenshot: the window under the cursor).
            if (longMode) StartScrolling(area, click, p);
            else CompleteScreenshot(click ? MonitorRectAt(p) : area);
            e.Handled = true;
        }

        void UpdateSelection(Point p)
        {
            var area = new Rect(selectionStart, p);
            selectionRect.Rect = area;
            selectionLabel.Text = string.Format("{0} × {1}",
                (int)Math.Round(area.Width * dpiScale), (int)Math.Round(area.Height * dpiScale));
            selectionLabel.Margin = new Thickness(area.X, area.Y >= 28 ? area.Y - 28 : area.Bottom + 6, 0, 0);
            selectionLabel.Visibility = area.Width >= 5 && area.Height >= 5 ? Visibility.Visible : Visibility.Collapsed;
        }

        void CompleteScreenshot(Rect area)
        {
            string savedPath = null, error = null, title = null;
            System.Drawing.Bitmap crop = null;
            var pixels = new System.Drawing.Rectangle(
                (int)Math.Round(area.X * dpiScale), (int)Math.Round(area.Y * dpiScale),
                (int)Math.Round(area.Width * dpiScale), (int)Math.Round(area.Height * dpiScale));
            try
            {
                pixels.Intersect(new System.Drawing.Rectangle(0, 0, frozenShot.Width, frozenShot.Height));
                if (pixels.Width > 0 && pixels.Height > 0)
                    crop = frozenShot.Clone(pixels, frozenShot.PixelFormat);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            EndScreenshot(); // the dialog is asked over the real screen, not the frozen one
            if (crop != null)
            {
                var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
                pixels.Offset(screen.X, screen.Y);
                try
                {
                    savedPath = SaveScreenshot(crop, pixels);
                    if (savedPath == null) title = "Скриншот не сохранён";
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                finally
                {
                    crop.Dispose();
                }
            }
            if (stripOpen && showingScreenshots) RefreshStrip();
            RaiseScreenshotFinished(savedPath, error, title);
        }

        // Copies the screenshot to the clipboard and asks where to save it.
        // Returns the saved file, or null when the dialog was cancelled (the image stays in the clipboard).
        string SaveScreenshot(System.Drawing.Bitmap image, System.Drawing.Rectangle shotOnScreen)
        {
            try
            {
                System.Windows.Forms.Clipboard.SetImage(image);
            }
            catch (ExternalException)
            {
                // Clipboard is held by another app; the file can be saved anyway.
            }
            string path = AskSavePath(shotOnScreen);
            if (path != null)
            {
                image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                lastSaveFolder = System.IO.Path.GetDirectoryName(path);
            }
            return path;
        }

        // "Save as" dialog on the monitor with the screenshot, above the overlay and other windows.
        // Opens in the folder of the previous screenshot (ScreenshotFolder the first time).
        string AskSavePath(System.Drawing.Rectangle shotOnScreen)
        {
            string folder = lastSaveFolder;
            if (folder == null || !System.IO.Directory.Exists(folder))
            {
                folder = ScreenshotFolder;
                System.IO.Directory.CreateDirectory(folder);
            }
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить скриншот",
                InitialDirectory = folder,
                FileName = System.IO.Path.GetFileName(UniqueScreenshotPath(folder)),
                Filter = "Изображение PNG (*.png)|*.png",
                DefaultExt = ".png",
                AddExtension = true,
                OverwritePrompt = true
            };
            // An invisible owner covering that monitor: the dialog opens over it, on top, with the keyboard.
            var center = new System.Drawing.Point(shotOnScreen.X + shotOnScreen.Width / 2, shotOnScreen.Y + shotOnScreen.Height / 2);
            var work = System.Windows.Forms.Screen.FromPoint(center).WorkingArea;
            var owner = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = work.X / dpiScale,
                Top = work.Y / dpiScale,
                Width = work.Width / dpiScale,
                Height = work.Height / dpiScale
            };
            bool? ok;
            try
            {
                owner.Show();
                owner.Activate();
                ok = dialog.ShowDialog(owner);
            }
            finally
            {
                owner.Close();
            }
            if (IsVisible)
            {
                Activate();
                Keyboard.Focus(root);
            }
            return ok == true ? dialog.FileName : null;
        }

        void EndScreenshot()
        {
            selecting = false;
            selectionDragging = false;
            snapLayer.ReleaseMouseCapture();
            snapLayer.Visibility = Visibility.Collapsed;
            frozenImage.Source = null;
            if (frozenShot != null)
            {
                frozenShot.Dispose();
                frozenShot = null;
            }
            SetChromeVisible(true);
            if (screenshotReturnHidden) Hide();
            else if (IsVisible)
            {
                Activate();
                Keyboard.Focus(root);
            }
        }

        void RaiseScreenshotFinished(string path, string error)
        {
            RaiseScreenshotFinished(path, error, null);
        }

        void RaiseScreenshotFinished(string path, string error, string title)
        {
            var handler = ScreenshotFinished;
            if (handler != null) handler(path, error, title);
        }

        // Long screenshot: the overlay steps aside (the wheel must reach the window below)
        // and ScrollCapture scrolls and stitches the chosen area.
        void StartScrolling(Rect area, bool click, Point point)
        {
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            var clickPixel = new System.Drawing.Point((int)(point.X * dpiScale) + screen.X, (int)(point.Y * dpiScale) + screen.Y);
            System.Drawing.Rectangle region = click
                ? System.Drawing.Rectangle.Empty
                : new System.Drawing.Rectangle(
                    (int)Math.Round(area.X * dpiScale) + screen.X, (int)Math.Round(area.Y * dpiScale) + screen.Y,
                    (int)Math.Round(area.Width * dpiScale), (int)Math.Round(area.Height * dpiScale));
            bool showAfter = !screenshotReturnHidden;
            screenshotReturnHidden = true;
            EndScreenshot();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (click) region = ScrollCapture.FindScrollableArea(clickPixel); // asked only once the overlay is gone
                if (region.Width < 50 || region.Height < 50)
                {
                    if (showAfter) ShowOverlay();
                    RaiseScreenshotFinished(null, "Не удалось определить область для прокрутки. Выделите её рамкой.");
                    return;
                }
                scrollCapture = new ScrollCapture(region, dpiScale, image => SaveScreenshot(image, region), (path, error, title) =>
                {
                    scrollCapture = null;
                    if (showAfter) ShowOverlay();
                    if (stripOpen && showingScreenshots) RefreshStrip();
                    RaiseScreenshotFinished(path, error, title);
                });
                scrollCapture.Start();
            };
            timer.Start();
        }

        static string UniqueScreenshotPath(string folder)
        {
            string name = "ScreenPen_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string path = System.IO.Path.Combine(folder, name + ".png");
            for (int i = 2; System.IO.File.Exists(path); i++)
                path = System.IO.Path.Combine(folder, name + "_" + i + ".png");
            return path;
        }

        // Whole virtual screen (all monitors), including the layered overlay with the drawings.
        // BitBlt directly: Graphics.CopyFromScreen rejects SRCCOPY | CAPTUREBLT as an invalid enum value.
        static System.Drawing.Bitmap CaptureVirtualScreen()
        {
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            var bitmap = new System.Drawing.Bitmap(screen.Width, screen.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                IntPtr target = g.GetHdc();
                IntPtr desktop = GetDC(IntPtr.Zero);
                try
                {
                    if (!BitBlt(target, 0, 0, screen.Width, screen.Height, desktop, screen.X, screen.Y, SRCCOPY | CAPTUREBLT))
                        throw new Win32Exception();
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, desktop);
                    g.ReleaseHdc(target);
                }
            }
            return bitmap;
        }

        static BitmapSource ToBitmapSource(System.Drawing.Bitmap bitmap)
        {
            IntPtr handle = bitmap.GetHbitmap();
            try
            {
                BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                    handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(handle);
            }
        }

        // Screen pixels -> window coordinates (the window covers the whole virtual screen).
        Rect ToWindowRect(System.Drawing.Rectangle pixels)
        {
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            return new Rect((pixels.X - screen.X) / dpiScale, (pixels.Y - screen.Y) / dpiScale,
                pixels.Width / dpiScale, pixels.Height / dpiScale);
        }

        Rect MonitorRectAt(Point p)
        {
            return ToWindowRect(ScreenAt(p).Bounds);
        }

        // Working area (without the taskbar) of the monitor at, or nearest to, a window point.
        Rect WorkAreaAt(Point p)
        {
            return ToWindowRect(ScreenAt(p).WorkingArea);
        }

        System.Windows.Forms.Screen ScreenAt(Point p)
        {
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            var pixel = new System.Drawing.Point((int)(p.X * dpiScale) + screen.X, (int)(p.Y * dpiScale) + screen.Y);
            return System.Windows.Forms.Screen.FromPoint(pixel);
        }

        // ---------- keyboard ----------

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (selecting)
            {
                if (e.Key == Key.Escape) EndScreenshot();
                else if (e.Key == Key.Enter && snapLayer.Visibility == Visibility.Visible)
                {
                    Point p = Mouse.GetPosition(snapLayer);
                    if (longMode) StartScrolling(Rect.Empty, true, p);
                    else CompleteScreenshot(MonitorRectAt(p));
                }
                e.Handled = true;
                return;
            }
            if (editingText != null)
            {
                if (e.Key == Key.Enter) { FinishText(); e.Handled = true; }
                else if (e.Key == Key.Escape) { CancelText(); e.Handled = true; }
                return;
            }

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            switch (e.Key == Key.System ? e.SystemKey : e.Key)
            {
                case Key.Escape:
                    if (stripOpen) ToggleStrip();
                    else HideOverlay();
                    break;
                case Key.D1: case Key.NumPad1: SelectTool(Tool.Pen); break;
                case Key.D2: case Key.NumPad2: SelectTool(Tool.Arrow); break;
                case Key.D3: case Key.NumPad3: SelectTool(Tool.Line); break;
                case Key.D4: case Key.NumPad4: SelectTool(Tool.Rect); break;
                case Key.D5: case Key.NumPad5: SelectTool(Tool.Ellipse); break;
                case Key.D6: case Key.NumPad6: SelectTool(Tool.Text); break;
                case Key.Z: if (ctrl) Undo(); break;
                case Key.Back: Undo(); break;
                case Key.Delete: ClearAll(); break;
                case Key.D: ToggleDim(); break;
                case Key.F: ToggleFlashlight(); break;
                case Key.C: TogglePalette(); break;
                case Key.S:
                    if (ctrl) TakeSnapshot();
                    else StartScreenshot();
                    break;
                case Key.G: ToggleStrip(); break;
                case Key.L: StartLongScreenshot(); break;
                case Key.Space: EnterPassThrough(); break;
                case Key.H: ToggleDrawings(); break;
                case Key.OemOpenBrackets: ChangeSize(-1); break;
                case Key.OemCloseBrackets: ChangeSize(1); break;
                case Key.LeftShift:
                case Key.RightShift:
                    if (!flashlightHeld)
                    {
                        flashlightHeld = true;
                        UpdateBackdrop();
                    }
                    break;
                default: return;
            }
            e.Handled = true;
        }

        void OnKeyUp(object sender, KeyEventArgs e)
        {
            if ((e.Key == Key.LeftShift || e.Key == Key.RightShift) && flashlightHeld)
            {
                flashlightHeld = false;
                UpdateBackdrop();
            }
            if (e.Key == Key.Space) ExitPassThrough();
        }
    }
}
