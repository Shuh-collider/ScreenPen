using System;
using System.Collections.Generic;
using System.Reflection;
using ScreenPen;

// Per-band view of one step, using the app's own static-row detection and band matching.
static class StitchDebug
{
    public static void Run(int seed, int step, bool big, Func<Random, int, System.Drawing.Bitmap> makePage,
        Func<System.Drawing.Bitmap, int, int, int, System.Drawing.Bitmap> makeFrame, int W, int H)
    {
        var rnd = new Random(seed);
        int pageHeight = 5200, contentHeight = H - 56 - 36;
        var page = makePage(rnd, pageHeight);
        // step = scroll position of the second frame; the first frame is at 0 when big is false,
        // otherwise at step - 400 (a wheel-100 step of 4 notches).
        int scrollA = big ? step - 300 : 0, scrollB = step;
        string fromEnv = Environment.GetEnvironmentVariable("STITCH_FROM");
        if (fromEnv != null) scrollA = int.Parse(fromEnv);
        var a = ScrollCapture.Frame.FromBitmap(makeFrame(page, scrollA, 1, pageHeight));
        var b = ScrollCapture.Frame.FromBitmap(makeFrame(page, scrollB, 2, pageHeight));
        int actual = scrollB - scrollA;
        var t = typeof(ScrollCapture);
        var flags = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var capture = new ScrollCapture(new System.Drawing.Rectangle(0, 0, W, H), 1.0, null, null);
        object[] st = { a, b, 0, 0 };
        t.GetMethod("DetectStaticRows", flags).Invoke(capture, st);
        int top = (int)st[2], bottom = H - (int)st[3];
        string forced = Environment.GetEnvironmentVariable("STITCH_STATIC"); // "top,footer" from the real run
        if (forced != null) { top = int.Parse(forced.Split(',')[0]); bottom = H - int.Parse(forced.Split(',')[1]); }
        object[] pa = { a, 0, W - 24, null, null }, pb = { b, 0, W - 24, null, null };
        t.GetMethod("HashRows", flags).Invoke(null, pa);
        t.GetMethod("HashRows", flags).Invoke(null, pb);
        var ha = (ulong[][])pa[3]; var hb = (ulong[][])pb[3]; var ib = (bool[][])pb[4];
        Console.WriteLine("seed {0} step {1}: actual shift {2}, header {3}, footer {4}", seed, step, actual, top, H - bottom);
        for (int k = 0; k < ha.Length; k++)
        {
            var pass = new List<string>();
            for (int d = 0; d <= bottom - top - 8; d++)
            {
                int m = 0, c = 0;
                for (int y = top; y < bottom - d; y++)
                {
                    if (!ib[k][y]) continue; c++;
                    if (hb[k][y] == ha[k][y + d] && (y == 0 || hb[k][y - 1] == ha[k][y + d - 1])) m++;
                }
                if (c >= 3 && m * 10 >= c * 9) pass.Add(d + "(" + m + "/" + c + ")");
            }
            int am = 0, ac = 0, single = 0;
            for (int y = top; y < bottom - actual; y++)
            {
                if (!ib[k][y]) continue; ac++;
                if (hb[k][y] == ha[k][y + actual]) single++;
                if (hb[k][y] == ha[k][y + actual] && (y == 0 || hb[k][y - 1] == ha[k][y + actual - 1])) am++;
            }
            Console.Write("  at actual: {0}/{1} (row only {2}); ", am, ac, single);
            object[] args = { ha[k], hb[k], ((bool[][])pa[4])[k], ib[k], top, bottom, (double)actual, 0 };
            int vote = (int)t.GetMethod("BandShift", flags).Invoke(null, args);
            Console.WriteLine("band {0}: vote {1}; passing: {2}", k, vote, pass.Count > 12 ? pass.Count + " shifts" : string.Join(" ", pass));
            var cm = t.GetMethod("CountMatches", flags);
            object[] atD = { ha[k], hb[k], ((bool[][])pa[4])[k], ib[k], top, bottom, actual, 0, 0 };
            object[] at0 = { ha[k], hb[k], ((bool[][])pa[4])[k], ib[k], top, bottom, 0, 0, 0 };
            cm.Invoke(null, atD); cm.Invoke(null, at0);
            Console.WriteLine("        both-sided at actual: {0}/{1}; at 0: {2}/{3}", atD[7], atD[8], at0[7], at0[8]);
            // peak view: best and runner-up by matched rows
            int bestD = -1, bestM = 0; var ms = new int[bottom - top - 8 + 1];
            for (int d = 0; d < ms.Length; d++)
            {
                object[] q = { ha[k], hb[k], ((bool[][])pa[4])[k], ib[k], top, bottom, d, 0, 0 };
                cm.Invoke(null, q); ms[d] = (int)q[7];
                if (ms[d] > bestM) { bestM = ms[d]; bestD = d; }
            }
            int run = 0, runD = -1;
            for (int d = 0; d < ms.Length; d++) if (Math.Abs(d - bestD) > 8 && ms[d] > run) { run = ms[d]; runD = d; }
            Console.WriteLine("        peak: d={0} matched {1}; runner-up d={2} matched {3}", bestD, bestM, runD, run);
        }
    }
}

