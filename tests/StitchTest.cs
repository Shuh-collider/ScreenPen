using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using ScreenPen;

// Offline test of ScrollCapture stitching: cut a synthetic tall page into overlapping "screen" frames
// (sticky header/footer, a sidebar whose highlight changes, a moving scrollbar thumb), stitch them back
// and compare the content columns with the original page.
//  - "wheel N": scrolls like the app does, N pixels per notch, notches chosen by ScrollCapture itself;
//  - "random":  random steps of 80..300 px (stress test).
// On a live page random steps are reported but not counted: with unpredictable steps a fully changed
// overlap cannot be matched, while the app's wheel steps are regular.
//
// Usage: StitchTest.exe [folder for failed results]   STITCH_SEEDS=1-40 for more pages
//        StitchTest.exe debug <seed> <scroll> [big]    per-band view of one step (STITCH_LIVE=1, STITCH_STATIC=top,footer)
static class StitchTest
{
    const int W = 900, H = 700, Header = 56, Footer = 36, Sidebar = 180, Scrollbar = 16;
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    // Pictures of the current page. In a "live" run they behave like a real feed: every third one is lazy
    // (a gray placeholder until its top reaches the upper half of the screen), every third one is a GIF
    // (a different picture in every frame).
    static readonly List<Rectangle> Pictures = new List<Rectangle>();
    static bool Live;

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "debug")
        {
            Live = Environment.GetEnvironmentVariable("STITCH_LIVE") == "1";
            StitchDebug.Run(int.Parse(args[1]), int.Parse(args[2]), args.Length > 3, MakePage, MakeFrame, W, H);
            return 0;
        }
        int failures = 0, cases = 0, stress = 0, stressPassed = 0;
        foreach (bool live in new[] { false, true })
        {
            Live = live;
            Console.WriteLine(live ? "--- live page: lazy pictures and GIFs ---" : "--- static page ---");
            foreach (int seed in Seeds())
            {
                foreach (int wheel in new[] { 100, 57, 0 })
                {
                    bool ok = RunCase(seed, wheel, args.Length > 0 ? args[0] : null, !(live && wheel == 0));
                    if (live && wheel == 0)
                    {
                        stress++;
                        if (ok) stressPassed++;
                        continue;
                    }
                    cases++;
                    if (!ok) failures++;
                }
            }
        }
        Console.WriteLine("Stress (live page, random steps, not counted): {0} of {1} passed", stressPassed, stress);
        Console.WriteLine(failures == 0 ? "ALL " + cases + " PASSED" : failures + " OF " + cases + " FAILED");
        return failures;
    }

    static IEnumerable<int> Seeds()
    {
        string range = Environment.GetEnvironmentVariable("STITCH_SEEDS");
        int from = 1, to = 8;
        if (range != null) { from = int.Parse(range.Split('-')[0]); to = int.Parse(range.Split('-')[1]); }
        for (int s = from; s <= to; s++) yield return s;
    }

    static bool RunCase(int seed, int wheel, string saveDir, bool counted)
    {
        var rnd = new Random(seed);
        int pageHeight = 5200, contentHeight = H - Header - Footer, last = pageHeight - contentHeight;
        Bitmap page = MakePage(rnd, pageHeight);

        var capture = new ScrollCapture(new Rectangle(0, 0, W, H), 1.0, null, null);
        var t = typeof(ScrollCapture);
        capture.Begin(ScrollCapture.Frame.FromBitmap(MakeFrame(page, 0, 0, pageHeight)));
        string log = "";
        int s = 0, frames = 1, stitched = 0;
        bool endSeen = false;
        while (frames < 200)
        {
            int notches = (int)t.GetField("notches", Private).GetValue(capture);
            t.GetField("lastNotches", Private).SetValue(capture, notches);
            // The app's first step is always one notch; random mode keeps that and then takes any step.
            int step = wheel > 0 ? notches * wheel : (frames == 1 ? rnd.Next(57, 130) : rnd.Next(80, 300));
            int next = Math.Min(last, s + step);
            int r = capture.Append(ScrollCapture.Frame.FromBitmap(MakeFrame(page, next, frames, pageHeight)));
            frames++;
            int total = (int)t.GetField("totalHeight", Private).GetValue(capture);
            int footer = (int)t.GetField("footer", Private).GetValue(capture);
            if (Environment.GetEnvironmentVariable("STITCH_TRACE") == seed + "/" + wheel)
                Console.WriteLine("  step {0}: {1} -> {2} (notches {3}) result {4}, total {5}, header {6}, footer {7}, px/notch {8:F1}",
                    frames - 1, s, next, notches, r, total, t.GetField("header", Private).GetValue(capture), footer,
                    t.GetField("pixelsPerNotch", Private).GetValue(capture));
            if (r == 1)
            {
                int got = total - (stitched == 0 ? H - footer : stitched);
                if (got != next - s) log += string.Format("[step {0}: got {1}, actual {2}] ", frames - 1, got, next - s);
                stitched = total;
                s = next;
            }
            else if (r == 0 && next == s) { endSeen = true; break; } // at the end nothing moves: done
            else { log += string.Format("[step {0}: returned {1}, actual {2}] ", frames - 1, r, next - s); break; }
        }

        using (Bitmap result = capture.Compose())
        {
            int expectedHeight = last + H, badRows = 0;
            // In a live run pictures may legitimately show a placeholder or another GIF frame: compare the rest.
            var pictureRow = new bool[pageHeight];
            if (Live)
                foreach (var p in Pictures)
                    for (int y = p.Top; y < p.Bottom && y < pageHeight; y++) pictureRow[y] = true;
            if (result.Height == expectedHeight)
            {
                for (int r = 0; r < pageHeight && Header + r < result.Height - Footer; r++)
                {
                    if (pictureRow[r]) continue;
                    for (int x = Sidebar; x < W - Scrollbar; x += 3)
                        if (result.GetPixel(x, Header + r).ToArgb() != page.GetPixel(x, r).ToArgb()) { badRows++; break; }
                }
            }
            bool ok = endSeen && result.Height == expectedHeight && badRows == 0;
            Console.WriteLine("seed {0} {1,-9}: frames {2,3}, height {3} (expected {4}), bad rows {5} {6} {7}",
                seed, wheel > 0 ? "wheel " + wheel : "random", frames, result.Height, expectedHeight, badRows,
                ok ? "OK" : counted ? "FAIL" : "stopped (stress, not counted)", log.Trim());
            if (saveDir != null && !ok)
                result.Save(System.IO.Path.Combine(saveDir, string.Format("stitched_{0}_{1}.png", seed, wheel)));
            return ok;
        }
    }

    // A tall page: paragraphs of "text", colored pictures, horizontal rules and blank gaps.
    public static Bitmap MakePage(Random rnd, int height)
    {
        var page = new Bitmap(W, height);
        Pictures.Clear();
        using (var g = Graphics.FromImage(page))
        {
            g.Clear(Color.White);
            int y = 10;
            while (y < height - 40)
            {
                int kind = rnd.Next(10);
                if (kind < 6)
                {
                    int lines = rnd.Next(2, 8);
                    for (int l = 0; l < lines && y < height - 30; l++, y += 22)
                    {
                        int x = Sidebar + 20;
                        while (x < W - Scrollbar - 60)
                        {
                            int word = rnd.Next(12, 70);
                            using (var b = new SolidBrush(Color.FromArgb(40 + rnd.Next(60), 40 + rnd.Next(60), 50 + rnd.Next(60))))
                                g.FillRectangle(b, x, y, word, 12);
                            x += word + rnd.Next(5, 12);
                        }
                    }
                    y += 14;
                }
                else if (kind < 8)
                {
                    int h = rnd.Next(80, 220);
                    using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(Sidebar + 20, y, 500, h),
                        Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)), Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)), 30f))
                        g.FillRectangle(b, Sidebar + 20, y, 500, h);
                    Pictures.Add(new Rectangle(Sidebar + 20, y, 500, h));
                    y += h + 16;
                }
                else if (kind == 8)
                {
                    g.FillRectangle(Brushes.LightGray, Sidebar + 20, y, W - Sidebar - 60, 2);
                    y += 20;
                }
                else y += rnd.Next(60, 260); // blank gap
            }
        }
        return page;
    }

    // What the screen shows when the page is scrolled by `scroll` pixels.
    public static Bitmap MakeFrame(Bitmap page, int scroll, int index, int pageHeight)
    {
        int contentHeight = H - Header - Footer;
        var frame = new Bitmap(W, H);
        using (var g = Graphics.FromImage(frame))
        {
            g.DrawImage(page, new Rectangle(0, Header, W, contentHeight), new Rectangle(0, scroll, W, contentHeight), GraphicsUnit.Pixel);
            if (Live)
            {
                g.SetClip(new Rectangle(0, Header, W, contentHeight));
                for (int i = 0; i < Pictures.Count; i++)
                {
                    Rectangle p = Pictures[i];
                    var onScreen = new Rectangle(p.X, p.Y - scroll + Header, p.Width, p.Height);
                    if (i % 3 == 0 && p.Top > scroll + contentHeight / 2)
                        g.FillRectangle(Brushes.Gainsboro, onScreen); // lazy picture: not loaded yet
                    else if (i % 3 == 1)
                        using (var b = new SolidBrush(Color.FromArgb((index * 53) % 256, (index * 97) % 256, (index * 31) % 256)))
                            g.FillRectangle(b, onScreen); // GIF: another frame
                }
                g.ResetClip();
            }
            g.FillRectangle(new SolidBrush(Color.FromArgb(30, 60, 120)), 0, 0, W, Header);
            g.FillRectangle(Brushes.Gold, 20, 16, 120, 24);
            g.FillRectangle(new SolidBrush(Color.FromArgb(50, 50, 50)), 0, H - Footer, W, Footer);
            g.FillRectangle(Brushes.White, 20, H - Footer + 12, 200, 10);
            g.FillRectangle(new SolidBrush(Color.FromArgb(240, 240, 245)), 0, Header, Sidebar, contentHeight);
            for (int i = 0; i < 12; i++)
            {
                if (i == index % 12) g.FillRectangle(new SolidBrush(Color.FromArgb(200, 220, 255)), 0, Header + 20 + i * 40, Sidebar, 32);
                g.FillRectangle(Brushes.DimGray, 16, Header + 32 + i * 40, 90 + (i * 37) % 60, 10);
            }
            g.FillRectangle(Brushes.Gainsboro, W - Scrollbar, Header, Scrollbar, contentHeight);
            int thumb = contentHeight * contentHeight / pageHeight;
            int thumbY = Header + (contentHeight - thumb) * scroll / (pageHeight - contentHeight);
            g.FillRectangle(Brushes.Gray, W - Scrollbar + 3, thumbY, Scrollbar - 6, thumb);
        }
        return frame;
    }
}

