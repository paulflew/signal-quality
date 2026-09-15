// Console harness: checks the green/yellow/red rules and time window, makes real web checks,
// and optionally renders every tray icon into a contact sheet for a visual check.
//   Tests.exe [output-folder-for-icon-sheet]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace SignalQuality
{
    static class Tests
    {
        static int failures;
        static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        static void Check(bool condition, string name)
        {
            Console.WriteLine((condition ? "PASS  " : "FAIL  ") + name);
            if (!condition) failures++;
        }

        // null = a failed check, a number = a successful check taking that long; 5 s apart
        static List<Sample> Window(params long?[] checks)
        {
            var list = new List<Sample>();
            for (int i = 0; i < checks.Length; i++)
            {
                DateTime at = T0.AddSeconds(5 * i);
                list.Add(checks[i].HasValue ? Sample.Success(checks[i].Value, at) : Sample.Failure("timed out", at));
            }
            return list;
        }

        static Status Judge(params long?[] checks)
        {
            return Classifier.Assess(Window(checks), new Settings()).Status;   // defaults: green <= 150, yellow <= 400
        }

        static Trend Trend(params long?[] checks)
        {
            return Classifier.Assess(Window(checks), new Settings()).Trend;
        }

        static int Main(string[] args)
        {
            bool offline = Array.IndexOf(args, "--offline") >= 0;
            string iconFolder = null;
            foreach (string arg in args) if (!arg.StartsWith("--")) iconFolder = arg;

            Check(Judge() == Status.Unknown, "no checks yet is unknown");
            Check(Judge(40, 45, 50) == Status.Green, "fast checks are green");
            Check(Judge(150, 150) == Status.Green, "exactly the green limit is still green");
            Check(Judge(200, 300, 250) == Status.Yellow, "medium times are yellow");
            Check(Judge(600, 700, 650) == Status.Red, "slow checks are red");
            Check(Judge(40, 40, 40, 40, 40, 1200) == Status.Yellow, "a single spike degrades to yellow, not red");
            Check(Judge(40, 40, 40, 40, null) == Status.Yellow, "one failed check is yellow");
            Check(Judge(40, 40, 40, null, null) == Status.Red, "two failures in a row is red");
            Check(Judge((long?)null) == Status.Red, "failing on the very first check is red");
            Check(Judge(null, 40, null, 40, null) == Status.Red, "60% failures is red");
            Check(Judge(null, 40, 40, 40, 40) == Status.Yellow, "a failure keeps it yellow while it's in the window");

            Assessment noConnection = Classifier.Assess(Window(null, null, null), new Settings());
            Check(noConnection.Offline && noConnection.Headline == "No connection" && double.IsNaN(noConnection.AvgMs), "total failure reads as no connection");
            Assessment slow = Classifier.Assess(Window(800, 900), new Settings());
            Check(!slow.Offline && slow.Headline == "Poor" && slow.AvgMs == 850, "slow but answering reads as poor");
            // Arrow: only when the latest check lands outside the band the light is showing.
            Check(Trend(40, 45, 50) == SignalQuality.Trend.None, "no arrow when the latest check matches the light");
            Check(Trend(40, 40, 40, 40, 40, 300) == SignalQuality.Trend.Worse, "slow check under a green light points down-right");
            Check(Trend(300, 300, 300, 300, 60) == SignalQuality.Trend.Better, "fast check under a yellow light points up-right");
            Check(Trend(300, 300, 300, 300, 250) == SignalQuality.Trend.None, "yellow-band check under a yellow light shows no arrow");
            Check(Trend(40, 40, 40, 40, null) == SignalQuality.Trend.Worse, "a failed check under a yellow light points down-right");
            Check(Trend(null, null, null) == SignalQuality.Trend.None, "still failing while offline shows no arrow");
            Check(Trend(null, null, null, 60) == SignalQuality.Trend.Better, "first reply after an outage points up-right");
            Check(Trend(600, 600, 600, 250) == SignalQuality.Trend.Better, "a yellow-band check under a slow red light points up-right");
            Check(Classifier.Assess(Window(300, 300, 300, 300, 60), new Settings()).LatestBand == Status.Green, "arrow takes the latest check's colour (green)");
            Check(Classifier.Assess(Window(40, 40, 40, 40, null), new Settings()).LatestBand == Status.Red, "a failed latest check colours the arrow red");

            var strict = new Settings { GreenMaxMs = 30, YellowMaxMs = 60 };
            Check(Classifier.Assess(Window(45, 45), strict).Status == Status.Yellow, "custom thresholds are respected");

            // Time window: checks every 5 s, averaged over 30 s, keeps the last 6 checks.
            var history = new History();
            for (int i = 0; i <= 12; i++) history.Add(Sample.Success(i == 0 ? 5000 : 40, T0.AddSeconds(5 * i)), 30);
            Check(history.Samples.Count == 6, "30 s window at 5 s intervals holds 6 checks");
            Check(Classifier.Assess(history.Samples, new Settings()).Status == Status.Green, "old slow checks age out of the window");
            history.Add(Sample.Success(40, T0.AddMinutes(10)), 30);
            Check(history.Samples.Count == 1, "checks from before a long gap (e.g. sleep) are discarded");

            Check(Settings.CleanUrl("example.com") == "http://example.com/", "bare host names become http URLs");
            Check(Settings.CleanUrl("ftp://example.com") == null && Settings.CleanUrl("  ") == null, "non-web addresses are rejected");
            var messy = new Settings { Url = "not a url", GreenMaxMs = 900, YellowMaxMs = 100, IntervalSeconds = 0, WindowSeconds = 0 };
            messy.Normalize();
            Check(messy.Url == Settings.DefaultUrl && messy.YellowMaxMs > messy.GreenMaxMs && messy.IntervalSeconds == 1 && messy.WindowSeconds == 1,
                "bad settings are normalized");

            if (!offline) LiveChecks();

            using (Icon icon = IconFactory.Create(Status.Green, Status.Red, 32))
                Check(icon.Width == 32 && icon.Height == 32, "tray icon handle is created");

            if (iconFolder != null) WriteContactSheet(Path.Combine(iconFolder, "icons.png"));

            Console.WriteLine(failures == 0 ? "\nAll checks passed." : "\n" + failures + " check(s) failed.");
            return failures;
        }

        // Real requests over the network. Skipped by --offline, so CI doesn't depend on the
        // runner's connection, and so a flaky network doesn't read as a broken build.
        static void LiveChecks()
        {
            Console.WriteLine("      live checks of " + Settings.DefaultUrl + ":");
            int ok = 0;
            for (int i = 0; i < 10; i++)
            {
                if (i > 0) System.Threading.Thread.Sleep(1000);
                Sample s = Prober.Check(Settings.DefaultUrl, 3000).Result;
                Console.WriteLine("        " + (s.Ok ? s.Ms + " ms" : s.Error));
                if (s.Ok) ok++;
            }
            Check(ok >= 8, "google connectivity check answers");

            Sample plain = Prober.Check("http://www.google.com/generate_204", 3000).Result;
            // Informational only: some networks (e.g. train Wi-Fi) route plain HTTP through a slow gateway.
            Console.WriteLine("      plain http check (info): " + (plain.Ok ? plain.Ms + " ms" : plain.Error));

            Sample bogus = Prober.Check("http://no-such-host.invalid/", 3000).Result;
            Console.WriteLine("      unknown host: " + (bogus.Ok ? bogus.Ms + " ms" : bogus.Error));
            Check(!bogus.Ok, "an unresolvable host counts as a failure");

            var clock = Stopwatch.StartNew();
            // TEST-NET-1 never answers. Over https, because Wi-Fi gateways can answer plain http themselves.
            Sample blackhole = Prober.Check("https://192.0.2.1/", 1000).Result;
            Console.WriteLine("      unreachable address: " + (blackhole.Ok ? blackhole.Ms + " ms" : blackhole.Error) + " after " + clock.ElapsedMilliseconds + " ms");
            Check(!blackhole.Ok && clock.ElapsedMilliseconds < 2600, "unreachable address fails within the timeout");
        }

        // Every light colour x latest-check colour, at each size on a dark and a light "taskbar", magnified 6x.
        static void WriteContactSheet(string path)
        {
            Status[] statuses = { Status.Green, Status.Yellow, Status.Red };
            Color[] taskbars = { Color.FromArgb(32, 32, 32), Color.FromArgb(238, 238, 238) };
            int[] sizes = { 16, 24, 32 };
            const int zoom = 6, cell = 32 * zoom + 12;

            int cols = statuses.Length * statuses.Length;
            int rows = taskbars.Length * sizes.Length;
            using (var sheet = new Bitmap(cols * cell, rows * cell))
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                int row = 0;
                foreach (int size in sizes)
                    foreach (Color bg in taskbars)
                    {
                        using (var b = new SolidBrush(bg)) g.FillRectangle(b, 0, row * cell, sheet.Width, cell);
                        int col = 0;
                        foreach (Status status in statuses)
                            foreach (Status latest in statuses)
                            {
                                using (Bitmap icon = IconFactory.Render(status, latest, size))
                                    g.DrawImage(icon, col * cell + 6, row * cell + 6, size * zoom, size * zoom);
                                col++;
                            }
                        row++;
                    }
                sheet.Save(path, ImageFormat.Png);
            }
            Console.WriteLine("      icon sheet written to " + path);
        }
    }
}
