// Claude Monitor: shows the 5-hour and weekly usage limits of one or more Claude accounts.
// Build with build.ps1 (uses the csc.exe bundled with .NET Framework 4.x, no SDK needed).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeMonitor
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

        public static int WM_SHOWME;
        public static float Scale = 1f;
        public static int D(float v) { return (int)Math.Round(v * Scale); }

        [STAThread]
        static void Main(string[] args)
        {
            WM_SHOWME = RegisterWindowMessage("ClaudeMonitor.ShowMe");
            bool created;
#if DEMO
            const string mutexName = "ClaudeMonitor.Demo";
#else
            const string mutexName = "ClaudeMonitor.SingleInstance";
#endif
            using (var mutex = new Mutex(true, mutexName, out created))
            {
                if (!created && Array.IndexOf(args, "--updated") >= 0)
                {
                    // Started by the updater: wait for the old version to exit.
                    try { created = mutex.WaitOne(15000); }
                    catch (AbandonedMutexException) { created = true; }
                }
                if (!created)
                {
                    // Already running: ask the existing instance to show its window.
                    PostMessage((IntPtr)0xffff, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                try { SetProcessDPIAware(); } catch { }
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
                Updater.Cleanup();
                Application.Run(new MainForm(Array.IndexOf(args, "--tray") >= 0));
                GC.KeepAlive(mutex);
            }
        }
    }

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(0x1F, 0x1E, 0x1D);
        public static readonly Color Card = Color.FromArgb(0x2C, 0x2B, 0x28);
        public static readonly Color CardHover = Color.FromArgb(0x35, 0x34, 0x30);
        public static readonly Color Track = Color.FromArgb(0x42, 0x40, 0x3B);
        public static readonly Color Text = Color.FromArgb(0xF5, 0xF4, 0xEE);
        public static readonly Color Muted = Color.FromArgb(0xA6, 0xA3, 0x9A);
        public static readonly Color Accent = Color.FromArgb(0xD9, 0x77, 0x57);
        public static readonly Color Danger = Color.FromArgb(0xE5, 0x5B, 0x4F);
        public static readonly Font Title = new Font("Segoe UI Semibold", 11f);
        public static readonly Font Name = new Font("Segoe UI Semibold", 10f);
        public static readonly Font Body = new Font("Segoe UI", 9f);
        public static readonly Font Pct = new Font("Segoe UI Semibold", 9.5f);
        public static readonly Font Small = new Font("Segoe UI", 8f);
        public static readonly Font Glyph = new Font("Segoe MDL2 Assets", 10f);

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Claude-style starburst logo.
        public static Bitmap Burst(int size, Color c)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            using (var pen = new Pen(c, Math.Max(1.6f, size * 0.12f)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                pen.StartCap = pen.EndCap = LineCap.Round;
                float cx = size / 2f, cy = size / 2f, inner = size * 0.06f;
                for (int i = 0; i < 10; i++)
                {
                    double a = i * Math.PI / 5 + 0.3;
                    float len = size * (i % 2 == 0 ? 0.44f : 0.36f);
                    g.DrawLine(pen,
                        cx + (float)Math.Cos(a) * inner, cy + (float)Math.Sin(a) * inner,
                        cx + (float)Math.Cos(a) * len, cy + (float)Math.Sin(a) * len);
                }
            }
            return bmp;
        }

        public static Icon BurstIcon(int size)
        {
            using (var b = Burst(size, Accent)) return Icon.FromHandle(b.GetHicon());
        }
    }

    class Limit
    {
        public string Label;
        public double Pct;
        public DateTimeOffset? Reset;
    }

    class Account
    {
        public string Email, AccessToken, RefreshToken;
        public long ExpiresAt; // unix ms
        public readonly object Sync = new object();
        // Runtime only, not persisted.
        public List<Limit> Limits = new List<Limit>();
        public string Error;
        public bool Loading;
        public DateTime? Updated;
    }

    static class Json
    {
        public static Dictionary<string, object> Parse(string s)
        {
            return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(s);
        }
        public static string Write(object o) { return new JavaScriptSerializer().Serialize(o); }
        public static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }
        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
        }
    }

    // Accounts are stored in %APPDATA%\ClaudeMonitor, encrypted with DPAPI (readable only by this Windows user).
    static class Store
    {
        public static readonly object Sync = new object();
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeMonitor");
        static readonly string FilePath = Path.Combine(Dir, "accounts.dat");

        public static List<Account> Load()
        {
            var list = new List<Account>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                byte[] raw = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
                var arr = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(Encoding.UTF8.GetString(raw));
                foreach (var d in arr)
                {
                    var a = new Account();
                    a.Email = Json.Str(d, "email");
                    a.AccessToken = Json.Str(d, "access_token");
                    a.RefreshToken = Json.Str(d, "refresh_token");
                    a.ExpiresAt = Convert.ToInt64(d["expires_at"], CultureInfo.InvariantCulture);
                    list.Add(a);
                }
            }
            catch { }
            return list;
        }

        public static void Save(List<Account> accounts)
        {
            lock (Sync)
            {
                var arr = new List<Dictionary<string, object>>();
                foreach (var a in accounts)
                    lock (a.Sync)
                        arr.Add(new Dictionary<string, object> {
                            { "email", a.Email }, { "access_token", a.AccessToken },
                            { "refresh_token", a.RefreshToken }, { "expires_at", a.ExpiresAt } });
                byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(Json.Write(arr)), null, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
        }
    }

    // Window preferences, stored in plain JSON next to the accounts file.
    static class Settings
    {
        static readonly string FilePath = Path.Combine(Store.Dir, "settings.json");
        public static bool TopMost;
        public static bool StartInTray = true;

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var d = Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                TopMost = Json.Str(d, "top_most") == "True";
                StartInTray = Json.Str(d, "start_in_tray") != "False";
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Store.Dir);
                File.WriteAllText(FilePath, Json.Write(new Dictionary<string, object> {
                    { "top_most", TopMost }, { "start_in_tray", StartInTray } }), Encoding.UTF8);
            }
            catch { }
        }
    }

    // Same OAuth client and endpoints used by Claude Code ("Login with Claude account").
    static class Api
    {
        const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        const string AuthorizeUrl = "https://claude.com/cai/oauth/authorize";
        const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
        const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
        const string Scopes = "user:profile user:inference";

        static long NowMs() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

        static string B64Url(byte[] b)
        {
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        static byte[] Random(int n)
        {
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        internal static string Http(string method, string url, string body, string bearer, out int status)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 20000;
            req.UserAgent = "claude-monitor/" + AppInfo.Version;
            req.Accept = "application/json";
            if (bearer != null)
            {
                req.Headers["Authorization"] = "Bearer " + bearer;
                req.Headers["anthropic-beta"] = "oauth-2025-04-20";
            }
            if (body != null)
            {
                req.ContentType = "application/json";
                byte[] b = Encoding.UTF8.GetBytes(body);
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex)
            {
                resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new Exception("Rete non raggiungibile: " + ex.Message);
            }
            using (resp)
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                status = (int)resp.StatusCode;
                return r.ReadToEnd();
            }
        }

        static void ApplyToken(Account a, Dictionary<string, object> t)
        {
            lock (a.Sync)
            {
                a.AccessToken = Json.Str(t, "access_token");
                a.RefreshToken = Json.Str(t, "refresh_token") ?? a.RefreshToken;
                string exp = Json.Str(t, "expires_in");
                a.ExpiresAt = NowMs() + (exp != null ? long.Parse(exp, CultureInfo.InvariantCulture) : 3600) * 1000;
                string email = Json.Str(Json.Obj(t, "account"), "email_address");
                if (email != null) a.Email = email;
            }
        }

        // Opens the browser on the Claude login page and waits for the redirect on a localhost port.
        public static Account Login()
        {
            string verifier = B64Url(Random(32));
            string challenge;
            using (var sha = SHA256.Create()) challenge = B64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            string state = B64Url(Random(32));

            var l4 = new TcpListener(IPAddress.Loopback, 0);
            l4.Start();
            int port = ((IPEndPoint)l4.LocalEndpoint).Port;
            TcpListener l6 = null;
            try { l6 = new TcpListener(IPAddress.IPv6Loopback, port); l6.Start(); } catch { l6 = null; }
            string redirect = "http://localhost:" + port + "/callback";
            string code = null;
            try
            {
                string url = AuthorizeUrl + "?code=true&client_id=" + ClientId + "&response_type=code" +
                    "&redirect_uri=" + Uri.EscapeDataString(redirect) + "&scope=" + Uri.EscapeDataString(Scopes) +
                    "&code_challenge=" + challenge + "&code_challenge_method=S256&state=" + state;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                DateTime deadline = DateTime.UtcNow.AddMinutes(5);
                while (code == null)
                {
                    if (DateTime.UtcNow > deadline) throw new Exception("Login scaduto (5 minuti).");
                    TcpClient c = null;
                    if (l4.Pending()) c = l4.AcceptTcpClient();
                    else if (l6 != null && l6.Pending()) c = l6.AcceptTcpClient();
                    if (c == null) { Thread.Sleep(100); continue; }
                    using (c) code = HandleCallback(c, state);
                }
            }
            finally
            {
                l4.Stop();
                if (l6 != null) l6.Stop();
            }

            var body = new Dictionary<string, object> {
                { "grant_type", "authorization_code" }, { "code", code }, { "redirect_uri", redirect },
                { "client_id", ClientId }, { "code_verifier", verifier }, { "state", state } };
            int st;
            string res = Http("POST", TokenUrl, Json.Write(body), null, out st);
            if (st != 200) throw new Exception("Scambio token fallito (HTTP " + st + ").");
            var a = new Account();
            ApplyToken(a, Json.Parse(res));
            if (a.Email == null) a.Email = "Account";
            return a;
        }

        static string HandleCallback(TcpClient c, string expectedState)
        {
            c.ReceiveTimeout = 5000;
            var stream = c.GetStream();
            string line = new StreamReader(stream, Encoding.ASCII).ReadLine() ?? "";
            string[] parts = line.Split(' ');
            string target = parts.Length > 1 ? parts[1] : "";
            int q = target.IndexOf('?');
            string path = q >= 0 ? target.Substring(0, q) : target;
            if (path != "/callback")
            {
                Respond(stream, "404 Not Found", "");
                return null;
            }
            var query = new Dictionary<string, string>();
            if (q >= 0)
                foreach (string kv in target.Substring(q + 1).Split('&'))
                {
                    int e = kv.IndexOf('=');
                    if (e > 0) query[kv.Substring(0, e)] = Uri.UnescapeDataString(kv.Substring(e + 1).Replace('+', ' '));
                }
            string code, state, error;
            query.TryGetValue("code", out code);
            query.TryGetValue("state", out state);
            query.TryGetValue("error", out error);
            bool ok = error == null && code != null && state == expectedState;
            Respond(stream, "200 OK",
                "<html><body style=\"background:#1F1E1D;color:#F5F4EE;font-family:Segoe UI;text-align:center;padding-top:80px\">" +
                (ok ? "<h2 style=\"color:#D97757\">Account collegato</h2><p>Puoi chiudere questa scheda.</p>"
                    : "<h2 style=\"color:#E55B4F\">Login non riuscito</h2>") + "</body></html>");
            if (!ok) throw new Exception(error != null ? "Login rifiutato: " + error : "Risposta di login non valida.");
            return code;
        }

        static void Respond(NetworkStream s, string status, string html)
        {
            byte[] b = Encoding.UTF8.GetBytes(html);
            byte[] h = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " +
                b.Length + "\r\nConnection: close\r\n\r\n");
            s.Write(h, 0, h.Length);
            s.Write(b, 0, b.Length);
        }

        static void Refresh(Account a)
        {
            string rt;
            lock (a.Sync) rt = a.RefreshToken;
            var body = new Dictionary<string, object> {
                { "grant_type", "refresh_token" }, { "refresh_token", rt },
                { "client_id", ClientId }, { "scope", Scopes } };
            int st;
            string res = Http("POST", TokenUrl, Json.Write(body), null, out st);
            if (st != 200) throw new Exception("Sessione scaduta: rimuovi e ricollega l'account.");
            ApplyToken(a, Json.Parse(res));
        }

        // Returns the account limits. tokenChanged is true when the tokens were refreshed and must be saved.
        public static List<Limit> Usage(Account a, out bool tokenChanged)
        {
            tokenChanged = false;
            long exp;
            lock (a.Sync) exp = a.ExpiresAt;
            if (exp - NowMs() < 120000) { Refresh(a); tokenChanged = true; }

            int st;
            string at;
            lock (a.Sync) at = a.AccessToken;
            string res = Http("GET", UsageUrl, null, at, out st);
            if (st == 401 && !tokenChanged)
            {
                Refresh(a);
                tokenChanged = true;
                lock (a.Sync) at = a.AccessToken;
                res = Http("GET", UsageUrl, null, at, out st);
            }
            if (st == 429) throw new Exception("Troppe richieste, riprovo al prossimo giro.");
            if (st != 200) throw new Exception("Errore HTTP " + st + ".");

            var d = Json.Parse(res);
            var list = new List<Limit>();
            AddLimit(list, d, "five_hour", "Sessione (5 ore)", true);
            AddLimit(list, d, "seven_day", "Settimana", true);
            AddLimit(list, d, "seven_day_sonnet", "Settimana · Sonnet", false);
            AddLimit(list, d, "seven_day_opus", "Settimana · Opus", false);
            return list;
        }

        static void AddLimit(List<Limit> list, Dictionary<string, object> d, string key, string label, bool always)
        {
            var o = Json.Obj(d, key);
            string u = Json.Str(o, "utilization");
            if (u == null && !always) return;
            var l = new Limit { Label = label };
            if (u != null) l.Pct = double.Parse(u, CultureInfo.InvariantCulture);
            string r = Json.Str(o, "resets_at");
            DateTimeOffset reset;
            if (r != null && DateTimeOffset.TryParse(r, CultureInfo.InvariantCulture, DateTimeStyles.None, out reset)) l.Reset = reset;
            list.Add(l);
        }
    }

    // Checks the latest GitHub release and replaces the running exe with the published one.
    static class Updater
    {
        public const string Repo = "carellice/claude-monitor";
        const string AssetName = "ClaudeMonitor.exe";

        public class Release
        {
            public string Version, Url;
        }

        // Returns the latest release when it is newer than this build, otherwise null.
        public static Release Check()
        {
            int st;
            string res = Api.Http("GET", "https://api.github.com/repos/" + Repo + "/releases/latest", null, null, out st);
            if (st != 200) throw new Exception("GitHub HTTP " + st);
            var d = Json.Parse(res);
            string tag = (Json.Str(d, "tag_name") ?? "").TrimStart('v');
            Version latest, current;
            if (!System.Version.TryParse(tag, out latest) || !System.Version.TryParse(AppInfo.Version, out current) || latest <= current)
                return null;
            object assets;
            if (d.TryGetValue("assets", out assets) && assets is System.Collections.ArrayList)
                foreach (object o in (System.Collections.ArrayList)assets)
                {
                    var a = o as Dictionary<string, object>;
                    if (Json.Str(a, "name") == AssetName)
                        return new Release { Version = tag, Url = Json.Str(a, "browser_download_url") };
                }
            return null;
        }

        // A running exe cannot be overwritten but can be renamed: swap the files, then start the new one.
        public static void Install(Release r)
        {
            string exe = Application.ExecutablePath, fresh = exe + ".new", old = exe + ".old";
            var req = (HttpWebRequest)WebRequest.Create(r.Url);
            req.UserAgent = "claude-monitor/" + AppInfo.Version;
            req.Timeout = 60000;
            using (var resp = req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(fresh))
                src.CopyTo(dst);
            if (new FileInfo(fresh).Length < 10000) throw new Exception("Download non valido.");
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(fresh, exe); }
            catch { File.Move(old, exe); throw; }
            Process.Start(exe, "--updated");
        }

        public static void Cleanup()
        {
            try { File.Delete(Application.ExecutablePath + ".old"); } catch { }
            try { File.Delete(Application.ExecutablePath + ".new"); } catch { }
        }
    }

    class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color MenuBorder { get { return Theme.Track; } }
        public override Color MenuItemBorder { get { return Theme.CardHover; } }
        public override Color MenuItemSelected { get { return Theme.CardHover; } }
        public override Color SeparatorDark { get { return Theme.Track; } }
        public override Color SeparatorLight { get { return Theme.Card; } }
        public override Color CheckBackground { get { return Theme.CardHover; } }
        public override Color CheckSelectedBackground { get { return Theme.Track; } }
    }

    class AccountCard : Control
    {
        public readonly Account Account;
        static readonly CultureInfo It = new CultureInfo("it-IT");

        public AccountCard(Account a)
        {
            Account = a;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        public int Measure()
        {
            int n = Math.Max(Account.Limits.Count, 2);
            return Program.D(44 + n * 50 + (Account.Error != null ? 20 : 0) + 4);
        }

        static string FormatReset(Limit l)
        {
            DateTimeOffset? reset = l.Reset;
            if (reset == null) return l.Label.StartsWith("Sessione") ? "Nessuna sessione attiva" : "";
            TimeSpan left = reset.Value - DateTimeOffset.Now;
            if (left.TotalSeconds <= 0) return "Reset in corso";
            if (left.TotalHours < 24)
                return "Reset tra " + (left.Hours > 0 ? left.Hours + " h " : "") + left.Minutes + " min";
            return "Reset " + reset.Value.ToLocalTime().ToString("ddd d MMM, HH:mm", It);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            using (var p = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Program.D(10)))
            using (var b = new SolidBrush(Theme.Card))
                g.FillPath(b, p);

            int x = Program.D(16), w = Width - Program.D(32), y = Program.D(12);
            var right = TextFormatFlags.Right | TextFormatFlags.NoPadding;
            var left = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

            string meta = Account.Loading ? "aggiorno…" : Account.Updated.HasValue ? Account.Updated.Value.ToString("HH:mm") : "";
            TextRenderer.DrawText(g, meta, Theme.Small, new Rectangle(x, y + Program.D(3), w, Program.D(18)), Theme.Muted, right);
            TextRenderer.DrawText(g, Account.Email, Theme.Name, new Rectangle(x, y, w - Program.D(70), Program.D(22)), Theme.Text, left);
            y += Program.D(32);

            var limits = Account.Limits;
            if (limits.Count == 0)
                limits = new List<Limit> { new Limit { Label = "Sessione (5 ore)" }, new Limit { Label = "Settimana" } };
            bool noData = Account.Limits.Count == 0;

            foreach (var l in limits)
            {
                TextRenderer.DrawText(g, l.Label, Theme.Body, new Rectangle(x, y, w, Program.D(18)), Theme.Text, left);
                TextRenderer.DrawText(g, noData ? "—" : Math.Round(l.Pct) + "%", Theme.Pct,
                    new Rectangle(x, y - Program.D(1), w, Program.D(20)), l.Pct >= 90 ? Theme.Danger : Theme.Text, right);
                y += Program.D(22);

                var track = new RectangleF(x, y, w, Program.D(6));
                using (var p = Theme.Round(track, Program.D(3)))
                {
                    using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, p);
                    float fill = (float)(w * Math.Max(0, Math.Min(100, l.Pct)) / 100);
                    if (fill > 0)
                    {
                        g.SetClip(p);
                        using (var b = new SolidBrush(l.Pct >= 90 ? Theme.Danger : Theme.Accent))
                            g.FillRectangle(b, x, y, fill, track.Height);
                        g.ResetClip();
                    }
                }
                y += Program.D(10);
                TextRenderer.DrawText(g, noData ? "" : FormatReset(l), Theme.Small,
                    new Rectangle(x, y, w, Program.D(16)), Theme.Muted, left);
                y += Program.D(18);
            }

            if (Account.Error != null)
                TextRenderer.DrawText(g, Account.Error, Theme.Small, new Rectangle(x, y, w, Program.D(18)), Theme.Danger, left);
        }
    }

    class MainForm : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ClaudeMonitor";
        const int Width0 = 360, Header = 52, Footer = 30, Gap = 10, Pad = 14;

        readonly List<Account> accounts;
        readonly List<AccountCard> cards = new List<AccountCard>();
        readonly Button addButton, refreshButton, settingsButton;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip cardMenu, settingsMenu;
        readonly System.Windows.Forms.Timer pollTimer, tickTimer;
        readonly Bitmap logo;
        readonly ToolStripMenuItem trayUpdateItem;
        bool allowShow, exiting, hintShown, loggingIn, updating;
        Updater.Release update;

        public MainForm(bool startHidden)
        {
            allowShow = !startHidden;
            Settings.Load();
            TopMost = Settings.TopMost;
            Text = "Claude Monitor";
            Icon = Theme.BurstIcon(Program.D(32));
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            logo = Theme.Burst(Program.D(22), Theme.Accent);

            addButton = MakeButton("", "Aggiungi account");
            addButton.Click += delegate { AddAccount(); };
            refreshButton = MakeButton("", "Aggiorna ora");
            refreshButton.Click += delegate { RefreshAll(); };
            settingsButton = MakeButton("", "Impostazioni");

            var renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            settingsMenu = new ContextMenuStrip { Renderer = renderer };
            AddSettingsItems(settingsMenu);
            settingsMenu.Items.Add(new ToolStripSeparator());
            settingsMenu.Items.Add("Controlla aggiornamenti", null, delegate { CheckUpdates(true); });
            settingsMenu.Items.Add(new ToolStripMenuItem("Versione " + AppInfo.Version) { Enabled = false });
            StyleMenu(settingsMenu);
            settingsButton.Click += delegate { settingsMenu.Show(settingsButton, new Point(0, settingsButton.Height)); };

            cardMenu = new ContextMenuStrip { Renderer = renderer };
            cardMenu.Items.Add("Aggiorna", null, delegate { var c = MenuCard(); if (c != null) RefreshOne(c.Account); });
            cardMenu.Items.Add("Rimuovi account", null, delegate { var c = MenuCard(); if (c != null) RemoveAccount(c.Account); });
            StyleMenu(cardMenu);

            var trayMenu = new ContextMenuStrip { Renderer = renderer };
            trayMenu.Items.Add("Apri", null, delegate { ShowWindow(); });
            trayMenu.Items.Add("Aggiorna", null, delegate { RefreshAll(); });
            trayUpdateItem = new ToolStripMenuItem("", null, delegate { InstallUpdate(); }) { Visible = false };
            trayMenu.Items.Add(trayUpdateItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            AddSettingsItems(trayMenu);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Esci", null, delegate { exiting = true; Close(); });
            StyleMenu(trayMenu);

            tray = new NotifyIcon { Icon = Theme.BurstIcon(SystemInformation.SmallIconSize.Width), Text = "Claude Monitor", ContextMenuStrip = trayMenu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) { if (Visible) Hide(); else ShowWindow(); } };

            accounts = Store.Load();
#if DEMO
            accounts.Clear();
            accounts.Add(new Account { Email = "mario.rossi@gmail.com", Updated = DateTime.Now, Limits = new List<Limit> {
                new Limit { Label = "Sessione (5 ore)", Pct = 42, Reset = DateTimeOffset.Now.AddMinutes(133) },
                new Limit { Label = "Settimana", Pct = 18, Reset = DateTimeOffset.Now.AddDays(4) } } });
            accounts.Add(new Account { Email = "lavoro@example.com", Updated = DateTime.Now, Limits = new List<Limit> {
                new Limit { Label = "Sessione (5 ore)", Pct = 94, Reset = DateTimeOffset.Now.AddMinutes(41) },
                new Limit { Label = "Settimana", Pct = 63, Reset = DateTimeOffset.Now.AddDays(2) },
                new Limit { Label = "Settimana · Sonnet", Pct = 7 } } });
#endif
            foreach (var a in accounts) AddCard(a);
            Relayout();
            PlaceNearTray();

            pollTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
            pollTimer.Tick += delegate { RefreshAll(); };
            pollTimer.Start();
            tickTimer = new System.Windows.Forms.Timer { Interval = 30 * 1000 };
            tickTimer.Tick += delegate { foreach (var c in cards) c.Invalidate(); };
            tickTimer.Start();

            var updateTimer = new System.Windows.Forms.Timer { Interval = 6 * 3600 * 1000 };
            updateTimer.Tick += delegate { CheckUpdates(false); };
            updateTimer.Start();

#if !DEMO
            RefreshAll();
            CheckUpdates(false);
#endif
        }

        Rectangle FooterRect()
        {
            return new Rectangle(0, ClientSize.Height - Program.D(Footer) + Program.D(6), ClientSize.Width, Program.D(18));
        }

        void CheckUpdates(bool manual)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                Updater.Release r = null;
                string error = null;
                try { r = Updater.Check(); }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        update = r;
                        trayUpdateItem.Text = r != null ? "Aggiorna alla versione " + r.Version : "";
                        trayUpdateItem.Visible = r != null;
                        Invalidate();
                        if (manual && r == null)
                            MessageBox.Show(this, error != null ? "Controllo non riuscito: " + error : "Hai già l'ultima versione (" + AppInfo.Version + ").",
                                "Claude Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        else if (manual) InstallUpdate();
                    });
                }
                catch { }
            });
        }

        void InstallUpdate()
        {
            if (update == null || updating) return;
            if (MessageBox.Show(this, "Scaricare e installare la versione " + update.Version + "?\nL'app si riavvierà.", "Claude Monitor",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            updating = true;
            Invalidate();
            var r = update;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try { Updater.Install(r); }
                catch (Exception ex) { error = ex.Message; }
                BeginInvoke((MethodInvoker)delegate
                {
                    if (error == null) { exiting = true; Close(); return; }
                    updating = false;
                    Invalidate();
                    MessageBox.Show(this, "Aggiornamento non riuscito: " + error, "Claude Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            });
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = update != null && FooterRect().Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (update != null && FooterRect().Contains(e.Location)) InstallUpdate();
        }

        // Same three options in the gear menu and in the tray menu; check marks are re-read on every opening.
        void AddSettingsItems(ContextMenuStrip m)
        {
            var top = new ToolStripMenuItem("Sempre in primo piano") { CheckOnClick = true };
            top.Click += delegate { Settings.TopMost = top.Checked; TopMost = top.Checked; Settings.Save(); };
            var auto = new ToolStripMenuItem("Avvia con Windows") { CheckOnClick = true };
            auto.Click += delegate { SetAutostart(auto.Checked); };
            var hidden = new ToolStripMenuItem("All'avvio con Windows resta nel tray") { CheckOnClick = true };
            hidden.Click += delegate
            {
                Settings.StartInTray = hidden.Checked;
                Settings.Save();
                if (IsAutostart()) SetAutostart(true);
            };
            m.Items.Add(top);
            m.Items.Add(auto);
            m.Items.Add(hidden);
            m.Opening += delegate
            {
                top.Checked = Settings.TopMost;
                auto.Checked = IsAutostart();
                hidden.Checked = Settings.StartInTray;
            };
        }

        static void StyleMenu(ContextMenuStrip m)
        {
            m.Font = Theme.Body;
            foreach (ToolStripItem i in m.Items) i.ForeColor = Theme.Text;
        }

        Button MakeButton(string glyph, string tip)
        {
            var b = new Button
            {
                Text = glyph, Font = Theme.Glyph, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text,
                BackColor = Theme.Bg, Size = new Size(Program.D(34), Program.D(30)), TabStop = false, Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.CardHover;
            b.FlatAppearance.MouseDownBackColor = Theme.Track;
            new ToolTip().SetToolTip(b, tip);
            Controls.Add(b);
            return b;
        }

        AccountCard MenuCard() { return cardMenu.SourceControl as AccountCard; }

        void AddCard(Account a)
        {
            var c = new AccountCard(a) { ContextMenuStrip = cardMenu };
            cards.Add(c);
            Controls.Add(c);
        }

        void Relayout()
        {
            int x = Program.D(Pad), w = Program.D(Width0 - 2 * Pad), y = Program.D(Header);
            foreach (var c in cards)
            {
                int h = c.Measure();
                c.SetBounds(x, y, w, h);
                c.Invalidate();
                y += h + Program.D(Gap);
            }
            if (cards.Count == 0) y += Program.D(60);
            ClientSize = new Size(Program.D(Width0), y + Program.D(Footer) - Program.D(Gap));
            refreshButton.Location = new Point(ClientSize.Width - Program.D(Pad) - refreshButton.Width, Program.D(11));
            addButton.Location = new Point(refreshButton.Left - addButton.Width - Program.D(2), Program.D(11));
            settingsButton.Location = new Point(addButton.Left - settingsButton.Width - Program.D(2), Program.D(11));
            Invalidate();
            UpdateTray();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.DrawImage(logo, Program.D(Pad), Program.D(15));
            TextRenderer.DrawText(g, "Claude Usage", Theme.Title, new Point(Program.D(Pad + 28), Program.D(14)), Theme.Text, TextFormatFlags.NoPadding);
            if (cards.Count == 0)
                TextRenderer.DrawText(g, "Nessun account collegato.\nPremi + per accedere con il tuo account Claude.", Theme.Body,
                    new Rectangle(0, Program.D(Header), ClientSize.Width, Program.D(50)), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            string footer = updating ? "Scarico l'aggiornamento…"
                : update != null ? "Nuova versione " + update.Version + " disponibile · clic per aggiornare"
                : "Aggiornamento ogni 5 min · tasto destro per le opzioni";
            TextRenderer.DrawText(g, footer, update != null ? Theme.Pct : Theme.Small, FooterRect(),
                update != null ? Theme.Accent : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        }

        void RefreshAll()
        {
            List<Account> snapshot;
            lock (Store.Sync) snapshot = new List<Account>(accounts);
            foreach (var a in snapshot) RefreshOne(a);
        }

        void RefreshOne(Account a)
        {
            if (a.Loading) return;
            a.Loading = true;
            Repaint(a);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    bool changed;
                    a.Limits = Api.Usage(a, out changed);
                    a.Error = null;
                    a.Updated = DateTime.Now;
                    if (changed) SaveAccounts();
                }
                catch (Exception ex) { a.Error = ex.Message; }
                a.Loading = false;
                try { BeginInvoke((MethodInvoker)Relayout); } catch { }
            });
        }

        void Repaint(Account a)
        {
            foreach (var c in cards) if (c.Account == a) c.Invalidate();
        }

        void SaveAccounts()
        {
            List<Account> snapshot;
            lock (Store.Sync) snapshot = new List<Account>(accounts);
            try { Store.Save(snapshot); } catch { }
        }

        void AddAccount()
        {
            if (loggingIn) return;
            loggingIn = true;
            addButton.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Account result = null;
                string error = null;
                try { result = Api.Login(); }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        loggingIn = false;
                        addButton.Enabled = true;
                        if (error != null) { MessageBox.Show(this, error, "Claude Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                        Account target = null;
                        lock (Store.Sync)
                        {
                            foreach (var a in accounts)
                                if (string.Equals(a.Email, result.Email, StringComparison.OrdinalIgnoreCase)) target = a;
                            if (target == null) { accounts.Add(result); target = result; AddCard(result); }
                            else lock (target.Sync)
                            {
                                target.AccessToken = result.AccessToken;
                                target.RefreshToken = result.RefreshToken;
                                target.ExpiresAt = result.ExpiresAt;
                            }
                        }
                        SaveAccounts();
                        Relayout();
                        ShowWindow();
                        RefreshOne(target);
                    });
                }
                catch { }
            });
        }

        void RemoveAccount(Account a)
        {
            if (MessageBox.Show(this, "Rimuovere " + a.Email + " da Claude Monitor?", "Claude Monitor",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            lock (Store.Sync) accounts.Remove(a);
            var card = cards.Find(c => c.Account == a);
            if (card != null) { cards.Remove(card); Controls.Remove(card); card.Dispose(); }
            SaveAccounts();
            Relayout();
        }

        void UpdateTray()
        {
            var sb = new StringBuilder();
            foreach (var c in cards)
            {
                var a = c.Account;
                string name = (a.Email ?? "").Split('@')[0];
                if (name.Length > 12) name = name.Substring(0, 12);
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(name);
                if (a.Limits.Count >= 2)
                    sb.Append(": 5h ").Append(Math.Round(a.Limits[0].Pct)).Append("% · 7g ").Append(Math.Round(a.Limits[1].Pct)).Append('%');
            }
            string t = sb.Length > 0 ? sb.ToString() : "Claude Monitor";
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void PlaceNearTray()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - Program.D(12), wa.Bottom - Height - Program.D(12));
        }

        void ShowWindow()
        {
            allowShow = true;
            if (!Visible) PlaceNearTray();
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        static bool IsAutostart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null;
        }

        static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"" + (Settings.StartInTray ? " --tray" : ""));
                else k.DeleteValue(RunName, false);
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!allowShow)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1, caption = Theme.Bg.R | Theme.Bg.G << 8 | Theme.Bg.B << 16;
            try
            {
                DwmSetWindowAttribute(Handle, 20, ref on, 4);       // dark title bar
                DwmSetWindowAttribute(Handle, 35, ref caption, 4);  // Windows 11 caption color
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.WM_SHOWME) ShowWindow();
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!hintShown)
                {
                    hintShown = true;
                    tray.ShowBalloonTip(3000, "Claude Monitor", "Resta attivo nell'area di notifica. Tasto destro sull'icona per uscire.", ToolTipIcon.None);
                }
                return;
            }
            tray.Visible = false;
            base.OnFormClosing(e);
        }
    }
}
