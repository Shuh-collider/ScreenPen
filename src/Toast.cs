using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace ScreenPen
{
    // Small self-closing notification in the bottom-right corner.
    // Used instead of tray balloons, which are invisible when Windows notifications are off.
    class Toast : Window
    {
        static Toast current;

        public static void Notify(string title, string text, Action onClick)
        {
            Dismiss();
            current = new Toast(title, text, onClick);
            current.Show();
        }

        // Closes the visible toast, if any. Returns true when one was open.
        public static bool Dismiss()
        {
            if (current == null) return false;
            current.Close();
            return true;
        }

        Toast(string title, string text, Action onClick)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            });
            panel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
                FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320
            });
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(240, 32, 33, 36)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(12),
                Child = panel,
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.5 },
                Cursor = onClick != null ? Cursors.Hand : Cursors.Arrow
            };
            Content = card;

            // Bottom-right corner of the primary monitor's work area.
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Rect area = SystemParameters.WorkArea;
            Left = area.Right - card.DesiredSize.Width - 8;
            Top = area.Bottom - card.DesiredSize.Height - 8;

            MouseLeftButtonUp += (s, e) =>
            {
                Close();
                if (onClick != null) onClick();
            };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                Close();
            };
            timer.Start();
            Closed += (s, e) =>
            {
                timer.Stop();
                if (current == this) current = null;
            };
        }
    }
}
