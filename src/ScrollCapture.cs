using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;

namespace ScreenPen
{
    // Long (scrolling) screenshot. Scrolls the area with the mouse wheel, captures it after each step
    // and stitches the frames by finding how far the content moved between them.
    // Stops at the end of the content or on Esc; bumping the mouse does not disturb it.
    class ScrollCapture
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr hdcDest, int x, int y, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, int rop);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        const uint INPUT_MOUSE = 0, MOUSEEVENTF_WHEEL = 0x0800;
        const int WHEEL_STEP = 120;
        const uint GA_ROOT = 2;
        const int SRCCOPY = 0x00CC0020;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        const uint WDA_EXCLUDEFROMCAPTURE = 0x11; // Windows 10 2004+: our windows never appear in the capture
        const int WM_HOTKEY = 0x0312, HOTKEY_ESC_ID = 0x5160;
        const uint MOD_NOREPEAT = 0x4000, VK_ESCAPE = 0x1B;

        const int SettleMinMs = 120;   // wait after a wheel step before the first check
        const int SettlePollMs = 60;   // then capture this often until the picture stops changing
        const int NoMoveMs = 700;      // give a slow app this long to start moving
        const int MaxNoMoveAttempts = 4; // when nothing moves, retry 3 times (a feed may still be loading)
        const int RetryWaitMs = 600;   // waits 600, 1200, 1800 ms before the retries
        const int SettleMaxMs = 1500;  // animated pages never settle; take what we have
        const int MinOverlapRows = 8;  // rows two frames must share
        const int MinBandRows = 2;     // distinctive rows a band needs in the overlap to vote
        const int MinVoteRows = 6;     // matched rows a shift needs across all bands
        const int Bands = 8;           // vertical bands vote separately, so static sidebars do not break the match
        const int MaxPixels = 60000000;

        enum Phase { First, Settling, Retry, Recapture }

        // One captured picture of the area plus its pixels for comparison.
        internal class Frame : IDisposable
        {
            public Drawing.Bitmap Bitmap;
            public int[] Pixels;
            public int Width, Height;

            public static Frame FromBitmap(Drawing.Bitmap bitmap)
            {
                var pixels = new int[bitmap.Width * bitmap.Height];
                var data = bitmap.LockBits(new Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppRgb);
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); // 32 bpp: stride == width * 4
                bitmap.UnlockBits(data);
                for (int i = 0; i < pixels.Length; i++) pixels[i] &= 0x00FFFFFF; // the alpha byte is undefined
                return new Frame { Bitmap = bitmap, Pixels = pixels, Width = bitmap.Width, Height = bitmap.Height };
            }

            public void Dispose()
            {
                if (Bitmap != null) Bitmap.Dispose();
                Bitmap = null;
            }
        }

        readonly Drawing.Rectangle region; // screen pixels
        readonly double dpiScale;
        readonly Func<Drawing.Bitmap, string> save; // saves the result; returns the file, or null if the user cancelled
        readonly Action<string, string, string> finished; // saved path, error, notification title
        readonly DispatcherTimer timer = new DispatcherTimer();
        readonly List<Drawing.Bitmap> pieces = new List<Drawing.Bitmap>();
        readonly int scrollbarMargin; // the scrollbar thumb moves differently from the content; ignore it
        readonly int maxHeight;

        Window frameWindow, panelWindow;
        TextBlock statusText;
        IntPtr panelHandle;
        POINT savedCursor, wheelPoint;
        Phase phase;
        Frame previous, candidate;
        int header = -1, footer = -1; // static rows (sticky site header, browser toolbars, sticky footer)
        int notches = 1, lastNotches = 1; // the first step is one notch: a big overlap to measure pixels per notch
        double pixelsPerNotch;
        int totalHeight, noMoveCount;
        int step, lastShift, lastSettleMs; // for the log
        string lastMethod = "";            // how the last shift was found, for the log
        bool wheelSteady;                  // the last two measured wheel steps agreed
        bool recaptured; // the current position was captured again after a failed match
        DateTime scrolledAt;
        bool done;
        string failure;

        public ScrollCapture(Drawing.Rectangle region, double dpiScale, Func<Drawing.Bitmap, string> save,
            Action<string, string, string> finished)
        {
            this.region = region;
            this.dpiScale = dpiScale;
            this.save = save;
            this.finished = finished;
            scrollbarMargin = Math.Min((int)(24 * dpiScale), region.Width / 4);
            maxHeight = Math.Min(20000, MaxPixels / Math.Max(1, region.Width));
            timer.Tick += Tick;
        }

        // ---------- choosing the area ----------

        // The scrollable part of the window under the point (e.g. the page in a browser), or the window's client area.
        public static Drawing.Rectangle FindScrollableArea(Drawing.Point point)
        {
            Drawing.Rectangle area = Drawing.Rectangle.Empty;
            try
            {
                AutomationElement element = AutomationElement.FromPoint(new Point(point.X, point.Y));
                for (int depth = 0; element != null && depth < 30; depth++)
                {
                    object pattern;
                    if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out pattern)
                        && ((ScrollPattern)pattern).Current.VerticallyScrollable)
                    {
                        Rect r = element.Current.BoundingRectangle;
                        if (!r.IsEmpty && r.Width >= 100 && r.Height >= 100)
                        {
                            area = new Drawing.Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
                            break;
                        }
                    }
                    element = TreeWalker.ControlViewWalker.GetParent(element);
                }
            }
            catch (Exception ex)
            {
                // UI Automation is best effort; fall back to the window below.
                Log.Write("UI Automation failed: {0}", ex.Message);
            }
            Log.Write("Click at {0}: {1}", point, area.IsEmpty ? "using the window's client area" : "scrollable element " + area);
            if (area.IsEmpty) area = WindowClientArea(point);
            area.Intersect(System.Windows.Forms.Screen.FromPoint(point).Bounds);
            return area;
        }

        static Drawing.Rectangle WindowClientArea(Drawing.Point point)
        {
            IntPtr hwnd = GetAncestor(WindowFromPoint(new POINT { X = point.X, Y = point.Y }), GA_ROOT);
            RECT rc;
            if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out rc)) return Drawing.Rectangle.Empty;
            var origin = new POINT();
            ClientToScreen(hwnd, ref origin);
            return new Drawing.Rectangle(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
        }

        // ---------- the capture loop ----------

        public void Start()
        {
            Log.Write("Long screenshot: area {0}, scale {1}, height limit {2}", region, dpiScale, maxHeight);
            ShowChrome();
            GetCursorPos(out savedCursor);
            wheelPoint = WheelSpot(0);
            SetCursorPos(wheelPoint.X, wheelPoint.Y); // the wheel scrolls the window under the cursor
            phase = Phase.First;
            Schedule(150);
        }

        public void Stop()
        {
            if (!done) Log.Write("  stopped with Esc");
            Finish(null);
        }

        // Where to put the cursor for the wheel: the center first; on retries a bit higher or lower,
        // in case something under the cursor (a carousel, an embedded frame) catches the wheel.
        POINT WheelSpot(int attempt)
        {
            int x = region.X + region.Width / 2;
            switch (attempt % 3)
            {
                case 1: return new POINT { X = x, Y = region.Y + region.Height / 4 };
                case 2: return new POINT { X = x, Y = region.Y + region.Height * 3 / 4 };
                default: return new POINT { X = x, Y = region.Y + region.Height / 2 };
            }
        }

        void Schedule(int milliseconds)
        {
            timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            timer.Start();
        }

        void Tick(object sender, EventArgs e)
        {
            timer.Stop();
            if (done) return;
            try
            {
                if (phase == Phase.First)
                {
                    previous = Capture();
                    phase = Phase.Settling;
                    ScrollAndWait();
                }
                else if (phase == Phase.Retry)
                {
                    phase = Phase.Settling;
                    ScrollAndWait();
                }
                else if (phase == Phase.Recapture)
                {
                    // Capture the same position again, without scrolling.
                    phase = Phase.Settling;
                    scrolledAt = DateTime.Now;
                    Settle();
                }
                else Settle();
            }
            catch (Exception ex)
            {
                Log.Write("  error: {0}", ex);
                failure = ex.Message;
                Finish(null);
            }
        }

        void ScrollAndWait()
        {
            // The wheel scrolls the window under the cursor: put the cursor back in case the mouse was bumped.
            SetCursorPos(wheelPoint.X, wheelPoint.Y);
            lastNotches = notches;
            // One event worth several notches: browsers may drop some of a burst of separate notches,
            // which makes the step size unpredictable.
            var inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].mi.mouseData = unchecked((uint)(-WHEEL_STEP * notches)); // negative = scroll down
            inputs[0].mi.dwFlags = MOUSEEVENTF_WHEEL;
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            scrolledAt = DateTime.Now;
            Schedule(SettleMinMs);
        }

        // Waits until the picture stops changing (smooth scrolling has finished).
        void Settle()
        {
            Frame frame = Capture();
            double elapsed = (DateTime.Now - scrolledAt).TotalMilliseconds;
            bool stable = candidate != null && SamePixels(frame, candidate);
            bool moved = !SamePixels(frame, previous);
            if (candidate != null) candidate.Dispose();
            candidate = frame;
            if (elapsed < SettleMaxMs && !(stable && (moved || elapsed >= NoMoveMs)))
            {
                Schedule(SettlePollMs);
                return;
            }
            candidate = null;
            lastSettleMs = (int)elapsed;
            Process(frame);
        }

        void Process(Frame frame)
        {
            step++;
            int notchesUsed = lastNotches;
            int result = Append(frame);
            Log.Write("  step {0}: {1} notch(es), settled in {2} ms -> {3}, header {4}, footer {5}, total {6} px",
                step, notchesUsed, lastSettleMs,
                result > 0 ? "moved " + lastShift + " px (" + lastMethod + ")" : result == 0 ? "nothing moved" : "no match",
                header, footer, totalHeight + Math.Max(0, footer));
            if (result == 0)
            {
                // Nothing moved: the end, or a feed still loading more posts. Wait a little longer each time
                // and put the wheel elsewhere in the area before trying again.
                if (++noMoveCount >= MaxNoMoveAttempts)
                {
                    Finish(null);
                    return;
                }
                statusText.Text = "Жду подгрузку…";
                wheelPoint = WheelSpot(noMoveCount);
                phase = Phase.Retry;
                Schedule(RetryWaitMs * noMoveCount);
                return;
            }
            if (result < 0)
            {
                if (recaptured)
                {
                    Finish("partial");
                    return;
                }
                // Pictures may still be loading: give them a second and capture the same place again.
                recaptured = true;
                statusText.Text = "Жду, пока догрузится…";
                phase = Phase.Recapture;
                Schedule(1000);
                return;
            }
            recaptured = false;
            noMoveCount = 0;
            statusText.Text = string.Format("Прокручиваю…  {0:N0} px", totalHeight + footer);
            if (totalHeight + footer >= maxHeight) Finish("limit");
            else ScrollAndWait();
        }

        // The first frame of the capture.
        internal void Begin(Frame first)
        {
            previous = first;
        }

        // Adds the rows the frame reveals below the previous one; takes ownership of the frame.
        // Returns 1 = appended, 0 = nothing moved, -1 = no reliable match.
        internal int Append(Frame frame)
        {
            int h = frame.Height;
            bool identical = SamePixels(previous, frame);
            if (!identical && footer < 0) DetectStaticRows(previous, frame, out header, out footer);

            double expected = pixelsPerNotch > 0 ? pixelsPerNotch * lastNotches : 100 * dpiScale * lastNotches;
            int shift = identical ? 0 : FindShift(previous, frame, header, h - footer, expected);
            lastShift = shift;
            if (shift <= 0)
            {
                frame.Dispose();
                return shift;
            }

            if (pieces.Count == 0)
            {
                pieces.Add(Crop(previous.Bitmap, 0, h - footer)); // first frame without the sticky footer
                totalHeight = h - footer;
            }
            pieces.Add(Crop(frame.Bitmap, h - footer - shift, shift)); // only the newly revealed rows
            totalHeight += shift;

            // Aim for half of the moving part per step: frames overlap by half, enough to match even sparse pages.
            // Only shifts seen in the picture count as measurements of the wheel step.
            double measured = (double)shift / lastNotches;
            if (lastMethod == "match" || lastMethod == "peak")
            {
                wheelSteady = pixelsPerNotch > 0 && Math.Abs(measured - pixelsPerNotch) <= pixelsPerNotch * 0.02;
                pixelsPerNotch = measured;
            }
            else if (pixelsPerNotch <= 0) pixelsPerNotch = measured;
            int target = (int)((h - header - footer) * 0.5);
            notches = Math.Max(1, Math.Min(15, (int)Math.Round(target / pixelsPerNotch)));
            // The second step is short too: two measurements of the wheel step early, while frames overlap a lot.
            if (pieces.Count <= 2) notches = Math.Min(notches, 2);

            previous.Dispose();
            previous = frame;
            return 1;
        }

        // ---------- stitching ----------

        Frame Capture()
        {
            var bitmap = new Drawing.Bitmap(region.Width, region.Height, Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = Drawing.Graphics.FromImage(bitmap))
            {
                IntPtr target = g.GetHdc();
                IntPtr desktop = GetDC(IntPtr.Zero);
                try
                {
                    BitBlt(target, 0, 0, region.Width, region.Height, desktop, region.X, region.Y, SRCCOPY);
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, desktop);
                    g.ReleaseHdc(target);
                }
            }
            return Frame.FromBitmap(bitmap);
        }

        static bool SamePixels(Frame a, Frame b)
        {
            int[] pa = a.Pixels, pb = b.Pixels;
            for (int i = 0; i < pa.Length; i++)
                if (pa[i] != pb[i]) return false;
            return true;
        }

        // Rows at the top and bottom that did not change between two frames are not part of the scrolling content.
        void DetectStaticRows(Frame a, Frame b, out int top, out int bottom)
        {
            int w = a.Width, h = a.Height, columns = w - scrollbarMargin;
            top = 0;
            while (top < h && RowsEqual(a, b, top, top, columns)) top++;
            bottom = 0;
            while (bottom < h - top && RowsEqual(a, b, h - 1 - bottom, h - 1 - bottom, columns)) bottom++;
            // Blank page areas are also "equal" after a small scroll. Real sticky parts are smaller:
            // a header with browser toolbars is under a third of the area, a sticky footer under a fifth.
            top = Math.Min(top, h * 3 / 10);
            bottom = Math.Min(bottom, h / 5);
            if (h - top - bottom < 40) top = bottom = 0; // almost nothing moved; do not trust it
        }

        static bool RowsEqual(Frame a, Frame b, int rowA, int rowB, int columns)
        {
            int oa = rowA * a.Width, ob = rowB * b.Width;
            for (int x = 0; x < columns; x++)
                if (a.Pixels[oa + x] != b.Pixels[ob + x]) return false;
            return true;
        }

        // How many pixels the content moved up from frame a to frame b:
        // 0 = it did not move, -1 = no reliable match.
        // Each vertical band finds its own shift and votes with the number of rows it matched:
        // static sidebars vote 0, the scrolling content votes for the real shift.
        int FindShift(Frame a, Frame b, int top, int bottom, double expected)
        {
            ulong[][] ha, hb;
            bool[][] informativeA, informativeB;
            HashRows(a, 0, a.Width - scrollbarMargin, out ha, out informativeA);
            HashRows(b, 0, b.Width - scrollbarMargin, out hb, out informativeB);

            var votes = new Dictionary<int, long>();
            for (int k = 0; k < Bands; k++)
            {
                int matched;
                int d = BandShift(ha[k], hb[k], informativeA[k], informativeB[k], top, bottom, expected, out matched);
                if (d < 0) continue;
                long weight;
                votes.TryGetValue(d, out weight);
                votes[d] = weight + matched;
            }

            int best = -1;
            long bestWeight = 0;
            foreach (var vote in votes)
            {
                if (vote.Key == 0) continue;
                bool closer = best < 0 || Math.Abs(vote.Key - expected) < Math.Abs(best - expected);
                if (vote.Value > bestWeight || (vote.Value == bestWeight && closer))
                {
                    best = vote.Key;
                    bestWeight = vote.Value;
                }
            }
            if (best > 0 && bestWeight >= MinVoteRows)
            {
                lastMethod = "match";
                return best;
            }
            int peak = PeakShift(ha, hb, informativeA, informativeB, top, bottom, expected);
            if (peak > 0)
            {
                lastMethod = "peak";
                return peak;
            }
            if (votes.ContainsKey(0)) return 0;

            // Nothing distinctive in the shared part (plain background): the exact shift cannot be seen.
            // Take the shift nearest to the measured (or, on the first step, typical) step that no band
            // contradicts; at worst a blank gap ends up a few pixels taller or shorter.
            int guess = (int)Math.Round(expected), maxShift = bottom - top - MinOverlapRows;
            bool measured = pixelsPerNotch > 0; // a measured wheel step is reliable even with no evidence at all
            for (int delta = 0; delta <= 40; delta++)
            {
                foreach (int d in new[] { guess - delta, guess + delta })
                {
                    if (d > 0 && d <= maxShift && MostBandsAgree(ha, hb, informativeA, informativeB, top, bottom, d, measured))
                    {
                        lastMethod = "blank area, wheel step";
                        return d;
                    }
                }
            }

            // The whole shared part changed (a picture loaded or a GIF filled it), so nothing can match.
            // If the wheel has been moving the page by the same amount every time, trust it.
            if (wheelSteady && guess > 0 && guess <= maxShift)
            {
                lastMethod = "content changed, wheel step";
                return guess;
            }
            return -1;
        }

        // Agreeing bands outnumber disagreeing ones 2:1. With allowSilence (measured step), a blank area where
        // no band agrees is accepted if at most one band objects (typically the edge of a sidebar).
        // Bands that match without any shift (static sidebars) have no say.
        static bool MostBandsAgree(ulong[][] ha, ulong[][] hb, bool[][] informativeA, bool[][] informativeB,
            int top, int bottom, int d, bool allowSilence)
        {
            int agree = 0, disagree = 0;
            for (int k = 0; k < Bands; k++)
            {
                int m, c;
                CountMatches(ha[k], hb[k], informativeA[k], informativeB[k], top, bottom, d, out m, out c);
                if (c == 0) continue;
                if (m * 10 >= c * 9)
                {
                    agree++;
                    continue;
                }
                int m0, c0;
                CountMatches(ha[k], hb[k], informativeA[k], informativeB[k], top, bottom, 0, out m0, out c0);
                if (c0 > 0 && m0 * 10 >= c0 * 6) continue; // mostly static band (e.g. a sidebar whose highlight moves)
                disagree++;
            }
            if (agree == 0) return allowSilence && disagree <= 1;
            return agree >= 2 * disagree;
        }

        // For pages that change while being captured (pictures loading, GIFs, rotating ads) the strict 90% rule
        // fails: a picture that loaded between frames spoils every band. But the text around it still matches.
        // Each band takes the shift where the most of its rows match, if that shift clearly stands out;
        // the shift must also be close to what the wheel step should give.
        static int PeakShift(ulong[][] ha, ulong[][] hb, bool[][] informativeA, bool[][] informativeB,
            int top, int bottom, double expected)
        {
            int maxShift = bottom - top - MinOverlapRows;
            if (maxShift < 1) return -1;
            var votes = new Dictionary<int, long>();
            var matched = new int[maxShift + 1];
            for (int k = 0; k < Bands; k++)
            {
                int best = -1, bestMatched = 0;
                for (int d = 0; d <= maxShift; d++)
                {
                    int m, c;
                    CountMatches(ha[k], hb[k], informativeA[k], informativeB[k], top, bottom, d, out m, out c);
                    matched[d] = m;
                    if (m > bestMatched)
                    {
                        bestMatched = m;
                        best = d;
                    }
                }
                if (best <= 0 || bestMatched < 3) continue; // static band (best without shift) or too little
                int runnerUp = 0;
                for (int d = 0; d <= maxShift; d++)
                    if (Math.Abs(d - best) > 8 && matched[d] > runnerUp) runnerUp = matched[d];
                if (bestMatched < 2 * runnerUp) continue; // no clear peak
                long weight;
                votes.TryGetValue(best, out weight);
                votes[best] = weight + bestMatched;
            }

            int shift = -1;
            long shiftWeight = 0;
            foreach (var vote in votes)
            {
                if (vote.Value > shiftWeight)
                {
                    shift = vote.Key;
                    shiftWeight = vote.Value;
                }
            }
            if (shift < 0 || shiftWeight < 8) return -1;
            if (expected > 0 && (shift < expected * 0.5 || shift > expected * 1.5)) return -1;
            return shift;
        }

        // Rows of the shared part that are distinctive in either frame (c), and how many of them match (m).
        static void CountMatches(ulong[] a, ulong[] b, bool[] informativeA, bool[] informativeB,
            int top, int bottom, int d, out int m, out int c)
        {
            m = c = 0;
            for (int y = top; y < bottom - d; y++)
            {
                if (!informativeB[y] && !informativeA[y + d]) continue;
                c++;
                // A distinctive row is an edge (it differs from the row above), so the row above must match too:
                // otherwise an edge would also "match" anywhere inside a run of identical rows in the other frame.
                // (The first row of the area has no scrolling row above it: above is the static header.)
                if (b[y] == a[y + d] && (y == top || b[y - 1] == a[y + d - 1])) m++;
            }
        }

        // Best shift for one band: at least 90% of its distinctive rows must match.
        // -1 if none, or if a clearly different shift fits as well (repeating pattern such as a menu).
        static int BandShift(ulong[] a, ulong[] b, bool[] informativeA, bool[] informativeB,
            int top, int bottom, double expected, out int bestMatched)
        {
            int best = -1;
            double bestRatio = 0, bestDistance = double.MaxValue;
            bestMatched = 0;
            var passing = new List<KeyValuePair<int, double>>();
            for (int d = 0; d <= bottom - top - MinOverlapRows; d++)
            {
                int m, c;
                CountMatches(a, b, informativeA, informativeB, top, bottom, d, out m, out c);
                if (c < MinBandRows || m * 10 < c * 9) continue;
                double ratio = (double)m / c, distance = Math.Abs(d - expected);
                passing.Add(new KeyValuePair<int, double>(d, ratio));
                if (ratio > bestRatio + 0.001 || (Math.Abs(ratio - bestRatio) <= 0.001 && distance < bestDistance))
                {
                    best = d;
                    bestRatio = ratio;
                    bestDistance = distance;
                    bestMatched = m;
                }
            }
            foreach (var other in passing)
                if (Math.Abs(other.Key - best) > 8 && other.Value >= bestRatio - 0.05) return -1;
            return best;
        }

        static void HashRows(Frame f, int x0, int x1, out ulong[][] hashes, out bool[][] informative)
        {
            hashes = new ulong[Bands][];
            informative = new bool[Bands][];
            int span = x1 - x0;
            for (int k = 0; k < Bands; k++)
            {
                int from = x0 + span * k / Bands, to = x0 + span * (k + 1) / Bands;
                var bandHashes = new ulong[f.Height];
                var bandInformative = new bool[f.Height];
                for (int y = 0; y < f.Height; y++)
                {
                    int o = y * f.Width;
                    int first = f.Pixels[o + from];
                    bool varied = false;
                    ulong hash = 14695981039346656037UL; // FNV-1a
                    for (int x = from; x < to; x++)
                    {
                        int p = f.Pixels[o + x];
                        if (p != first) varied = true;
                        hash = (hash ^ (uint)p) * 1099511628211UL;
                    }
                    bandHashes[y] = hash;
                    // Distinctive = not a single color and not a repeat of the row above:
                    // runs of identical rows would match at several nearby shifts.
                    bandInformative[y] = varied && (y == 0 || hash != bandHashes[y - 1]);
                }
                hashes[k] = bandHashes;
                informative[k] = bandInformative;
            }
        }

        static Drawing.Bitmap Crop(Drawing.Bitmap source, int y, int height)
        {
            return source.Clone(new Drawing.Rectangle(0, y, source.Width, height), source.PixelFormat);
        }

        // ---------- finishing ----------

        // reason: null = normal end, "limit" = height limit reached, "partial" = frames stopped matching.
        void Finish(string reason)
        {
            if (done) return;
            done = true;
            timer.Stop();
            CloseChrome();
            SetCursorPos(savedCursor.X, savedCursor.Y);
            if (candidate != null) candidate.Dispose();

            string path = null, error = failure, title = null;
            if (failure == null && previous != null)
            {
                try
                {
                    using (Drawing.Bitmap result = Compose())
                    {
                        path = save(result);
                        if (path == null) title = string.Format("Длинный скриншот {0:N0} px не сохранён", result.Height);
                        else if (pieces.Count == 0) title = "Прокрутка не сработала — сохранён обычный скриншот";
                        else if (reason == "limit") title = string.Format("Длинный скриншот {0:N0} px (достигнут предел)", result.Height);
                        else if (reason == "partial") title = string.Format("Длинный скриншот {0:N0} px — дальше склеить не удалось", result.Height);
                        else title = string.Format("Длинный скриншот сохранён: {0:N0} px", result.Height);
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            }
            Log.Write("  finished: {0}; {1}", reason ?? (noMoveCount >= MaxNoMoveAttempts ? "end of content" : "stopped"),
                error != null ? "error: " + error : title + " -> " + path);
            foreach (var piece in pieces) piece.Dispose();
            pieces.Clear();
            if (previous != null) previous.Dispose();
            finished(path, error, title);
        }

        internal Drawing.Bitmap Compose()
        {
            if (pieces.Count == 0) return Crop(previous.Bitmap, 0, previous.Height); // the area never scrolled
            if (footer > 0) pieces.Add(Crop(previous.Bitmap, previous.Height - footer, footer));
            int height = 0;
            foreach (var piece in pieces) height += piece.Height;
            var result = new Drawing.Bitmap(region.Width, height, Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = Drawing.Graphics.FromImage(result))
            {
                int y = 0;
                foreach (var piece in pieces)
                {
                    g.DrawImageUnscaled(piece, 0, y);
                    y += piece.Height;
                }
            }
            return result;
        }

        // ---------- frame around the area and the status label ----------

        void ShowChrome()
        {
            var accent = new SolidColorBrush(Color.FromRgb(0xFF, 0x2D, 0x78));
            int pad = (int)Math.Ceiling(3 * dpiScale); // the frame lies just outside the captured area
            frameWindow = MakeWindow(new Border { BorderBrush = accent, BorderThickness = new Thickness(2) });
            frameWindow.Left = (region.X - pad) / dpiScale;
            frameWindow.Top = (region.Y - pad) / dpiScale;
            frameWindow.Width = (region.Width + 2 * pad) / dpiScale;
            frameWindow.Height = (region.Height + 2 * pad) / dpiScale;

            statusText = new TextBlock { Text = "Прокручиваю…", Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold };
            var panel = new StackPanel();
            panel.Children.Add(statusText);
            panel.Children.Add(new TextBlock
            {
                Text = "Остановить — Esc",
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0)
            });
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(240, 32, 33, 36)),
                BorderBrush = accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 8, 14, 8),
                Child = panel
            };
            panelWindow = MakeWindow(card);
            panelWindow.SizeToContent = SizeToContent.WidthAndHeight;
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            PlacePanel(card.DesiredSize, pad);

            panelHandle = new WindowInteropHelper(panelWindow).EnsureHandle();
            HwndSource.FromHwnd(panelHandle).AddHook(PanelProc);
            RegisterHotKey(panelHandle, HOTKEY_ESC_ID, MOD_NOREPEAT, VK_ESCAPE);
            frameWindow.Show();
            panelWindow.Show();
        }

        // Below the area if there is room on its monitor, else above it, else inside at the bottom.
        void PlacePanel(Size size, int pad)
        {
            Drawing.Rectangle monitor = System.Windows.Forms.Screen.FromRectangle(region).Bounds;
            double width = size.Width * dpiScale, height = size.Height * dpiScale, gap = 8 * dpiScale;
            double x = region.X + (region.Width - width) / 2;
            double y = region.Bottom + pad + gap;
            if (y + height > monitor.Bottom) y = region.Y - pad - gap - height;
            if (y < monitor.Top) y = region.Bottom - height - 3 * gap;
            panelWindow.Left = x / dpiScale;
            panelWindow.Top = y / dpiScale;
        }

        // Topmost, never activated, click-through, and excluded from screen capture.
        static Window MakeWindow(UIElement content)
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = content
            };
            IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
            SetWindowLong(handle, GWL_EXSTYLE,
                GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE);
            return window;
        }

        IntPtr PanelProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ESC_ID)
            {
                handled = true;
                Stop();
            }
            return IntPtr.Zero;
        }

        void CloseChrome()
        {
            if (panelHandle != IntPtr.Zero) UnregisterHotKey(panelHandle, HOTKEY_ESC_ID);
            if (panelWindow != null) panelWindow.Close();
            if (frameWindow != null) frameWindow.Close();
        }
    }
}
