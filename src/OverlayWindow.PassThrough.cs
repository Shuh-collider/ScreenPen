using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ScreenPen
{
    // Hold Space while drawing: the normal mouse cursor. A drawing under the cursor can be grabbed and
    // moved; arrows and lines show points at their ends, rectangles and ovals at their corners, to reshape
    // them. A click anywhere else goes to the app below (the drawing stays on screen). Release Space to draw.
    //
    // The overlay is a layered window: fully transparent pixels let the mouse through. In this mode the
    // backdrop becomes fully transparent and every drawing gets an almost invisible wider "halo", so even
    // a thin line is easy to grab.
    public partial class OverlayWindow
    {
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

        const int HOTKEY_SPACE_ID = 0x5153;
        const uint VK_SPACE = 0x20;
        const double GrabTolerance = 8; // how far from a line it can still be grabbed
        const double HandleRadius = 6;

        static readonly Brush HaloBrush = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
        static readonly Brush HandleFill = Frozen(new SolidColorBrush(Colors.White));
        static readonly Brush HandleStroke = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));

        enum ShapeKind { Line, Arrow, Rect, Ellipse }

        // An undo step for a move or reshape.
        class EditRecord
        {
            public readonly Action Undo;

            public EditRecord(Action undo)
            {
                Undo = undo;
            }
        }

        readonly DispatcherTimer spaceWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        readonly Canvas editLayer = new Canvas { IsHitTestVisible = false }; // halos and points
        bool passThrough, editHandlersAdded;

        object dragTarget; // Path (shape), TextBlock or Stroke being dragged
        int dragHandle = -1; // index of the point being dragged, -1 = move the whole drawing
        Point dragStart, dragLast;
        Action undoDrag;

        void EnterPassThrough()
        {
            if (passThrough || hwnd == IntPtr.Zero) return;
            passThrough = true;
            FinishText();
            CancelShape();
            HidePalette();
            if (!editHandlersAdded)
            {
                editHandlersAdded = true;
                root.Children.Insert(root.Children.IndexOf(ink) + 1, editLayer);
                root.PreviewMouseLeftButtonDown += Edit_MouseDown;
                root.PreviewMouseMove += Edit_MouseMove;
                root.PreviewMouseLeftButtonUp += Edit_MouseUp;
                spaceWatch.Tick += SpaceWatch_Tick;
            }
            ink.EditingMode = InkCanvasEditingMode.None;
            UpdateBackdrop(); // fully transparent: clicks outside the drawings go through
            RefreshEditLayer();
            // While Space is held, its auto-repeat goes to us instead of the app the user clicks
            // (a browser would scroll a page per repeat).
            RegisterHotKey(hwnd, HOTKEY_SPACE_ID, 0, VK_SPACE);
            toolbar.Opacity = 0.45;
            // After a click on another app the key-up goes there, so watch the key itself.
            spaceWatch.Start();
        }

        void ExitPassThrough()
        {
            if (!passThrough) return;
            EndDrag();
            passThrough = false;
            spaceWatch.Stop();
            UnregisterHotKey(hwnd, HOTKEY_SPACE_ID);
            editLayer.Children.Clear();
            Mouse.OverrideCursor = null;
            ink.EditingMode = tool == Tool.Pen ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
            UpdateBackdrop();
            toolbar.Opacity = 1;
            if (IsVisible)
            {
                Activate();
                Keyboard.Focus(root);
            }
        }

        void SpaceWatch_Tick(object sender, EventArgs e)
        {
            if ((GetAsyncKeyState((int)VK_SPACE) & 0x8000) == 0) ExitPassThrough();
        }

        // ---------- halos and points ----------

        void RefreshEditLayer()
        {
            editLayer.Children.Clear();
            if (!passThrough || drawingsHidden) return;
            foreach (Stroke stroke in ink.Strokes)
            {
                editLayer.Children.Add(new Path
                {
                    Data = stroke.GetGeometry(),
                    Fill = HaloBrush,
                    Stroke = HaloBrush,
                    StrokeThickness = 2 * GrabTolerance
                });
            }
            foreach (UIElement child in ink.Children)
            {
                var path = child as Path;
                if (path != null)
                {
                    editLayer.Children.Add(new Path
                    {
                        Data = path.Data,
                        Stroke = HaloBrush,
                        StrokeThickness = path.StrokeThickness + 2 * GrabTolerance
                    });
                    foreach (Point point in ShapePoints(path)) editLayer.Children.Add(MakeHandle(point));
                    continue;
                }
                var text = child as FrameworkElement;
                if (text == null) continue;
                var halo = new Rectangle { Width = text.ActualWidth, Height = text.ActualHeight, Fill = HaloBrush };
                Canvas.SetLeft(halo, InkCanvas.GetLeft(text));
                Canvas.SetTop(halo, InkCanvas.GetTop(text));
                editLayer.Children.Add(halo);
            }
        }

        static Ellipse MakeHandle(Point center)
        {
            var handle = new Ellipse
            {
                Width = 2 * HandleRadius,
                Height = 2 * HandleRadius,
                Fill = HandleFill,
                Stroke = HandleStroke,
                StrokeThickness = 2
            };
            Canvas.SetLeft(handle, center.X - HandleRadius);
            Canvas.SetTop(handle, center.Y - HandleRadius);
            return handle;
        }

        // Ends of an arrow or line; corners of a rectangle or oval (top-left, top-right, bottom-right, bottom-left).
        static List<Point> ShapePoints(Path path)
        {
            var points = new List<Point>();
            ShapeKind kind;
            Point a, b;
            if (!ReadShape(path, out kind, out a, out b)) return points;
            if (kind == ShapeKind.Line || kind == ShapeKind.Arrow)
            {
                points.Add(a);
                points.Add(b);
            }
            else
            {
                for (int i = 0; i < 4; i++) points.Add(Corner(a, b, i));
            }
            return points;
        }

        static Point Corner(Point a, Point b, int index)
        {
            double left = Math.Min(a.X, b.X), right = Math.Max(a.X, b.X);
            double top = Math.Min(a.Y, b.Y), bottom = Math.Max(a.Y, b.Y);
            switch (index)
            {
                case 1: return new Point(right, top);
                case 2: return new Point(right, bottom);
                case 3: return new Point(left, bottom);
                default: return new Point(left, top);
            }
        }

        // Reads what a shape is and its two defining points (also for shapes restored from a snapshot,
        // where an arrow comes back as a StreamGeometry).
        static bool ReadShape(Path path, out ShapeKind kind, out Point a, out Point b)
        {
            kind = ShapeKind.Line;
            a = b = new Point();
            var line = path.Data as LineGeometry;
            if (line != null)
            {
                a = line.StartPoint;
                b = line.EndPoint;
                return true;
            }
            var rect = path.Data as RectangleGeometry;
            if (rect != null)
            {
                kind = ShapeKind.Rect;
                a = rect.Rect.TopLeft;
                b = rect.Rect.BottomRight;
                return true;
            }
            var ellipse = path.Data as EllipseGeometry;
            if (ellipse != null)
            {
                kind = ShapeKind.Ellipse;
                a = new Point(ellipse.Center.X - ellipse.RadiusX, ellipse.Center.Y - ellipse.RadiusY);
                b = new Point(ellipse.Center.X + ellipse.RadiusX, ellipse.Center.Y + ellipse.RadiusY);
                return true;
            }
            if (path.Data == null) return false;
            PathGeometry geometry = PathGeometry.CreateFromGeometry(path.Data);
            if (geometry.Figures.Count == 0 || geometry.Figures[0].Segments.Count == 0) return false;
            PathFigure shaft = geometry.Figures[0];
            var segment = shaft.Segments[0];
            var lineSegment = segment as LineSegment;
            var polyLine = segment as PolyLineSegment;
            if (lineSegment != null) b = lineSegment.Point;
            else if (polyLine != null && polyLine.Points.Count > 0) b = polyLine.Points[polyLine.Points.Count - 1];
            else return false;
            a = shaft.StartPoint;
            kind = geometry.Figures.Count > 1 ? ShapeKind.Arrow : ShapeKind.Line;
            return true;
        }

        static Geometry BuildShape(ShapeKind kind, Point a, Point b, double thickness)
        {
            switch (kind)
            {
                case ShapeKind.Arrow: return ArrowGeometry(a, b, thickness);
                case ShapeKind.Rect: return new RectangleGeometry(new Rect(a, b), 4, 4);
                case ShapeKind.Ellipse: return new EllipseGeometry(new Rect(a, b));
                default: return new LineGeometry(a, b);
            }
        }

        // Moves every drawing by the same offset (used when window coordinates change but the screen does not).
        void OffsetDrawings(Vector shift)
        {
            var move = Matrix.Identity;
            move.Translate(shift.X, shift.Y);
            foreach (Stroke stroke in ink.Strokes) stroke.Transform(move, false);
            foreach (UIElement child in ink.Children)
            {
                var path = child as Path;
                ShapeKind kind;
                Point a, b;
                if (path != null && ReadShape(path, out kind, out a, out b))
                {
                    path.Data = BuildShape(kind, a + shift, b + shift, path.StrokeThickness);
                    continue;
                }
                InkCanvas.SetLeft(child, InkCanvas.GetLeft(child) + shift.X);
                InkCanvas.SetTop(child, InkCanvas.GetTop(child) + shift.Y);
            }
        }

        // The drawing under the point, newest first; handle = which of a shape's points, or -1.
        object DrawingAt(Point p, out int handle)
        {
            handle = -1;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                var shape = history[i] as Path;
                if (shape == null || !ink.Children.Contains(shape)) continue;
                List<Point> points = ShapePoints(shape);
                for (int h = 0; h < points.Count; h++)
                {
                    if ((points[h] - p).Length <= HandleRadius + 3)
                    {
                        handle = h;
                        return shape;
                    }
                }
            }
            for (int i = history.Count - 1; i >= 0; i--)
            {
                object item = history[i];
                var stroke = item as Stroke;
                if (stroke != null)
                {
                    if (ink.Strokes.Contains(stroke) && stroke.HitTest(p, 2 * GrabTolerance)) return stroke;
                    continue;
                }
                var path = item as Path;
                if (path != null)
                {
                    var pen = new Pen(Brushes.Black, path.StrokeThickness + 2 * GrabTolerance);
                    if (ink.Children.Contains(path) && path.Data.StrokeContains(pen, p)) return path;
                    continue;
                }
                var text = item as FrameworkElement;
                if (text != null && ink.Children.Contains(text)
                    && new Rect(InkCanvas.GetLeft(text), InkCanvas.GetTop(text), text.ActualWidth, text.ActualHeight).Contains(p))
                    return text;
            }
            return null;
        }

        // ---------- dragging ----------

        void Edit_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!passThrough || IsInside(e.OriginalSource as DependencyObject, uiLayer)) return; // toolbar, palette, strip
            Point p = e.GetPosition(ink);
            int handle;
            object target = DrawingAt(p, out handle);
            if (target == null) return;
            StartDrag(target, handle, p);
            root.CaptureMouse();
            e.Handled = true;
        }

        void Edit_MouseMove(object sender, MouseEventArgs e)
        {
            if (!passThrough) return;
            Point p = e.GetPosition(ink);
            if (dragTarget != null)
            {
                DragTo(p);
                return;
            }
            int handle = -1;
            object target = IsInside(e.OriginalSource as DependencyObject, uiLayer) ? null : DrawingAt(p, out handle);
            Mouse.OverrideCursor = target == null ? null : (handle >= 0 ? Cursors.Cross : Cursors.SizeAll);
        }

        void Edit_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (dragTarget == null) return;
            EndDrag();
            e.Handled = true;
        }

        void StartDrag(object target, int handle, Point p)
        {
            dragTarget = target;
            dragHandle = handle;
            dragStart = dragLast = p;
            undoDrag = null;
            var path = target as Path;
            var text = target as FrameworkElement;
            if (path != null)
            {
                Geometry before = path.Data;
                undoDrag = () => path.Data = before;
            }
            else if (text != null)
            {
                double left = InkCanvas.GetLeft(text), top = InkCanvas.GetTop(text);
                undoDrag = () =>
                {
                    InkCanvas.SetLeft(text, left);
                    InkCanvas.SetTop(text, top);
                };
            }
            // A stroke's undo is made in EndDrag from the total offset.
            editLayer.Visibility = Visibility.Hidden; // its halos and points would lag behind the drag
        }

        void DragTo(Point p)
        {
            Vector step = p - dragLast;
            dragLast = p;
            var stroke = dragTarget as Stroke;
            if (stroke != null)
            {
                var move = Matrix.Identity;
                move.Translate(step.X, step.Y);
                stroke.Transform(move, false);
                return;
            }
            var path = dragTarget as Path;
            if (path != null)
            {
                ShapeKind kind;
                Point a, b;
                if (!ReadShape(path, out kind, out a, out b)) return;
                bool box = kind == ShapeKind.Rect || kind == ShapeKind.Ellipse;
                if (dragHandle < 0)
                {
                    a += step;
                    b += step;
                }
                else if (!box)
                {
                    if (dragHandle == 0) a = p;
                    else b = p;
                }
                else
                {
                    a = Corner(a, b, (dragHandle + 2) % 4); // the opposite corner stays in place
                    b = p;
                }
                path.Data = BuildShape(kind, a, b, path.StrokeThickness);
                if (box && dragHandle >= 0) dragHandle = NearestCorner(a, b, p); // dragged past the opposite corner
                return;
            }
            var text = dragTarget as FrameworkElement;
            if (text != null)
            {
                InkCanvas.SetLeft(text, InkCanvas.GetLeft(text) + step.X);
                InkCanvas.SetTop(text, InkCanvas.GetTop(text) + step.Y);
            }
        }

        static int NearestCorner(Point a, Point b, Point p)
        {
            int nearest = 0;
            for (int i = 1; i < 4; i++)
                if ((Corner(a, b, i) - p).Length < (Corner(a, b, nearest) - p).Length) nearest = i;
            return nearest;
        }

        void EndDrag()
        {
            if (dragTarget == null) return;
            root.ReleaseMouseCapture();
            Vector total = dragLast - dragStart;
            var stroke = dragTarget as Stroke;
            if (stroke != null)
            {
                undoDrag = () =>
                {
                    var back = Matrix.Identity;
                    back.Translate(-total.X, -total.Y);
                    stroke.Transform(back, false);
                };
            }
            if (undoDrag != null && total.Length > 0.5) history.Add(new EditRecord(undoDrag));
            dragTarget = null;
            dragHandle = -1;
            undoDrag = null;
            editLayer.Visibility = Visibility.Visible;
            RefreshEditLayer();
        }
    }
}
