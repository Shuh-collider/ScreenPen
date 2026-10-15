using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using ScreenPen;

// Checks how ScreenPen reads a drawn shape back into its points and rebuilds it, including an arrow
// restored from a snapshot (XAML round trip), and that "clear all" can be undone.
static class ShapeTest
{
    static readonly Type T = typeof(OverlayWindow);
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Static;
    static int failures;

    [STAThread]
    static int Main()
    {
        var a = new Point(40, 30);
        var b = new Point(220, 140);
        var arrow = (Geometry)T.GetMethod("ArrowGeometry", Any).Invoke(null, new object[] { a, b, 6.0 });

        Check("line", Shape(new LineGeometry(a, b)), "Line", a, b);
        Check("arrow", Shape(arrow), "Arrow", a, b);
        Check("arrow from snapshot", RoundTrip(Shape(arrow)), "Arrow", a, b);
        Check("line from snapshot", RoundTrip(Shape(new LineGeometry(a, b))), "Line", a, b);
        Check("rectangle", Shape(new RectangleGeometry(new Rect(b, a), 4, 4)), "Rect", a, b);
        Check("rectangle from snapshot", RoundTrip(Shape(new RectangleGeometry(new Rect(a, b), 4, 4))), "Rect", a, b);
        Check("oval", Shape(new EllipseGeometry(new Rect(a, b))), "Ellipse", a, b);
        Check("oval from snapshot", RoundTrip(Shape(new EllipseGeometry(new Rect(a, b)))), "Ellipse", a, b);

        // Rebuilding a moved arrow keeps it an arrow with the new ends.
        object kindArrow = Enum.Parse(T.GetNestedType("ShapeKind", BindingFlags.NonPublic), "Arrow");
        var moved = (Geometry)T.GetMethod("BuildShape", Any).Invoke(null, new object[] { kindArrow, a + new Vector(10, 5), b + new Vector(10, 5), 6.0 });
        Check("moved arrow", Shape(moved), "Arrow", a + new Vector(10, 5), b + new Vector(10, 5));

        // Corner points of a rectangle and grabbing near its outline.
        var points = (System.Collections.Generic.List<Point>)T.GetMethod("ShapePoints", Any).Invoke(null, new object[] { Shape(new RectangleGeometry(new Rect(a, b), 4, 4)) });
        Report("rectangle corners", points.Count == 4 && points[0] == a && points[2] == b && points[1] == new Point(b.X, a.Y),
            string.Join(" ", points));
        var outline = new RectangleGeometry(new Rect(a, b), 4, 4);
        Report("grab 6 px from the outline", outline.StrokeContains(new Pen(Brushes.Black, 6 + 16), new Point(a.X + 50, a.Y - 6)), "");
        Report("no grab in the middle", !outline.StrokeContains(new Pen(Brushes.Black, 6 + 16), new Point(130, 85)), "");

        CheckClearAllUndo(a, b);
        CheckToolbarClamp();
        CheckOffsetDrawings(a, b);

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures;
    }

    // "Clear all" is one undo step that brings back the drawing and its own undo history.
    static void CheckClearAllUndo(Point a, Point b)
    {
        const BindingFlags Field = BindingFlags.NonPublic | BindingFlags.Instance;
        var window = new OverlayWindow();
        var ink = (InkCanvas)T.GetField("ink", Field).GetValue(window);
        var history = (List<object>)T.GetField("history", Field).GetValue(window);
        var stroke = new Stroke(new StylusPointCollection(new[] { new StylusPoint(10, 10), new StylusPoint(50, 60) }));
        ink.Strokes.Add(stroke);
        history.Add(stroke);
        Path line = Shape(new LineGeometry(a, b));
        ink.Children.Add(line);
        history.Add(line);

        window.ClearAll();
        Report("clear all", ink.Strokes.Count == 0 && ink.Children.Count == 0, "history " + history.Count);
        window.Undo();
        Report("undo clear all", ink.Strokes.Count == 1 && ink.Children.Count == 1 && history.Count == 2,
            string.Format("strokes {0}, shapes {1}, history {2}", ink.Strokes.Count, ink.Children.Count, history.Count));
        window.Undo(); // the line was drawn last
        Report("older undo still works", ink.Strokes.Count == 1 && ink.Children.Count == 0, "");
    }

    // A toolbar dropped partly off the monitor slides back fully onto it.
    static void CheckToolbarClamp()
    {
        MethodInfo clamp = T.GetMethod("ClampInto", Any);
        var area = new Rect(0, 0, 1000, 800);
        var offRight = (Point)clamp.Invoke(null, new object[] { area, new Rect(950, 700, 100, 200) });
        Report("toolbar off right/bottom", offRight == new Point(900, 600), offRight.ToString());
        var offLeft = (Point)clamp.Invoke(null, new object[] { area, new Rect(-300, -50, 100, 200) });
        Report("toolbar off left/top", offLeft == new Point(0, 0), offLeft.ToString());
        var inside = (Point)clamp.Invoke(null, new object[] { area, new Rect(400, 300, 100, 200) });
        Report("toolbar inside stays", inside == new Point(400, 300), inside.ToString());
    }

    // When the screens change, every drawing moves by the same offset and keeps its shape.
    static void CheckOffsetDrawings(Point a, Point b)
    {
        const BindingFlags Field = BindingFlags.NonPublic | BindingFlags.Instance;
        var window = new OverlayWindow();
        var ink = (InkCanvas)T.GetField("ink", Field).GetValue(window);
        var arrow = Shape((Geometry)T.GetMethod("ArrowGeometry", Any).Invoke(null, new object[] { a, b, 6.0 }));
        var box = Shape(new RectangleGeometry(new Rect(a, b), 4, 4));
        var text = new TextBlock { Text = "Привет" };
        InkCanvas.SetLeft(text, 300);
        InkCanvas.SetTop(text, 200);
        var stroke = new Stroke(new StylusPointCollection(new[] { new StylusPoint(10, 10), new StylusPoint(50, 60) }));
        ink.Children.Add(arrow);
        ink.Children.Add(box);
        ink.Children.Add(text);
        ink.Strokes.Add(stroke);

        var shift = new Vector(1920, -50);
        T.GetMethod("OffsetDrawings", Field).Invoke(window, new object[] { shift });

        Check("arrow after screens change", arrow, "Arrow", a + shift, b + shift);
        Check("rectangle after screens change", box, "Rect", a + shift, b + shift);
        Report("text after screens change", InkCanvas.GetLeft(text) == 2220 && InkCanvas.GetTop(text) == 150,
            InkCanvas.GetLeft(text) + ";" + InkCanvas.GetTop(text));
        StylusPoint first = stroke.StylusPoints[0];
        Report("brush stroke after screens change", Math.Abs(first.X - 1930) < 0.01 && Math.Abs(first.Y + 40) < 0.01,
            first.X + ";" + first.Y);
    }

    static Path Shape(Geometry g)
    {
        return new Path { Data = g, Stroke = Brushes.Red, StrokeThickness = 6 };
    }

    // What a snapshot does: save to XAML and load back.
    static Path RoundTrip(Path p)
    {
        return (Path)XamlReader.Parse(XamlWriter.Save(p));
    }

    static void Check(string name, Path path, string kind, Point a, Point b)
    {
        object[] args = { path, null, null, null };
        bool ok = (bool)T.GetMethod("ReadShape", Any).Invoke(null, args);
        string gotKind = args[1].ToString();
        Point ga = (Point)args[2], gb = (Point)args[3];
        bool same = ok && gotKind == kind && Near(ga, a) && Near(gb, b);
        Report(name, same, string.Format("{0} ({1}): {2} -> {3}", gotKind, path.Data.GetType().Name, ga, gb));
    }

    static bool Near(Point p, Point q)
    {
        return (p - q).Length < 0.01;
    }

    static void Report(string name, bool ok, string detail)
    {
        if (!ok) failures++;
        Console.WriteLine("{0,-28} {1}  {2}", name, ok ? "OK  " : "FAIL", detail);
    }
}
