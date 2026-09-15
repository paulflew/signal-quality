// Signal Quality - a single-light connectivity monitor for the Windows system tray.
//
// Every few seconds it times a small HTTPS request (Google's connectivity-check URL by
// default) and shows the average over a recent time window as one coloured light:
// green = responsive, yellow = sluggish or the odd failure, red = very slow or offline.
// Build with build.cmd, which uses the C# compiler that ships with Windows
// (.NET Framework 4.x), so the code sticks to C# 5.
//
// Why HTTPS on a kept-open connection rather than ping or plain HTTP: public Wi-Fi (trains,
// hotels, cafes) often blocks ping, and plain HTTP can be answered or slowed by the
// network's own gateway. An HTTPS answer can only come from the real server, and timing a
// request on an already-open connection measures the link itself (about one round trip)
// rather than connection setup.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

// csc.exe doesn't stamp a target framework, which would leave the runtime in legacy mode
// (no TLS 1.2+ by default, among other things). Opt in to modern behaviour explicitly.
[assembly: TargetFramework(".NETFramework,Version=v4.7.2", FrameworkDisplayName = ".NET Framework 4.7.2")]

// Kept here rather than in a generated AssemblyInfo so build.cmd stamps them too.
[assembly: AssemblyTitle("Signal Quality")]
[assembly: AssemblyProduct("Signal Quality")]
[assembly: AssemblyDescription("Connectivity traffic light for the Windows system tray")]
[assembly: AssemblyCompany("Paul Flew")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Paul Flew")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyInformationalVersion("1.1.0")]
[assembly: ComVisible(false)]

namespace SignalQuality
{
    public enum Status { Unknown, Green, Yellow, Red }

    // Whether the latest check falls in a better or worse band than the light is showing.
    public enum Trend { None, Better, Worse }

    public sealed class Settings
    {
        public const string DefaultUrl = "https://www.google.com/generate_204";

        public string Url = DefaultUrl;
        public int IntervalSeconds = 5;
        public int WindowSeconds = 30;
        public int TimeoutMs = 3000;
        public int GreenMaxMs = 150;
        public int YellowMaxMs = 400;
        public bool NotifyOnChange = false;

        static string FilePath
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "SignalQuality", "settings.ini");
            }
        }

        public string Host
        {
            get
            {
                Uri uri;
                return Uri.TryCreate(Url, UriKind.Absolute, out uri) ? uri.Host : Url;
            }
        }

        public Settings Clone() { return (Settings)MemberwiseClone(); }

        // Accepts "example.com" as shorthand for "http://example.com/". Returns null if unusable.
        public static string CleanUrl(string input)
        {
            string s = (input ?? "").Trim();
            if (s.Length == 0) return null;
            if (!s.Contains("://")) s = "http://" + s;
            Uri uri;
            if (!Uri.TryCreate(s, UriKind.Absolute, out uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
            return uri.AbsoluteUri;
        }

        public void Normalize()
        {
            Url = CleanUrl(Url) ?? DefaultUrl;
            IntervalSeconds = Clamp(IntervalSeconds, 1, 300);
            WindowSeconds = Clamp(WindowSeconds, IntervalSeconds, 3600);
            TimeoutMs = Clamp(TimeoutMs, 500, 30000);
            GreenMaxMs = Clamp(GreenMaxMs, 1, 30000);
            YellowMaxMs = Clamp(YellowMaxMs, GreenMaxMs + 1, 60000);
        }

        static int Clamp(int v, int min, int max) { return Math.Max(min, Math.Min(max, v)); }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (line.StartsWith("#") || eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    int n;
                    bool isInt = int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
                    switch (key)
                    {
                        case "url": s.Url = val; break;
                        case "intervalseconds": if (isInt) s.IntervalSeconds = n; break;
                        case "windowseconds": if (isInt) s.WindowSeconds = n; break;
                        case "timeoutms": if (isInt) s.TimeoutMs = n; break;
                        case "greenmaxms": if (isInt) s.GreenMaxMs = n; break;
                        case "yellowmaxms": if (isInt) s.YellowMaxMs = n; break;
                        case "notifyonchange": s.NotifyOnChange = val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable settings file: carry on with defaults.
            }
            s.Normalize();
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[]
                {
                    "# Signal Quality settings",
                    "Url=" + Url,
                    "IntervalSeconds=" + IntervalSeconds.ToString(CultureInfo.InvariantCulture),
                    "WindowSeconds=" + WindowSeconds.ToString(CultureInfo.InvariantCulture),
                    "TimeoutMs=" + TimeoutMs.ToString(CultureInfo.InvariantCulture),
                    "GreenMaxMs=" + GreenMaxMs.ToString(CultureInfo.InvariantCulture),
                    "YellowMaxMs=" + YellowMaxMs.ToString(CultureInfo.InvariantCulture),
                    "NotifyOnChange=" + (NotifyOnChange ? "true" : "false"),
                });
            }
            catch (Exception)
            {
                // Not being able to persist settings shouldn't take the monitor down.
            }
        }
    }

    public struct Sample
    {
        public readonly DateTime At;   // when the check started (UTC)
        public readonly bool Ok;
        public readonly long Ms;
        public readonly string Error;

        Sample(DateTime at, bool ok, long ms, string error) { At = at; Ok = ok; Ms = ms; Error = error; }

        public static Sample Success(long ms, DateTime at) { return new Sample(at, true, ms, null); }
        public static Sample Failure(string error, DateTime at) { return new Sample(at, false, 0, error); }
    }

    // The checks that fall inside a sliding time window (e.g. the last 30 seconds).
    public sealed class History
    {
        readonly List<Sample> samples = new List<Sample>();

        public IList<Sample> Samples { get { return samples; } }

        public void Add(Sample s, int windowSeconds)
        {
            samples.Add(s);
            DateTime cutoff = s.At.AddSeconds(-windowSeconds);
            samples.RemoveAll(x => x.At <= cutoff);
        }

        public void Clear() { samples.Clear(); }
    }

    public sealed class Assessment
    {
        public Status Status = Status.Unknown;
        public double AvgMs = double.NaN;   // NaN when no check in the window succeeded
        public double FailPct;
        public int Count;
        public bool Offline;                // red because checks are failing, not just slow
        public Sample Last;
        public Status LatestBand;           // the band the latest check alone falls in
        public Trend Trend;                 // latest check vs. the band the light is showing

        public string Headline
        {
            get
            {
                switch (Status)
                {
                    case Status.Green: return "Good";
                    case Status.Yellow: return "Fair";
                    case Status.Red: return Offline ? "No connection" : "Poor";
                    default: return "Checking";
                }
            }
        }
    }

    public static class Classifier
    {
        // Judges the average of the recent window so one slow check doesn't flip the light:
        //   Red    - the last two checks failed, half or more of the window failed,
        //            or the average time is above YellowMaxMs
        //   Yellow - any failure in the window, or the average time is above GreenMaxMs
        //   Green  - everything else
        public static Assessment Assess(IList<Sample> window, Settings s)
        {
            var a = new Assessment { Count = window.Count };
            if (window.Count == 0) return a;

            a.Last = window[window.Count - 1];
            int failures = window.Count(x => !x.Ok);
            a.FailPct = 100.0 * failures / window.Count;
            if (failures < window.Count) a.AvgMs = window.Where(x => x.Ok).Average(x => (double)x.Ms);

            bool lastTwoFailed = !a.Last.Ok && (window.Count == 1 || !window[window.Count - 2].Ok);
            a.Offline = lastTwoFailed || a.FailPct >= 50;

            if (a.Offline || a.AvgMs > s.YellowMaxMs) a.Status = Status.Red;
            else if (failures > 0 || a.AvgMs > s.GreenMaxMs) a.Status = Status.Yellow;
            else a.Status = Status.Green;

            // Flag a latest check that falls outside the light's band: a fast reply while the
            // light is yellow or red is Better; a slow reply or a failure is Worse.
            a.LatestBand = Band(a.Last, s);
            if (a.LatestBand < a.Status) a.Trend = Trend.Better;
            else if (a.LatestBand > a.Status) a.Trend = Trend.Worse;
            return a;
        }

        // The band a single check falls in: by its time for a reply, red for a failure.
        public static Status Band(Sample check, Settings s)
        {
            if (!check.Ok || check.Ms > s.YellowMaxMs) return Status.Red;
            return check.Ms > s.GreenMaxMs ? Status.Yellow : Status.Green;
        }
    }

    public static class Prober
    {
        static Prober()
        {
            // Belt and braces for machines stuck on legacy TLS defaults: if the protocol list
            // isn't "system default", make sure TLS 1.2 is in it.
            if (ServicePointManager.SecurityProtocol != 0)
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        }

        // Times one GET request up to the response headers, on a connection that is already
        // open. If there's no open connection (first check, or the last one dropped), an
        // untimed request opens it first, so every sample measures the link rather than
        // DNS + TCP + TLS setup. Never throws: problems become failed samples.
        public static async Task<Sample> Check(string url, int timeoutMs)
        {
            DateTime at = DateTime.UtcNow;
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return Sample.Failure("invalid address", at);

            bool cold = ServicePointManager.FindServicePoint(uri).CurrentConnections == 0;
            var active = new HttpWebRequest[1];   // whichever request is in flight, for the watchdog

            // HttpWebRequest can block on DNS and proxy lookup, so run it off the UI thread and
            // enforce the timeout ourselves (its own Timeout doesn't cover slow DNS).
            Task<Sample> work = Task.Run(() =>
            {
                if (cold)
                {
                    Sample setup = Send(uri, timeoutMs, at, active);
                    if (!setup.Ok) return setup;
                }
                return Send(uri, timeoutMs, at, active);
            });
            int budget = (cold ? 2 * timeoutMs : timeoutMs) + 250;
            if (await Task.WhenAny(work, Task.Delay(budget)) == work) return work.Result;

            HttpWebRequest stuck = active[0];
            if (stuck != null) stuck.Abort();
            return Sample.Failure("timed out", at);
        }

        static Sample Send(Uri uri, int timeoutMs, DateTime at, HttpWebRequest[] active)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.KeepAlive = true;
            request.AllowAutoRedirect = false;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.UserAgent = "SignalQuality/1.0";
            request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            active[0] = request;

            var clock = Stopwatch.StartNew();
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                    return Judge(uri, response, clock, at);
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    using (response)
                        return Judge(uri, response, clock, at);
                }
                return Sample.Failure(Describe(ex.Status), at);
            }
            catch (Exception)
            {
                return Sample.Failure("request failed", at);
            }
        }

        static Sample Judge(Uri uri, HttpWebResponse response, Stopwatch clock, DateTime at)
        {
            long ms = clock.ElapsedMilliseconds;
            int code = (int)response.StatusCode;
            // A generate_204 URL always answers "204 No Content" on the open internet. Anything
            // else means a Wi-Fi sign-in page (captive portal) intercepted the request.
            bool expect204 = uri.AbsolutePath.EndsWith("generate_204", StringComparison.OrdinalIgnoreCase);
            if (expect204 && code != 204) return Sample.Failure("Wi-Fi sign-in page", at);
            // 502/503/504 come from a gateway or proxy that couldn't reach the server.
            if (code == 502 || code == 503 || code == 504) return Sample.Failure("gateway error " + code, at);
            return Sample.Success(ms, at);   // any other HTTP answer means the server was reached
        }

        static string Describe(WebExceptionStatus status)
        {
            switch (status)
            {
                case WebExceptionStatus.NameResolutionFailure: return "can't resolve host";
                case WebExceptionStatus.ProxyNameResolutionFailure: return "can't reach proxy";
                case WebExceptionStatus.ConnectFailure: return "can't connect";
                case WebExceptionStatus.Timeout: return "timed out";
                case WebExceptionStatus.RequestCanceled: return "timed out";
                case WebExceptionStatus.SecureChannelFailure: return "secure connection failed";
                case WebExceptionStatus.TrustFailure: return "certificate not trusted";
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.ReceiveFailure:
                case WebExceptionStatus.SendFailure: return "connection dropped";
                default: return "request failed";
            }
        }
    }

    public static class IconFactory
    {
        static readonly Color GreenLamp = Color.FromArgb(34, 197, 94);
        static readonly Color YellowLamp = Color.FromArgb(250, 204, 21);
        static readonly Color RedLamp = Color.FromArgb(239, 68, 68);
        static readonly Color GreyLamp = Color.FromArgb(156, 163, 175);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        public static Color ColorFor(Status s)
        {
            switch (s)
            {
                case Status.Green: return GreenLamp;
                case Status.Yellow: return YellowLamp;
                case Status.Red: return RedLamp;
                default: return GreyLamp;
            }
        }

        // A single light that fills the whole icon: a bezel in a darker shade of the status
        // colour, a softly lit lens inside it, and a hairline outer edge so it stays crisp on
        // light taskbars. When the latest check falls in a different band, an arrow in that
        // band's colour points up-right (better) or down-right (worse).
        public static Bitmap Render(Status status, Status latest, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float n = size;
                Color c = ColorFor(status);
                float edge = Math.Max(1f, n / 24f);
                float rim = Math.Max(1.5f, n * 0.12f);
                var full = new RectangleF(0, 0, n, n);
                var outer = new RectangleF(edge / 2, edge / 2, n - edge, n - edge);
                var lens = new RectangleF(rim, rim, n - 2 * rim, n - 2 * rim);

                using (var bezel = new LinearGradientBrush(full, Mix(c, Color.Black, 0.35f), Mix(c, Color.Black, 0.6f), 90f))
                using (var outline = new Pen(Color.FromArgb(160, 0, 0, 0), edge))
                using (var glow = new LinearGradientBrush(lens, Mix(c, Color.White, 0.3f), c, 90f))
                {
                    g.FillEllipse(bezel, outer);
                    g.DrawEllipse(outline, outer);
                    g.FillEllipse(glow, lens);
                }

                if (status != Status.Unknown && latest != Status.Unknown && latest != status)
                    DrawArrow(g, n, latest < status, ColorFor(latest));
            }
            return bmp;
        }

        // The arrow is drawn twice: a dark outline underneath, then the band colour on top,
        // so e.g. a green arrow still stands out on a yellow light.
        static void DrawArrow(Graphics g, float n, bool upRight, Color color)
        {
            float mid = n / 2f, reach = n * 0.17f, head = n * 0.21f;
            float dir = upRight ? -1f : 1f;
            var tail = new PointF(mid - reach, mid - dir * reach);
            var tip = new PointF(mid + reach, mid + dir * reach);
            float stroke = Math.Max(1.5f, n * 0.1f);
            float outline = Math.Max(1f, n * 0.05f);

            using (var shape = new GraphicsPath())
            using (var under = new Pen(Color.FromArgb(235, 20, 20, 20), stroke + 2 * outline))
            using (var over = new Pen(color, stroke))
            {
                shape.AddLine(tail, tip);
                shape.StartFigure();
                shape.AddLines(new[] { new PointF(tip.X - head, tip.Y), tip, new PointF(tip.X, tip.Y - dir * head) });
                foreach (Pen pen in new[] { under, over })
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawPath(pen, shape);
                }
            }
        }

        static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static Icon Create(Status status, Status latest, int size)
        {
            using (Bitmap bmp = Render(status, latest, size))
            {
                IntPtr handle = bmp.GetHicon();
                try
                {
                    using (Icon borrowed = Icon.FromHandle(handle))
                        return (Icon)borrowed.Clone();   // the clone owns its own copy of the handle
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "SignalQuality";
        const int TooltipMax = 63;   // NotifyIcon.Text throws beyond this on .NET Framework

        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly ToolStripMenuItem statusItem, statsItem, sessionItem, notifyItem, startupItem;
        readonly System.Windows.Forms.Timer timer;
        readonly History history = new History();
        readonly Dictionary<string, Icon> icons = new Dictionary<string, Icon>();
        readonly Dictionary<string, Bitmap> menuDots = new Dictionary<string, Bitmap>();
        readonly int iconSize = SystemInformation.SmallIconSize.Width;

        Settings settings;
        Status shown = Status.Unknown;
        Status shownLatest = Status.Unknown;
        bool busy;
        int generation;   // bumped when the address changes so in-flight results are dropped
        long sessionChecks, sessionFailures;
        SettingsForm openSettings;

        public TrayApp()
        {
            settings = Settings.Load();

            statusItem = new ToolStripMenuItem("Checking...") { ToolTipText = "Click to check now" };
            statusItem.Font = new Font(statusItem.Font, FontStyle.Bold);
            statusItem.Click += delegate { Tick(); };
            statsItem = new ToolStripMenuItem("") { Enabled = false };
            sessionItem = new ToolStripMenuItem("") { Enabled = false };
            notifyItem = new ToolStripMenuItem("Notify when status changes");
            notifyItem.Click += delegate
            {
                settings.NotifyOnChange = !settings.NotifyOnChange;
                settings.Save();
            };
            startupItem = new ToolStripMenuItem("Start with Windows");
            startupItem.Click += delegate { ToggleStartup(); };

            menu = new ContextMenuStrip { ImageScalingSize = new Size(iconSize, iconSize), ShowItemToolTips = true };
            menu.Items.AddRange(new ToolStripItem[]
            {
                statusItem,
                statsItem,
                sessionItem,
                new ToolStripSeparator(),
                new ToolStripMenuItem("Settings...", null, delegate { ShowSettings(); }),
                notifyItem,
                startupItem,
                new ToolStripSeparator(),
                new ToolStripMenuItem("Exit", null, delegate { ExitThread(); }),
            });
            menu.Opening += delegate
            {
                notifyItem.Checked = settings.NotifyOnChange;
                startupItem.Checked = StartupEnabled;
            };

            tray = new NotifyIcon
            {
                ContextMenuStrip = menu,
                Icon = IconFor(Status.Unknown, Status.Unknown),
                Text = Clip("Signal Quality - checking " + settings.Host),
                Visible = true,
            };
            tray.MouseUp += OnTrayMouseUp;

            timer = new System.Windows.Forms.Timer { Interval = settings.IntervalSeconds * 1000 };
            timer.Tick += delegate { Tick(); };
            timer.Start();
            Tick();
        }

        async void Tick()
        {
            if (busy) return;
            busy = true;
            int gen = generation;
            Sample sample;
            try
            {
                sample = await Prober.Check(settings.Url, settings.TimeoutMs);
            }
            finally
            {
                busy = false;
            }
            if (gen != generation) return;

            history.Add(sample, settings.WindowSeconds);
            sessionChecks++;
            if (!sample.Ok) sessionFailures++;
            Show(Classifier.Assess(history.Samples, settings));
        }

        void Show(Assessment a)
        {
            if (a.Count == 0) return;

            string latest = a.Last.Ok ? a.Last.Ms + " ms" : a.Last.Error;
            string avg = double.IsNaN(a.AvgMs) ? "-" : Math.Round(a.AvgMs) + " ms";
            string failed = Math.Round(a.FailPct) + "% failed";

            string trend = a.Trend == Trend.Better ? "improving" : a.Trend == Trend.Worse ? "worsening" : null;
            statusItem.Text = a.Headline + "  \u00B7  avg " + avg + (trend != null ? "  \u00B7  " + trend : "");
            statusItem.Image = DotFor(a.Status, a.LatestBand);
            statsItem.Text = string.Format("Last {0} s: {1} checks, {2}; latest {3}", settings.WindowSeconds, a.Count, failed, latest);
            double okPct = 100.0 * (sessionChecks - sessionFailures) / Math.Max(1, sessionChecks);
            sessionItem.Text = string.Format("Session: {0:N0} checks to {1}, {2:0.0}% OK", sessionChecks, settings.Host, okPct);

            string versus = a.Trend == Trend.Better ? " (better)" : a.Trend == Trend.Worse ? " (worse)" : "";
            tray.Text = Clip(a.Headline + ": avg " + avg + " over " + settings.WindowSeconds + " s\nLatest " + latest + versus + ", " + failed);

            if (a.Status != shown || a.LatestBand != shownLatest) tray.Icon = IconFor(a.Status, a.LatestBand);
            shownLatest = a.LatestBand;
            if (a.Status == shown) return;
            Status previous = shown;
            shown = a.Status;

            if (settings.NotifyOnChange && previous != Status.Unknown)
            {
                ToolTipIcon kind = shown == Status.Red ? ToolTipIcon.Error : shown == Status.Yellow ? ToolTipIcon.Warning : ToolTipIcon.Info;
                tray.ShowBalloonTip(4000, "Connection: " + a.Headline, "Average " + avg + ", latest " + latest + ", " + failed, kind);
            }
        }

        Icon IconFor(Status status, Status latest)
        {
            string key = status + "/" + latest;
            Icon icon;
            if (!icons.TryGetValue(key, out icon))
            {
                icon = IconFactory.Create(status, latest, iconSize);
                icons[key] = icon;
            }
            return icon;
        }

        Bitmap DotFor(Status status, Status latest)
        {
            string key = status + "/" + latest;
            Bitmap dot;
            if (!menuDots.TryGetValue(key, out dot))
            {
                dot = IconFactory.Render(status, latest, iconSize);
                menuDots[key] = dot;
            }
            return dot;
        }

        void OnTrayMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            // NotifyIcon only opens its menu on right-click; this private method is the
            // standard way to get the same correctly-positioned menu on left-click.
            MethodInfo show = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            if (show != null) show.Invoke(tray, null);
        }

        void ShowSettings()
        {
            if (openSettings != null)
            {
                openSettings.Activate();
                return;
            }
            using (openSettings = new SettingsForm(settings))
            {
                if (openSettings.ShowDialog() == DialogResult.OK) Apply(openSettings.Result);
            }
            openSettings = null;
        }

        void Apply(Settings next)
        {
            bool urlChanged = next.Url != settings.Url;
            next.NotifyOnChange = settings.NotifyOnChange;
            settings = next;
            settings.Save();

            timer.Interval = settings.IntervalSeconds * 1000;
            if (urlChanged)
            {
                generation++;
                history.Clear();
                sessionChecks = sessionFailures = 0;
                Tick();
                return;
            }
            Show(Classifier.Assess(history.Samples, settings));
        }

        static bool StartupEnabled
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(RunValue) != null;
            }
        }

        static void ToggleStartup()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key.GetValue(RunValue) != null) key.DeleteValue(RunValue, false);
                    else key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't change the startup setting:\n" + ex.Message, "Signal Quality", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        static string Clip(string s)
        {
            return s.Length <= TooltipMax ? s : s.Substring(0, TooltipMax);
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }

    sealed class SettingsForm : Form
    {
        readonly TextBox url = new TextBox { Width = 260 };
        readonly NumericUpDown interval = Number(1, 300);
        readonly NumericUpDown window = Number(1, 3600);
        readonly NumericUpDown green = Number(1, 30000);
        readonly NumericUpDown yellow = Number(2, 60000);
        readonly NumericUpDown timeout = Number(500, 30000);
        readonly Settings original;

        public Settings Result { get; private set; }

        public SettingsForm(Settings current)
        {
            original = current;
            Text = "Signal Quality Settings";
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            url.Text = current.Url;
            interval.Value = current.IntervalSeconds;
            window.Value = current.WindowSeconds;
            green.Value = current.GreenMaxMs;
            yellow.Value = current.YellowMaxMs;
            timeout.Value = current.TimeoutMs;

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            AddRow(grid, "Address to check:", url);
            AddRow(grid, "Check every (seconds):", interval);
            AddRow(grid, "Average over last (seconds):", window);
            AddRow(grid, "Green up to (ms):", green);
            AddRow(grid, "Yellow up to (ms):", yellow);
            AddRow(grid, "Give up after (ms):", timeout);

            var hint = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(420, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 10, 3, 6),
                Text = "Each check times one web request on an open connection (about one network round trip). "
                     + "The light shows red when the average is "
                     + "slower than the yellow limit, the last two checks failed, or half the recent checks failed. "
                     + "Any recent failure shows at least yellow.",
            };

            var ok = new Button { Text = "OK", AutoSize = true };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var reset = new Button { Text = "Defaults", AutoSize = true };
            ok.Click += OnOk;
            reset.Click += delegate
            {
                var d = new Settings();
                url.Text = d.Url;
                interval.Value = d.IntervalSeconds;
                window.Value = d.WindowSeconds;
                green.Value = d.GreenMaxMs;
                yellow.Value = d.YellowMaxMs;
                timeout.Value = d.TimeoutMs;
            };
            AcceptButton = ok;
            CancelButton = cancel;
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(reset);

            var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            layout.Controls.Add(grid);
            layout.Controls.Add(hint);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
        }

        static NumericUpDown Number(int min, int max)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Width = 90 };
        }

        static void AddRow(TableLayoutPanel grid, string label, Control input)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 6) });
            input.Anchor = AnchorStyles.Left;
            grid.Controls.Add(input);
        }

        void OnOk(object sender, EventArgs e)
        {
            string cleaned = Settings.CleanUrl(url.Text);
            if (cleaned == null)
            {
                MessageBox.Show(this, "Enter a web address such as " + Settings.DefaultUrl, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                url.Focus();
                return;
            }
            if (yellow.Value <= green.Value)
            {
                MessageBox.Show(this, "The yellow limit must be higher than the green limit.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                yellow.Focus();
                return;
            }
            if (window.Value < interval.Value)
            {
                MessageBox.Show(this, "The averaging window must be at least as long as the time between checks.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                window.Focus();
                return;
            }

            Settings s = original.Clone();
            s.Url = cleaned;
            s.IntervalSeconds = (int)interval.Value;
            s.WindowSeconds = (int)window.Value;
            s.GreenMaxMs = (int)green.Value;
            s.YellowMaxMs = (int)yellow.Value;
            s.TimeoutMs = (int)timeout.Value;
            s.Normalize();
            Result = s;
            DialogResult = DialogResult.OK;
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            bool firstInstance;
            using (new Mutex(true, @"Local\SignalQuality.Tray", out firstInstance))
            {
                if (!firstInstance)
                {
                    MessageBox.Show("Signal Quality is already running. Look for the coloured light in the system tray.", "Signal Quality");
                    return;
                }
                SetProcessDPIAware();   // crisp tray icons on high-DPI displays
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }
}
