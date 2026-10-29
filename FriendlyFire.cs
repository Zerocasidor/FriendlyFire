// Friendly Fire: tray app for two friends. Each keeps a task list; ticking a task or pressing "เสร็จแล้ว!"
// pops up on the other's PC (corner box + sound + reaction buttons). Messages go through ntfy.sh on a private,
// random topic made from the invite code. The app updates itself from the GitHub releases of Repo.
// Build: build.ps1 (the C# compiler that comes with Windows). Two copies on one PC for testing:
//   FriendlyFire.exe -Data <folder> [-Name <name> -Code <invite code>]
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;
using Microsoft.Win32;

namespace FriendlyFire
{
    public class TaskItem
    {
        public string id;
        public string text;
        public bool done;
        public long doneAt;   // unix seconds
        public long seenAt;   // this PC only: when a finished task was seen here; it goes away 24 h later
        public bool offer;    // given by the friend, waiting for ✓ (take it) or ✗ (throw it away)
        public override string ToString() { return text; }
    }

    public class Config
    {
        public string name = "";
        public string clientId = "";
        public string code = "";              // invite code; the ntfy topic is made from it
        public string friendName = "";
        public bool startWithWindows = true;
        public long lastSeen;                 // ntfy time of the newest message already shown
        public long friendTasksTime;          // Msg.time of the friend's list we have
        public List<TaskItem> tasks = new List<TaskItem>();
        public List<TaskItem> friendTasks = new List<TaskItem>();
        public List<string> friendHidden = new List<string>();   // friend's tasks removed on this PC (don't bring them back)
        public List<TaskItem> giving = new List<TaskItem>();     // my tasks dragged to the friend, sent until their list shows them
        public List<string> giftsDone = new List<string>();      // gifts from the friend already taken or thrown away here
        public bool notifyAll = true;                            // pop-ups at all
        public bool notifySound = true;
        public List<string> notifyOff = new List<string> { "accept", "reject" };   // kinds turned off in Settings → ขั้นสูง
        public List<string> reactions = new List<string> { "🔥", "😎", "👍", "❓" };   // the 4 reaction buttons, chosen in Settings
        public string answerMode = "react";                      // on a friend's pop-up: react (emoji buttons) | reply (type an answer) | none
        public int notifyExtra;                                  // pop-ups stay 1–8 s by text length plus this many seconds, at least 1 s (Notice.Forever = until closed)
        public bool stayFixed;                                   // notifyExtra moved from -1 = until closed to Notice.Forever (typed negatives are allowed now)
        public string blockedVersion = "";                       // an update Windows would not run (Smart App Control): not tried again on its own
        public string themeMode = "light";                       // light | dark | system (Settings → หน้าตา)
        public string themeColor = "orange";                     // accent: orange blue purple pink mint graphite
        public string notifyPlace = "br";                        // corner/edge: br bl tr tl bc tc
        public int notifySize = 1;                               // 0 small, 1 medium, 2 large
        public int doneKeepHours = 24;                           // finished tasks go away this long after they were seen here (0 = never)
        public long notifyMuteUntil;                             // unix s: all pop-ups paused until then (🔕 on a pop-up)
    }

    // One message on the topic. Every message carries the sender's task list, so the other side is always up to date.
    public class Msg
    {
        public string from;    // clientId
        public string name;
        public string type;    // sync | done | react | reply | give | accept | reject
        public string text;
        public long time;      // unix ms, newer lists replace older ones
        public List<TaskItem> tasks;
        public List<TaskItem> gifts;   // tasks given to the receiver (sent again until they show up in the receiver's list)
        public List<string> got;       // ids of gifts the sender has already handled
        public string ver;             // sender's app version: a newer one makes the receiver update right away
    }

    static class Util
    {
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static long NowSec() { return (long)(DateTime.UtcNow - Epoch).TotalSeconds; }
        public static long NowMs() { return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds; }
        public static DateTime Local(long sec) { return Epoch.AddSeconds(sec).ToLocalTime(); }

        public static string RandomCode(int n)
        {
            const string abc = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O or 1/I to mix up
            var bytes = new byte[n];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            var sb = new StringBuilder();
            foreach (byte b in bytes) sb.Append(abc[b % abc.Length]);
            return sb.ToString();
        }

        public static Font UiFont(float size) { return new Font("Segoe UI", size); }
        public static Font EmojiFont(float size) { return new Font("Segoe UI Emoji", size); }
    }

    // ---------- talking to the friend through ntfy.sh ----------

    class Relay
    {
        public const string Server = "https://ntfy.sh/";
        public event Action<Msg, string, long> Received;   // message, ntfy id, ntfy time (s); called on a background thread
        public event Action<bool> Connected;

        readonly string topic, clientId;
        readonly HashSet<string> seen = new HashSet<string>();
        readonly BlockingCollection<Msg> outbox = new BlockingCollection<Msg>();
        volatile bool stop;

        public Relay(string topic, string clientId) { this.topic = topic; this.clientId = clientId; }

        public void Start()
        {
            new Thread(Listen) { IsBackground = true }.Start();
            new Thread(Send) { IsBackground = true }.Start();
        }

        public void Stop() { stop = true; }

        public void Publish(Msg m) { outbox.Add(m); }

        // Keeps a stream open; on reconnect asks for everything since the last message, so nothing is missed
        void Listen()
        {
            string since = "12h";
            while (!stop)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(Server + topic + "/json?since=" + since);
                    req.UserAgent = "FriendlyFire/" + Program.Version;
                    req.Timeout = 30000;
                    req.ReadWriteTimeout = 100000;   // ntfy sends a keepalive every ~45 s; silence = dead connection (e.g. after sleep)
                    using (var resp = req.GetResponse())
                    using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        if (Connected != null) Connected(true);
                        string line;
                        while (!stop && (line = r.ReadLine()) != null)
                        {
                            var ev = Util.Json.Deserialize<Dictionary<string, object>>(line);
                            if (ev == null || !"message".Equals(ev["event"]) || !ev.ContainsKey("message")) continue;
                            string id = (string)ev["id"];
                            since = id;
                            if (!seen.Add(id)) continue;
                            Msg m;
                            try { m = Util.Json.Deserialize<Msg>((string)ev["message"]); }
                            catch { continue; }   // not ours
                            if (m == null || m.from == null || m.from == clientId) continue;
                            if (Received != null) Received(m, id, Convert.ToInt64(ev["time"], CultureInfo.InvariantCulture));
                        }
                    }
                }
                catch { }
                if (Connected != null) Connected(false);
                if (!stop) Thread.Sleep(5000);
            }
        }

        // One sender thread keeps messages in order; retries while offline
        void Send()
        {
            foreach (Msg m in outbox.GetConsumingEnumerable())
            {
                byte[] body = Fit(m);
                for (int attempt = 0; attempt < 60 && !stop; attempt++)
                {
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(Server + topic);
                        req.Method = "POST";
                        req.UserAgent = "FriendlyFire/" + Program.Version;
                        req.ContentType = "text/plain; charset=utf-8";
                        req.Timeout = 20000;
                        using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                        using (req.GetResponse()) { }
                        break;
                    }
                    catch { Thread.Sleep(10000); }
                }
            }
        }

        // ntfy turns messages over 4 KB into attachments: drop the oldest tasks until it fits
        static byte[] Fit(Msg m)
        {
            while (true)
            {
                byte[] b = Encoding.UTF8.GetBytes(Util.Json.Serialize(m));
                if (b.Length <= 3800 || m.tasks == null || m.tasks.Count == 0) return b;
                m.tasks.RemoveAt(0);
            }
        }
    }

    // ---------- updates from GitHub releases ----------

    static class Updater
    {
        public const string Repo = "Zerocasidor/FriendlyFire";

        // Downloads a newer FriendlyFire.exe next to this one (as FriendlyFire.new.exe) and returns its path, or null.
        // skip: a version not to download again (one Windows blocked before)
        public static string Download(string skip, out string version)
        {
            version = null;
            var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
            req.UserAgent = "FriendlyFire/" + Program.Version;
            req.Accept = "application/vnd.github+json";
            req.Timeout = 20000;
            string json;
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) json = r.ReadToEnd();
            var rel = Util.Json.Deserialize<Dictionary<string, object>>(json);
            Version latest;
            if (!Version.TryParse(((string)rel["tag_name"]).TrimStart('v', 'V'), out latest) || latest <= new Version(Program.Version)) return null;
            version = latest.ToString();
            if (version == skip) return null;

            string url = null;
            foreach (Dictionary<string, object> a in (System.Collections.ArrayList)rel["assets"])
                if ("FriendlyFire.exe".Equals(a["name"])) url = (string)a["browser_download_url"];
            if (url == null) return null;

            string exe = Application.ExecutablePath, tmp = Path.Combine(Path.GetDirectoryName(exe), "FriendlyFire.new.exe");
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "FriendlyFire/" + Program.Version;
                wc.DownloadFile(url, tmp);
            }
            if (new FileInfo(tmp).Length < 20000) { File.Delete(tmp); return null; }   // not a real exe
            return tmp;
        }

        // Runs the downloaded exe once with --probe (it exits right away with code 7) to make sure Windows lets it run
        // (Smart App Control blocks new unsigned exes); returns null when it ran, else why not
        public static string Probe(string newExe)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(newExe, "--probe") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return "ไฟล์ใหม่ไม่ตอบสนอง"; }
                    return p.ExitCode == ProbeCode ? null : "ไฟล์ใหม่เปิดไม่ขึ้น (รหัส " + p.ExitCode + ")";
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        public const int ProbeCode = 7;

        // A running exe can be renamed but not overwritten: move it aside, put the new one in its place
        public static void Swap(string newExe)
        {
            string exe = Application.ExecutablePath, old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            File.Move(newExe, exe);
        }

        // Undo Swap: the old exe back in place (the new one is thrown away)
        public static void SwapBack()
        {
            string exe = Application.ExecutablePath, old = exe + ".old";
            if (File.Exists(exe)) File.Delete(exe);
            File.Move(old, exe);
        }

        public static void CleanUp()
        {
            foreach (string f in new[] { Application.ExecutablePath + ".old", Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "FriendlyFire.new.exe") })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    // ---------- corner pop-ups (one at a time, the rest wait in line) ----------

    class Notice : Form
    {
        // Pop-ups wait their turn; an exact repeat of one showing or waiting is dropped (spam), and ✕ clears the rest
        class Pending { public string Key; public bool Keep; public Action Open; }

        // What the person did on a pop-up: an emoji button (reply = false) or a typed answer (reply = true)
        public delegate void Answer(string text, bool reply);
        // "reply" (type an answer on the pop-up, message type "reply") is built but not offered yet: it still needs a real two-PC test.
        // To turn it on: add "reply" / "ตอบกลับ (พิมพ์ข้อความ)" here and the "reply" row to Kinds
        public static readonly string[] AnswerModes = { "react", "none" };
        public static readonly string[] AnswerModeNames = { "รีแอคชัน (ปุ่มอีโมจิ)", "ไม่มี" };
        static readonly List<Pending> waiting = new List<Pending>();
        static Notice current;
        static string currentKey;
        public static Config Settings;   // which kinds may pop up, and the sound
        public static Action SaveSettings;
        public static readonly string[] DefaultReactions = { "🔥", "😎", "👍", "❓" };
        public static string[] Reactions
        {
            get
            {
                var r = Settings == null ? null : Settings.reactions.Where(e => !string.IsNullOrWhiteSpace(e)).Take(4).ToArray();
                return r == null || r.Length == 0 ? DefaultReactions : r;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        // The kinds of pop-ups you can turn off in Settings (problems and answers to your own clicks always show)
        public static readonly string[][] Kinds =
        {
            new[] { "done",   "เพื่อนทำงานสำเร็จ" },
            new[] { "react",  "เพื่อนส่งรีแอคชันให้" },
            new[] { "gift",   "เพื่อนส่งงานให้" },
            new[] { "accept", "เพื่อนรับงานที่คุณส่ง" },
            new[] { "reject", "เพื่อนไม่รับงานที่คุณส่ง" },
            new[] { "sent",   "ยืนยันเมื่อคุณส่งอะไรให้เพื่อน" },
            new[] { "update", "แอปอัปเดตเป็นเวอร์ชันใหม่" },
        };

        static string KindName(string kind) { foreach (var k in Kinds) if (k[0] == kind) return k[1]; return kind; }

        public static bool Muted { get { return Settings != null && Settings.notifyMuteUntil > Util.NowSec(); } }

        static bool Allowed(string kind)
        {
            return kind == null || Settings == null || (Settings.notifyAll && !Muted && !Settings.notifyOff.Contains(kind));
        }

        public static void Show(string kind, string title, string text, Answer react)
        {
            if (!Allowed(kind)) return;
            string key = kind + "|" + title + "|" + text;
            if ((key == currentKey && current != null && !current.IsDisposed) || waiting.Any(w => w.Key == key)) return;
            waiting.Add(new Pending { Key = key, Open = () => { if (Allowed(kind)) Open(kind, title, text, react, null); else Next(); } });   // muting from a pop-up also skips the ones waiting
            if (current == null || current.IsDisposed) Next();
        }

        public static void Show(string title, string text, Answer react) { Show(null, title, text, react); }

        // A pop-up with a "↩ เลิกทำ" button (always shown: it answers something you just did)
        public static void ShowUndo(string title, string text, Action undo)
        {
            waiting.Add(new Pending { Keep = true, Open = () => Open(null, title, text, null, undo) });   // ✕ never throws away an undo
            if (current == null || current.IsDisposed) Next();
        }

        static void Next()
        {
            current = null; currentKey = null;
            if (waiting.Count == 0) return;
            var p = waiting[0];
            waiting.RemoveAt(0);
            currentKey = p.Key;
            p.Open();
        }

        // Where pop-ups go (Settings → ตำแหน่ง) and how big they are (Settings → ขนาด)
        public static readonly string[] Places = { "br", "bl", "tr", "tl", "bc", "tc" };
        public static readonly string[] PlaceNames = { "มุมขวาล่าง", "มุมซ้ายล่าง", "มุมขวาบน", "มุมซ้ายบน", "กลางด้านล่าง", "กลางด้านบน" };
        public static readonly string[] SizeNames = { "เล็ก", "กลาง", "ใหญ่" };
        static readonly float[] Zooms = { 0.85f, 1f, 1.3f };

        static void Open(string kind, string title, string text, Answer react, Action undo)
        {
            float z = Zooms[Settings == null ? 1 : Math.Max(0, Math.Min(2, Settings.notifySize))];
            Func<int, int> S = v => (int)Math.Round(v * z);
            var f = current = new Notice
            {
                Text = "Friendly Fire", TopMost = true, FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Font = Util.UiFont(10 * z), BackColor = Theme.Paper, ForeColor = Theme.Text
            };
            f.Paint += (s, e) => { using (var p = new Pen(Theme.Edge)) e.Graphics.DrawRectangle(p, 0, 0, f.ClientSize.Width - 1, f.ClientSize.Height - 1); };
            var head = new Label { Text = title, Font = new Font("Segoe UI", 11 * z, FontStyle.Bold), ForeColor = Theme.AccentText,
                                   Location = new Point(S(12), S(10)), AutoSize = true, MaximumSize = new Size(S(320), 0) };
            f.Controls.Add(head);
            int bottom = head.Bottom;
            if (!string.IsNullOrEmpty(text))
            {
                var body = new Label { Text = text, Location = new Point(S(12), bottom + S(4)), AutoSize = true, MaximumSize = new Size(S(320), 0) };
                f.Controls.Add(body);
                bottom = body.Bottom;
            }
            int right = 0;
            foreach (Control c in f.Controls) right = Math.Max(right, c.Right);
            // Answer area (Settings → ปุ่มรีแอคชัน): emoji buttons, a reply box, or nothing
            string answerMode = Settings == null ? "react" : Settings.answerMode;
            if (Array.IndexOf(AnswerModes, answerMode) < 0) answerMode = "react";   // e.g. "reply" while it is switched off
            TextBox replyBox = null;
            if (react != null && answerMode == "reply")
            {
                replyBox = new TextBox { Location = new Point(S(12), bottom + S(10)), Width = S(230), MaxLength = 300, Font = Util.UiFont(10 * z),
                                         BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
                var box = replyBox;
                box.HandleCreated += (s, e) => Native.SendMessage(box.Handle, 0x1501, (IntPtr)1, "พิมพ์ตอบกลับ แล้วกด Enter");
                var send = new Button { Text = "ส่ง", Location = new Point(box.Right + S(6), box.Top - 1), Size = new Size(S(60), box.Height + 2), FlatStyle = FlatStyle.Flat,
                                        BackColor = Theme.AccentButton, ForeColor = Color.White };
                send.FlatAppearance.BorderColor = Theme.AccentButton;
                Action go = () => { string v = box.Text.Trim(); if (v == "") { box.Focus(); return; } react(v, true); f.Close(); };
                send.Click += (s, ev) => go();
                box.KeyDown += (s, ev) => { if (ev.KeyCode == Keys.Enter) { ev.SuppressKeyPress = true; go(); } };
                f.Controls.Add(box); f.Controls.Add(send);
                bottom = box.Bottom + S(2);
                right = Math.Max(right, send.Right);
            }
            if (react != null && answerMode == "react")
            {
                int x = S(12);
                foreach (string e in Reactions)
                {
                    string emoji = e;
                    var b = new Button { Text = emoji, Font = Util.EmojiFont(13 * z), Location = new Point(x, bottom + S(8)), Size = new Size(S(48), S(38)), FlatStyle = FlatStyle.Flat };
                    b.FlatAppearance.BorderColor = Theme.Mix(Theme.Paper, Theme.Text, 0.35);
                    b.FlatAppearance.MouseOverBackColor = Theme.Mix(Theme.Paper, Theme.Accent, 0.25);
                    b.Click += (s, ev) => { react(emoji, false); f.Close(); };
                    f.Controls.Add(b);
                    x += S(54);
                }
                bottom += S(8) + S(38);
                right = Math.Max(right, x - S(6));
            }
            if (undo != null)
            {
                var b = new Button { Text = "↩ เลิกทำ", Location = new Point(S(12), bottom + S(8)), Size = new Size(S(110), S(32)), FlatStyle = FlatStyle.Flat, BackColor = Theme.Field, ForeColor = Theme.Text };
                b.Click += (s, ev) => { undo(); f.Close(); };
                f.Controls.Add(b);
                bottom += S(8) + S(32);
                right = Math.Max(right, b.Right);
            }
            int width = Math.Max(right + S(12) + S(56), S(240));   // room on the right for 🔕 and ✕
            f.ClientSize = new Size(width, bottom + S(12));
            var more = new Label { ForeColor = Theme.Muted, Font = Util.UiFont(8.5f * z), TextAlign = System.Drawing.ContentAlignment.BottomRight,
                                   Size = new Size(S(50), S(18)), Location = new Point(width - S(56), f.ClientSize.Height - S(22)), Visible = false };
            f.Controls.Add(more);

            // 🔕 (mute) and ✕ (close): only while the mouse is over the pop-up
            var close = TopButton("✕", width - S(30), z);
            close.Click += (s, e) => { waiting.RemoveAll(w => !w.Keep); f.Close(); };   // one click closes the ones waiting too
            var mute = TopButton("🔕", width - S(58), z);
            var menu = new ContextMenuStrip { Font = Util.UiFont(10) };
            menu.Opening += (s, e) => Theme.Style(menu);
            menu.Items.Add("ปิดการแจ้งเตือน 1 ชั่วโมง", null, (s, e) => { MuteFor(3600); f.Close(); });
            menu.Items.Add("ปิดการแจ้งเตือน 1 วัน", null, (s, e) => { MuteFor(86400); f.Close(); });
            if (kind != null)
                menu.Items.Add("ไม่แจ้งเตือนแบบนี้อีก (" + KindName(kind) + ")", null, (s, e) =>
                {
                    if (!Settings.notifyOff.Contains(kind)) Settings.notifyOff.Add(kind);
                    if (SaveSettings != null) SaveSettings();
                    f.Close();
                });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("(เปิดคืนได้ใน ⚙ ตั้งค่า)") { Enabled = false });
            mute.Click += (s, e) => menu.Show(mute, new Point(0, mute.Height));
            f.Controls.Add(close); f.Controls.Add(mute);
            close.BringToFront(); mute.BringToFront();
            close.Visible = false; mute.Visible = false;

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            string place = Settings == null ? "br" : Settings.notifyPlace;
            int gap = 12;
            int px = place.EndsWith("l") ? wa.Left + gap : place.EndsWith("c") ? wa.Left + (wa.Width - f.Width) / 2 : wa.Right - f.Width - gap;
            int py = place.StartsWith("t") ? wa.Top + gap : wa.Bottom - f.Height - gap;
            f.Location = new Point(px, py);
            f.FormClosed += (s, e) => { menu.Dispose(); Next(); };

            // Every pop-up stays 1–8 s depending on how much there is to read, plus the extra time from Settings
            // (-1 = until closed), then fades out over the last second; hovering (or the mute menu) keeps it
            // Read on every tick, so changing the setting also changes a pop-up that is already showing
            int auto = AutoSeconds(title, text);
            DateTime shownAt = DateTime.Now, heldUntil = DateTime.MinValue;
            var t = new System.Windows.Forms.Timer { Interval = 50 };
            t.Tick += (s, e) =>
            {
                if (f.IsDisposed) { t.Dispose(); return; }
                bool over = f.Bounds.Contains(Cursor.Position) || menu.Visible || (replyBox != null && (replyBox.Focused || replyBox.Text != ""));   // stays while you type
                close.Visible = over; mute.Visible = over && Settings != null;
                more.Text = "+" + waiting.Count + " รอ"; more.Visible = waiting.Count > 0;
                if (over) { heldUntil = DateTime.Now.AddSeconds(3); f.Opacity = 1; return; }
                int extra = Settings == null ? 0 : Settings.notifyExtra;
                if (extra == Forever) { f.Opacity = 1; return; }
                DateTime fadeAt = shownAt.AddSeconds(Math.Max(1, auto + extra));   // never less than 1 s
                if (heldUntil > fadeAt) fadeAt = heldUntil;
                double into = (DateTime.Now - fadeAt).TotalSeconds;
                if (into < 0) return;
                if (into >= 1) { t.Dispose(); f.Close(); return; }
                f.Opacity = 1 - into;
            };
            t.Start();
            f.Show();
            PlaySound();
        }
        public const int Forever = -1000;   // Settings → แสดงนาน → จนกว่าจะกดปิด

        // Settings → ลองดูป๊อปอัป: always shows (even when muted) and replaces the last try, so it shows the settings as they are now
        public static void Preview()
        {
            waiting.RemoveAll(w => w.Key == "preview");
            if (current != null && !current.IsDisposed && currentKey == "preview") current.Close();
            waiting.Insert(0, new Pending { Key = "preview", Open = () => Open(null, "🔥 ตัวอย่างป๊อปอัป", "ตำแหน่ง ขนาด และเวลาแสดงจะเป็นแบบนี้", (v, r) => { }, null) });
            if (current == null || current.IsDisposed) Next();
        }

        // Auto: about the time it takes to read it, 1 s at least and 8 s at most
        static int AutoSeconds(string title, string text)
        {
            int chars = (title ?? "").Length + (text ?? "").Length;
            return Math.Max(1, Math.Min(8, 1 + (int)Math.Round(chars / 15.0)));
        }

        static Label TopButton(string text, int x, float z)
        {
            var b = new Label { Text = text, Font = Util.EmojiFont(10 * z), ForeColor = Theme.Muted, TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                                Location = new Point(x, (int)(4 * z)), Size = new Size((int)(26 * z), (int)(24 * z)), Cursor = Cursors.Hand };
            b.MouseEnter += (s, e) => b.BackColor = Theme.Mix(Theme.Paper, Theme.Accent, 0.3);
            b.MouseLeave += (s, e) => b.BackColor = Color.Transparent;
            return b;
        }

        static void MuteFor(int seconds)
        {
            Settings.notifyMuteUntil = Util.NowSec() + seconds;
            if (SaveSettings != null) SaveSettings();
        }

        static void PlaySound()
        {
            if (Settings != null && !Settings.notifySound) return;
            try
            {
                string wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Media\Windows Notify Messaging.wav");
                if (File.Exists(wav)) new SoundPlayer(wav).Play(); else SystemSounds.Asterisk.Play();
            }
            catch { }
        }
    }

    // ---------- look: light / dark and the accent colour (Settings → หน้าตา) ----------

    static class Theme
    {
        public static readonly string[] Modes = { "light", "dark", "system" };
        public static readonly string[] ModeNames = { "สว่าง", "มืด", "ตาม Windows" };
        public static readonly string[] Accents = { "orange", "blue", "purple", "pink", "mint", "graphite" };
        public static readonly string[] AccentNames = { "ส้มไฟ", "ฟ้าทะเล", "ม่วงลาเวนเดอร์", "ชมพูซากุระ", "เขียวมิ้นต์", "เทากราไฟต์" };
        public static readonly Color[] AccentColors = { Color.FromArgb(255, 140, 40), Color.FromArgb(40, 130, 230), Color.FromArgb(140, 95, 225),
                                                        Color.FromArgb(232, 85, 150), Color.FromArgb(25, 170, 140), Color.FromArgb(105, 112, 125) };

        // Dark mode: mixing these with white looked washed out, so they get their own vivid text and deeper button tones
        // (purple and graphite looked right mixed, so they stay that way: Empty)
        static readonly Color[] DarkText = { Color.FromArgb(255, 152, 60), Color.FromArgb(85, 165, 255), Color.Empty,
                                             Color.FromArgb(255, 112, 172), Color.FromArgb(50, 210, 170), Color.Empty };
        static readonly Color[] DarkButton = { Color.FromArgb(235, 120, 25), Color.FromArgb(30, 115, 225), Color.Empty,
                                               Color.FromArgb(220, 65, 135), Color.FromArgb(20, 155, 125), Color.Empty };

        public static bool Dark;
        public static Color AccentButton;
        public static Color Accent, AccentText, AccentSoft, Back, Field, Text, Muted, Line, Card, CardEdge, Done, Wait,
                            Gift, GiftText, Pick, PickOut, PickEdge, Sel, Paper, Edge, Online, Button;

        public static event Action Changed;   // open windows colour themselves again

        public static void Set(string mode, string accent)
        {
            Dark = mode == "dark" || (mode == "system" && WindowsDark());
            int at = Math.Max(0, Array.IndexOf(Accents, accent));
            Accent = AccentButton = AccentColors[at];
            if (Dark)
            {
                Back = Rgb(32, 32, 32); Field = Rgb(43, 43, 43); Text = Rgb(235, 235, 235); Muted = Rgb(160, 160, 160); Line = Rgb(56, 56, 56);
                Card = Rgb(58, 58, 58); CardEdge = Rgb(78, 78, 78); Done = Rgb(52, 52, 52); Wait = Rgb(50, 50, 50);
                Gift = Rgb(36, 68, 46); GiftText = Rgb(140, 220, 160); Pick = Rgb(92, 48, 48); PickOut = Rgb(125, 48, 42); PickEdge = Rgb(190, 95, 85);
                Online = Rgb(110, 210, 130); Button = Rgb(55, 55, 55);
                AccentText = DarkText[at].IsEmpty ? Mix(Accent, Color.White, 0.3) : DarkText[at];
                if (!DarkButton[at].IsEmpty) AccentButton = DarkButton[at];
                AccentSoft = Mix(Field, Accent, 0.16); Sel = Mix(Field, Accent, 0.24);
                Paper = Mix(Rgb(40, 40, 42), Accent, 0.05); Edge = Accent;   // a near-neutral pop-up: a strong tint turned brown/muddy
            }
            else
            {
                Back = SystemColors.Control; Field = Color.White; Text = Rgb(25, 25, 25); Muted = Color.DimGray; Line = Rgb(238, 238, 238);
                Card = Color.White; CardEdge = Rgb(228, 228, 228); Done = Rgb(236, 236, 236); Wait = Rgb(234, 234, 234);
                Gift = Rgb(220, 244, 225); GiftText = Rgb(25, 110, 45); Pick = Rgb(255, 222, 220); PickOut = Rgb(255, 200, 195); PickEdge = Rgb(240, 150, 140);
                Online = Rgb(30, 130, 60); Button = Color.White;
                AccentText = Mix(Accent, Color.Black, 0.28); AccentSoft = Mix(Field, Accent, 0.1); Sel = Mix(Field, Accent, 0.16);
                Paper = Mix(Color.White, Accent, 0.1); Edge = Mix(Color.White, Accent, 0.65);
            }
            if (Changed != null) Changed();
        }

        static Color Rgb(int r, int g, int b) { return Color.FromArgb(r, g, b); }
        public static Color Mix(Color a, Color b, double t) { return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t)); }

        static bool WindowsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        // Colours a window and everything in it. Tags: "heading" / "muted" labels, "accent" button, "swatch" = leave alone
        public static void Apply(Control c)
        {
            string tag = c.Tag as string;
            if (tag == "swatch") return;
            if (c is Form) { c.BackColor = Back; c.ForeColor = Text; TitleBar((Form)c); }
            else if (c is ListBox) { c.BackColor = Field; c.ForeColor = Text; c.Invalidate(); }
            else if (c is TextBox) { var tb = (TextBox)c; tb.BorderStyle = Dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D; c.BackColor = Field; c.ForeColor = Text; }   // the 3D edge is bright white on dark
            else if (c is ComboBox) { ((ComboBox)c).FlatStyle = Dark ? FlatStyle.Flat : FlatStyle.Standard; c.BackColor = Field; c.ForeColor = Text; }
            else if (c is System.Windows.Forms.Button)
            {
                var b = (System.Windows.Forms.Button)c;
                bool accent = tag == "accent";
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = accent ? AccentButton : Button;
                b.ForeColor = accent ? Color.White : Text;
                b.FlatAppearance.BorderColor = accent ? AccentButton : Dark ? Rgb(90, 90, 90) : Color.Silver;
                b.FlatAppearance.MouseOverBackColor = accent ? Mix(AccentButton, Color.White, 0.15) : Mix(Button, Accent, 0.15);
                b.FlatAppearance.MouseDownBackColor = accent ? Mix(AccentButton, Color.Black, 0.1) : Mix(Button, Accent, 0.28);
            }
            else if (c is LinkLabel) { var l = (LinkLabel)c; l.ForeColor = Text; l.LinkColor = AccentText; l.ActiveLinkColor = Accent; }
            else if (c is Label) c.ForeColor = tag == "heading" ? AccentText : tag == "muted" ? Muted : Text;
            else if (c is CheckBox || c is RadioButton || c is GroupBox) c.ForeColor = Text;
            if (c.ContextMenuStrip != null) Style(c.ContextMenuStrip);
            foreach (Control k in c.Controls) Apply(k);
        }

        // Right-click menus in the same colours
        public static void Style(ToolStripDropDown m)
        {
            m.Renderer = new ToolStripProfessionalRenderer(new MenuColors());
            m.BackColor = Field; m.ForeColor = Text;
            foreach (ToolStripItem i in m.Items) i.ForeColor = Text;
        }

        class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Field; } }
            public override Color ImageMarginGradientBegin { get { return Field; } }
            public override Color ImageMarginGradientMiddle { get { return Field; } }
            public override Color ImageMarginGradientEnd { get { return Field; } }
            public override Color MenuBorder { get { return Dark ? Rgb(70, 70, 70) : Rgb(200, 200, 200); } }
            public override Color MenuItemBorder { get { return Mix(Field, Accent, 0.5); } }
            public override Color MenuItemSelected { get { return Sel; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Line; } }
            public override Color CheckBackground { get { return Sel; } }
            public override Color CheckSelectedBackground { get { return Sel; } }
            public override Color CheckPressedBackground { get { return Sel; } }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        // Dark title bar too (Windows 10 2004+ / 11)
        public static void TitleBar(Form f)
        {
            if (!f.IsHandleCreated) { f.HandleCreated -= TitleBarLater; f.HandleCreated += TitleBarLater; return; }
            int v = Dark ? 1 : 0;
            try
            {
                DwmSetWindowAttribute(f.Handle, 20, ref v, 4);
                SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0027);   // NOSIZE | NOMOVE | NOZORDER | FRAMECHANGED: repaint the frame now
            }
            catch { }
        }

        static void TitleBarLater(object s, EventArgs e) { TitleBar((Form)s); }
    }

    // ---------- first run: name + pair ----------

    class PairForm : Form
    {
        readonly TextBox nameBox = new TextBox(), codeBox = new TextBox();
        readonly RadioButton create = new RadioButton(), join = new RadioButton();
        public string PlayerName, Code; public bool MadeNew;

        public PairForm(string name)
        {
            Text = "Friendly Fire"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(380, 250); Font = Util.UiFont(10.5f); Icon = Program.Flame;
            Controls.Add(new Label { Text = "ชื่อของคุณ", Location = new Point(16, 18), AutoSize = true });
            nameBox.SetBounds(120, 15, 240, 28); nameBox.Text = name;
            create.Text = "สร้างคู่ใหม่ (แล้วส่งรหัสให้เพื่อน)"; create.SetBounds(16, 60, 340, 26); create.Checked = true;
            join.Text = "ใส่รหัสเชิญจากเพื่อน"; join.SetBounds(16, 92, 340, 26);
            codeBox.SetBounds(40, 124, 320, 28); codeBox.Enabled = false;
            join.CheckedChanged += (s, e) => codeBox.Enabled = join.Checked;
            var ok = new Button { Text = "ตกลง", Location = new Point(250, 196), Size = new Size(110, 36) };
            ok.Click += (s, e) => Done();
            Controls.AddRange(new Control[] { nameBox, create, join, codeBox, ok });
            AcceptButton = ok;
            Theme.Apply(this);
        }

        void Done()
        {
            PlayerName = nameBox.Text.Trim();
            if (PlayerName == "") { MessageBox.Show(this, "ใส่ชื่อก่อนนะ", "Friendly Fire"); return; }
            if (create.Checked) { Code = "FF-" + Util.RandomCode(16); MadeNew = true; }
            else
            {
                Code = Program.CleanCode(codeBox.Text);
                if (Code == null) { MessageBox.Show(this, "รหัสไม่ถูกต้อง (หน้าตาแบบ FF-XXXXXXXXXXXXXXXX)", "Friendly Fire"); return; }
            }
            DialogResult = DialogResult.OK;
        }
    }

    // ---------- main window ----------

    // One row of a TaskList
    class Row
    {
        public TaskItem task;     // null: a hint like "(ยังไม่มีงาน)"
        public string text;
        public bool check;        // draw a tick box (my tasks)
        public bool grey;         // grey cover: a gift not taken yet
        public bool buttons;      // ✓ / ✗ buttons of a gift
        public string tag;        // small note on the right, e.g. "รอรับ"
    }

    // A list whose rows wrap long text instead of running off the edge; draws tick boxes, gifts and the drop line itself
    class TaskList : ListBox
    {
        public List<Row> Rows = new List<Row>();
        public int DropLine = -1;   // where a dragged task would land, -1 = not dragging here
        public bool ShowSelection;  // highlight the selected row (only while its right-click menu is open)
        const int TextLeft = 32, Btn = 26;
        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
        const TextFormatFlags Keep = TextFormatFlags.PreserveGraphicsTranslateTransform;   // rows are drawn into an off-screen buffer that is shifted
        static readonly Color Green = Color.FromArgb(40, 160, 70), Red = Color.FromArgb(215, 60, 50);
        int lastWidth;

        public TaskList()
        {
            DrawMode = DrawMode.OwnerDrawVariable; IntegralHeight = false; BorderStyle = BorderStyle.None;
        }

        public void SetRows(List<Row> rows)
        {
            int top = TopIndex;
            Rows = rows;
            BeginUpdate();
            Items.Clear();
            foreach (var r in rows) Items.Add(r);   // heights are measured as items are added
            if (top < Items.Count) TopIndex = top;
            EndUpdate();
        }

        // Row heights depend on the width: measure again when it changes
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (IsHandleCreated && ClientSize.Width != lastWidth) { lastWidth = ClientSize.Width; SetRows(Rows); }
        }

        int TextWidth(Row r) { return Math.Max(60, ClientSize.Width - TextLeft - (r.buttons ? 2 * Btn + 16 : r.tag != null ? 70 : 8)); }

        protected override void OnMeasureItem(MeasureItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Rows.Count) return;
            var r = Rows[e.Index];
            int h = TextRenderer.MeasureText(r.text, Font, new Size(TextWidth(r), 10000), Wrap).Height;
            e.ItemHeight = Math.Min(255, Math.Max(Btn + 8, h + 12));
        }

        // Only a click inside the tick box ticks (a click elsewhere on the row is easy to make by mistake)
        public Rectangle CheckRect(int i) { var b = GetItemRectangle(i); return new Rectangle(b.X + 5, b.Y + 6, 22, 22); }

        public Rectangle OkRect(int i) { var b = GetItemRectangle(i); return new Rectangle(b.Right - 2 * Btn - 12, b.Y + 4, Btn, Btn); }
        public Rectangle NoRect(int i) { var b = GetItemRectangle(i); return new Rectangle(b.Right - Btn - 6, b.Y + 4, Btn, Btn); }

        public int Hot = -1;      // row under the mouse: drawn lifted
        public int Picked = -1;   // row being dragged: lifted, with a light red cover
        public bool PickedOut;    // the dragged row is outside the window: letting go throws it away


        // Each row is drawn off-screen first and then copied in one go, so it never shows half-drawn
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Rows.Count) return;
            using (var buf = BufferedGraphicsManager.Current.Allocate(e.Graphics, e.Bounds))
            {
                DrawRow(buf.Graphics, e);
                buf.Render(e.Graphics);
            }
        }

        void DrawRow(Graphics g, DrawItemEventArgs e)
        {
            var r = Rows[e.Index];
            var b = e.Bounds;
            bool sel = ShowSelection && (e.State & DrawItemState.Selected) != 0 && r.task != null;
            bool picked = Picked == e.Index, lifted = r.task != null && (picked || Hot == e.Index);
            Color back = picked ? (PickedOut ? Theme.PickOut : Theme.Pick) : sel ? Theme.Sel : r.buttons ? Theme.Gift : r.grey ? Theme.Wait : r.task != null && r.task.done ? Theme.Done : BackColor;
            using (var br = new SolidBrush(BackColor)) g.FillRectangle(br, b);

            int up = 0;   // lifted rows move up a little and get a shadow, like a card picked off the table
            if (lifted)
            {
                up = picked ? 2 : 1;   // hover: just a hint of lift; dragging: a bit more
                int layers = picked ? 3 : 2, alpha = picked ? 18 : 9;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var card = new Rectangle(b.X + 3, b.Y + 1, b.Width - 7, b.Height - 3 - layers);
                for (int k = layers; k >= 1; k--)
                    using (var path = Round(new Rectangle(card.X, card.Y + k, card.Width, card.Height), 7))
                    using (var sh = new SolidBrush(Color.FromArgb(alpha * (layers + 1 - k), 0, 0, 0))) g.FillPath(sh, path);
                using (var path = Round(card, 7))
                {
                    using (var br = new SolidBrush(back == BackColor ? Theme.Card : back)) g.FillPath(br, path);
                    using (var pen = new Pen(picked ? Theme.PickEdge : Theme.CardEdge)) g.DrawPath(pen, path);
                }
                g.SmoothingMode = SmoothingMode.Default;
            }
            else
            {
                if (back != BackColor) using (var br = new SolidBrush(back)) g.FillRectangle(br, b.X + 3, b.Y + 1, b.Width - 7, b.Height - 3);
                using (var p = new Pen(Theme.Line)) g.DrawLine(p, b.X + 6, b.Bottom - 1, b.Right - 6, b.Bottom - 1);
            }

            int top = b.Y - up, lineY = top + 6;   // the first text line; marks and buttons line up with it
            if (r.check)
                CheckBoxRenderer.DrawCheckBox(g, new Point(b.X + 9, top + 10), r.task.done ? CheckBoxState.CheckedNormal : CheckBoxState.UncheckedNormal);
            else if (r.task != null && !r.buttons)
                TextRenderer.DrawText(g, r.task.done ? "✓" : "○", Font, new Rectangle(b.X + 4, lineY, 26, 22), r.task.done ? Green : Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | Keep);
            else if (r.buttons)
                TextRenderer.DrawText(g, "🎁", Font, new Rectangle(b.X + 4, lineY, 26, 22), Green, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | Keep);

            Color fore = r.task == null || r.grey || r.task.done ? Color.Gray : r.buttons ? Theme.GiftText : ForeColor;
            TextRenderer.DrawText(g, r.text, Font, new Rectangle(b.X + TextLeft, lineY, TextWidth(r), b.Height - 6), fore, Wrap | Keep);

            if (r.tag != null)
                using (var small = Util.UiFont(8.5f))
                    TextRenderer.DrawText(g, r.tag, small, new Rectangle(b.Right - 68, lineY + 2, 62, 20), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.NoPrefix | Keep);

            if (r.buttons)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var ok = OkRect(e.Index); ok.Offset(0, -up);
                var no = NoRect(e.Index); no.Offset(0, -up);
                DrawButton(g, ok, Green, true);
                DrawButton(g, no, Red, false);
                g.SmoothingMode = SmoothingMode.Default;
            }

            if (picked && PickedOut)
                using (var small = new Font("Segoe UI", 9, FontStyle.Bold))
                {
                    var pill = new Rectangle(b.Right - 118, top + 4, 108, 22);
                    using (var br = new SolidBrush(Red)) g.FillRectangle(br, pill);
                    TextRenderer.DrawText(g, "ปล่อยเพื่อทิ้ง 🗑", small, pill, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | Keep);
                }

            if (DropLine == e.Index || (DropLine == Rows.Count && e.Index == Rows.Count - 1))
                using (var br = new SolidBrush(Theme.Accent)) g.FillRectangle(br, b.X + 2, DropLine == e.Index ? b.Y : b.Bottom - 3, b.Width - 4, 3);
        }

        static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        void Redraw(int i) { if (i >= 0 && i < Items.Count) Invalidate(GetItemRectangle(i)); }

        public void SetHot(int i) { if (Hot == i) return; int old = Hot; Hot = i; Redraw(old); Redraw(i); }
        public void SetPicked(int i) { if (Picked == i) return; int old = Picked; Picked = i; PickedOut = false; Redraw(old); Redraw(i); }
        public void SetPickedOut(bool on) { if (PickedOut == on) return; PickedOut = on; Redraw(Picked); }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHot(-1); }
        static void DrawButton(Graphics g, Rectangle r, Color c, bool tick)
        {
            using (var br = new SolidBrush(c)) g.FillEllipse(br, r);
            using (var p = new Pen(Color.White, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float x = r.X, y = r.Y, s = r.Width;
                if (tick) g.DrawLines(p, new[] { new PointF(x + s * 0.27f, y + s * 0.52f), new PointF(x + s * 0.44f, y + s * 0.68f), new PointF(x + s * 0.74f, y + s * 0.34f) });
                else { g.DrawLine(p, x + s * 0.32f, y + s * 0.32f, x + s * 0.68f, y + s * 0.68f); g.DrawLine(p, x + s * 0.68f, y + s * 0.32f, x + s * 0.32f, y + s * 0.68f); }
            }
        }

        public TaskItem TaskAt(int i) { return i >= 0 && i < Rows.Count ? Rows[i].task : null; }
        public TaskItem SelectedTask { get { return TaskAt(SelectedIndex); } }

        public int RowAt(Point p)
        {
            int i = IndexFromPoint(p);
            return i >= 0 && i < Rows.Count && GetItemRectangle(i).Contains(p) ? i : -1;
        }

        public void SelectAt(Point p) { int i = RowAt(p); if (i >= 0) SelectedIndex = i; else ClearSelected(); }

        // Insert position for a drop at screen point p: before the row under it, or after it when in its lower half
        public int DropIndexAt(Point screen)
        {
            var p = PointToClient(screen);
            int i = RowAt(p);
            if (i < 0 || Rows[i].task == null) return Rows.Count(r => r.task != null);
            var b = GetItemRectangle(i);
            return p.Y > b.Y + b.Height / 2 ? i + 1 : i;
        }

        // Redraw only the rows the line leaves and enters (redrawing the whole list made it flicker while dragging)
        public void ShowDropLine(int i)
        {
            if (DropLine == i) return;
            int old = DropLine; DropLine = i;
            foreach (int k in new[] { old, old - 1, i, i - 1 }) Redraw(k);
        }

        // The list doesn't wipe rows white before they are drawn (that flash was the flicker): rows paint their own
        // background, so only the space under the last row is filled here
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x14)   // WM_ERASEBKGND
            {
                int bottom = Items.Count > 0 ? GetItemRectangle(Items.Count - 1).Bottom : 0;
                if (bottom < ClientSize.Height)
                    using (var g = Graphics.FromHdc(m.WParam))
                    using (var br = new SolidBrush(BackColor)) g.FillRectangle(br, 0, bottom, ClientSize.Width, ClientSize.Height - bottom);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = RowAt(e.Location);
            var r = i >= 0 && i < Rows.Count ? Rows[i] : null;
            bool hot = r != null && ((r.check && CheckRect(i).Contains(e.Location)) || (r.buttons && (OkRect(i).Contains(e.Location) || NoRect(i).Contains(e.Location))));
            Cursor = hot ? Cursors.Hand : Cursors.Default;
            SetHot(r != null && r.task != null ? i : -1);
        }
    }

    class MainForm : Form
    {
        readonly App app;
        readonly Label status = new Label();
        readonly TaskList mine = new TaskList(), theirs = new TaskList();
        readonly GroupBox theirBox = new GroupBox();
        readonly TextBox newTask = new TextBox(), brag = new TextBox();
        int pressed = -1;          // row the left button went down on (a click or the start of a drag)
        Point pressedAt;
        public bool Exiting;

        public MainForm(App app)
        {
            this.app = app;
            Text = "Friendly Fire"; Icon = Program.Flame; Font = Util.UiFont(10.5f); ClientSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(560, 380);

            var top = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 4, 10, 0) };
            var gear = new Button { Text = "⚙ ตั้งค่า", Dock = DockStyle.Right, Width = 96 };
            gear.Click += (s, e) => app.ShowSettings();
            status.Dock = DockStyle.Fill; status.Padding = new Padding(10, 4, 0, 0);
            top.Controls.Add(status); top.Controls.Add(gear);

            // Bottom: tell the friend about anything you finished
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(10, 8, 10, 10) };
            var bragBtn = new Button { Text = "เสร็จแล้ว! 🎉", Dock = DockStyle.Right, Width = 150, Font = new Font("Segoe UI", 11, FontStyle.Bold),
                                       Tag = "accent" };
            brag.Dock = DockStyle.Fill; brag.Font = Util.UiFont(12);
            SetCue(brag, "ทำอะไรเสร็จ? เช่น อ่านฟิสิกส์จบบท 3");
            bragBtn.Click += (s, e) => Brag();
            brag.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Brag(); } };
            bottom.Controls.Add(brag); bottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); bottom.Controls.Add(bragBtn);

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(6, 0, 6, 0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            // My tasks: add, tick, drag to reorder or over to the friend, delete
            var myBox = new GroupBox { Text = "งานของฉัน  (ลากเพื่อเรียง, ไปให้เพื่อน หรือออกนอกหน้าต่างเพื่อทิ้ง)", Dock = DockStyle.Fill, Padding = new Padding(8) };
            var addRow = new Panel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(0, 6, 0, 0) };
            var addBtn = new Button { Text = "เพิ่ม", Dock = DockStyle.Right, Width = 70 };
            newTask.Dock = DockStyle.Fill; SetCue(newTask, "งานใหม่...");
            addBtn.Click += (s, e) => AddTask();
            newTask.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddTask(); } };
            addRow.Controls.Add(newTask); addRow.Controls.Add(addBtn);
            mine.Dock = DockStyle.Fill; mine.AllowDrop = true;
            mine.MouseDown += MineDown;
            mine.MouseMove += MineMove;
            mine.MouseUp += MineUp;
            mine.DragOver += (s, e) => { var t = Dragged(e); e.Effect = t != null ? DragDropEffects.Move : DragDropEffects.None; mine.ShowDropLine(t != null ? mine.DropIndexAt(new Point(e.X, e.Y)) : -1); };
            mine.DragLeave += (s, e) => mine.ShowDropLine(-1);
            mine.QueryContinueDrag += (s, e) => mine.SetPickedOut(!Bounds.Contains(Cursor.Position));   // say "ปล่อยเพื่อทิ้ง" outside the window
            mine.DragDrop += (s, e) =>
            {
                var t = Dragged(e);
                int at = mine.DropIndexAt(new Point(e.X, e.Y));
                mine.ShowDropLine(-1);
                if (t != null) app.MoveTask(t, at);
            };
            var menu = new ContextMenuStrip();
            var delOne = menu.Items.Add("ลบงานนี้", null, (s, e) => { var t = mine.SelectedTask; if (t != null) DeleteTask(t); });
            var delDone = menu.Items.Add("ลบงานที่เสร็จแล้วทั้งหมด", null, (s, e) => { app.Cfg.tasks.RemoveAll(t => t.done); app.Changed("sync", null); });
            menu.Opening += (s, e) =>
            {
                var t = mine.SelectedTask;
                delOne.Enabled = t != null && !t.offer;   // gifts have their own ✓ / ✗
                delDone.Enabled = app.Cfg.tasks.Any(x => x.done);
                e.Cancel = !delOne.Enabled && !delDone.Enabled;
            };
            mine.ContextMenuStrip = menu;
            menu.Opened += (s, e) => { mine.ShowSelection = true; mine.Invalidate(); };
            menu.Closed += (s, e) => { mine.ShowSelection = false; mine.Invalidate(); };
            myBox.Controls.Add(mine); myBox.Controls.Add(addRow);

            // Friend's tasks: read only; finished ones can be removed from this PC; drop a task here to give it
            theirBox.Dock = DockStyle.Fill; theirBox.Padding = new Padding(8);
            theirs.Dock = DockStyle.Fill; theirs.AllowDrop = true;
            var theirMenu = new ContextMenuStrip();
            var hideOne = theirMenu.Items.Add("ลบงานนี้ (เฉพาะเครื่องนี้)", null, (s, e) => { var t = theirs.SelectedTask; if (t != null) app.HideFriendTask(t); });
            var hideDone = theirMenu.Items.Add("ลบงานที่เสร็จแล้วทั้งหมดของเพื่อน (เฉพาะเครื่องนี้)", null, (s, e) => app.HideFriendDone());
            theirMenu.Opening += (s, e) =>
            {
                var t = theirs.SelectedTask;
                hideOne.Enabled = t != null && t.done;   // unfinished tasks belong to the friend
                hideDone.Enabled = app.Cfg.friendTasks.Any(x => x.done);
                e.Cancel = !hideOne.Enabled && !hideDone.Enabled;
            };
            theirs.ContextMenuStrip = theirMenu;
            theirMenu.Opened += (s, e) => { theirs.ShowSelection = true; theirs.Invalidate(); };
            theirMenu.Closed += (s, e) => { theirs.ShowSelection = false; theirs.Invalidate(); };
            theirs.MouseDown += (s, e) => { if (e.Button == MouseButtons.Right) theirs.SelectAt(e.Location); };
            theirs.DragOver += (s, e) =>
            {
                var t = Dragged(e);
                bool ok = t != null && !t.done;
                e.Effect = ok ? DragDropEffects.Move : DragDropEffects.None;
                theirs.BackColor = ok ? Theme.AccentSoft : Theme.Field;
            };
            theirs.DragLeave += (s, e) => theirs.BackColor = Theme.Field;
            theirs.DragDrop += (s, e) => { theirs.BackColor = Theme.Field; var t = Dragged(e); if (t != null && !t.done) app.GiveTask(t); };
            theirBox.Controls.Add(theirs);
            Activated += (s, e) => app.MarkSeen(true);

            grid.Controls.Add(myBox, 0, 0); grid.Controls.Add(theirBox, 1, 0);
            Controls.Add(grid); Controls.Add(bottom); Controls.Add(top);

            FormClosing += (s, e) => { if (!Exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
            FillMine(); FillTheirs(); SetStatus(false);
            Retheme();
        }

        public void Retheme() { Theme.Apply(this); SetStatus(online); }

        // Grey hint text inside an empty text box
        static void SetCue(TextBox box, string cue)
        {
            box.HandleCreated += (s, e) => Native.SendMessage(box.Handle, 0x1501, (IntPtr)1, cue);
        }

        static TaskItem Dragged(DragEventArgs e) { return e.Data.GetData(typeof(TaskItem)) as TaskItem; }

        void MineDown(object sender, MouseEventArgs e)
        {
            int i = mine.RowAt(e.Location);
            if (e.Button == MouseButtons.Right) { mine.SelectAt(e.Location); return; }
            pressed = mine.TaskAt(i) != null ? i : -1;
            pressedAt = e.Location;
        }

        // Moving past the drag distance starts a drag (gifts can't be dragged until they're taken)
        void MineMove(object sender, MouseEventArgs e)
        {
            if (pressed < 0 || e.Button != MouseButtons.Left) return;
            var drag = SystemInformation.DragSize;
            if (Math.Abs(e.X - pressedAt.X) < drag.Width && Math.Abs(e.Y - pressedAt.Y) < drag.Height) return;
            var t = mine.TaskAt(pressed);
            pressed = -1;
            if (t == null || t.offer) return;
            mine.Capture = false;   // otherwise the list keeps moving its selection with the mouse
            mine.SetHot(-1);
            mine.SetPicked(mine.Rows.FindIndex(x => x.task == t));
            var result = mine.DoDragDrop(t, DragDropEffects.Move);
            bool outside = !Bounds.Contains(Cursor.Position);
            mine.SetPicked(-1);
            if (result == DragDropEffects.None && outside) app.DiscardTask(t);
            mine.ShowDropLine(-1);
            mine.ClearSelected();
            theirs.BackColor = Theme.Field;
        }

        // A click without a drag: tick / untick, or the ✓ / ✗ of a gift
        void MineUp(object sender, MouseEventArgs e)
        {
            int i = pressed;
            pressed = -1;
            if (e.Button != MouseButtons.Left || i < 0 || mine.RowAt(e.Location) != i) return;
            var t = mine.TaskAt(i);
            if (t == null) return;
            if (!t.offer) { if (mine.CheckRect(i).Contains(e.Location)) Toggle(t); }
            else if (mine.OkRect(i).Contains(e.Location)) app.TakeGift(t, true);
            else if (mine.NoRect(i).Contains(e.Location)) app.TakeGift(t, false);
        }

        void AddTask()
        {
            string text = newTask.Text.Trim();
            if (text == "") return;
            app.Cfg.tasks.Add(new TaskItem { id = Util.RandomCode(8), text = text });
            newTask.Text = "";
            app.Changed("sync", null);
        }

        void DeleteTask(TaskItem t)
        {
            if (t.offer) return;
            app.Cfg.tasks.Remove(t);
            app.Changed("sync", null);
        }

        void Toggle(TaskItem t)
        {
            t.done = !t.done;
            t.doneAt = t.done ? Util.NowSec() : 0;
            t.seenAt = t.doneAt;   // you saw it when you ticked it
            app.Changed(t.done ? "done" : "sync", t.done ? t.text : null);
        }

        void Brag()
        {
            string text = brag.Text.Trim();
            if (text == "") { brag.Focus(); return; }
            brag.Text = "";
            app.Changed("done", text);
            Notice.Show("sent", "ส่งให้ " + app.FriendLabel + " แล้ว 🎉", text, null);
        }

        public void FillMine()
        {
            var rows = app.Cfg.tasks.Select(t => t.offer
                ? new Row { task = t, text = app.FriendLabel + " ส่งงานให้: " + t.text, grey = true, buttons = true }
                : new Row { task = t, text = t.text, check = true }).ToList();
            if (rows.Count == 0) rows.Add(new Row { text = "(ยังไม่มีงาน พิมพ์งานใหม่ด้านล่าง)" });
            mine.SetRows(rows);
        }

        public void FillTheirs()
        {
            theirBox.Text = "งานของ " + app.FriendLabel;
            var rows = app.Cfg.friendTasks.Select(t => new Row { task = t, text = t.text, grey = t.offer, tag = t.offer ? "รอรับ" : null }).ToList();
            // Gifts on their way: the friend's list doesn't show them yet
            foreach (var g in app.Cfg.giving)
                if (!app.Cfg.friendTasks.Any(t => t.id == g.id)) rows.Add(new Row { task = g, text = g.text, grey = true, tag = "กำลังส่ง" });
            if (rows.Count == 0) rows.Add(new Row { text = app.Cfg.friendName == "" ? "(รอเพื่อนเข้าร่วม...)" : "(ยังไม่มีงาน)" });
            theirs.SetRows(rows);
        }

        public bool Watching { get { return Visible && WindowState != FormWindowState.Minimized && Form.ActiveForm == this; } }

        bool online;
        public void SetStatus(bool online)
        {
            this.online = online;
            status.Text = (online ? "● เชื่อมต่อแล้ว" : "○ กำลังเชื่อมต่อ...") + "   ·   คุณ: " + app.Cfg.name + "   ·   เพื่อน: " + app.FriendLabel + "   ·   v" + Program.Version;
            status.ForeColor = online ? Theme.Online : Theme.Muted;
        }
    }

    // ---------- settings: pop-ups, start with Windows, update log ----------

    class SettingsForm : Form
    {
        public SettingsForm(App app)
        {
            var cfg = app.Cfg;
            Text = "ตั้งค่า Friendly Fire"; Icon = Program.Flame; Font = Util.UiFont(10.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(14);

            var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill };
            Controls.Add(page);
            Func<string, Label> heading = s => new Label { Text = s, AutoSize = true, Font = new Font("Segoe UI", 11.5f, FontStyle.Bold), Tag = "heading", Margin = new Padding(0, 10, 0, 4) };

            // Look: light / dark / follow Windows, and the accent colour
            page.Controls.Add(heading("หน้าตา"));
            var modeRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
            modeRow.Controls.Add(new Label { Text = "โหมด", AutoSize = true, MinimumSize = new Size(120, 0), Margin = new Padding(3, 7, 6, 0) });
            var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
            mode.Items.AddRange(Theme.ModeNames);
            mode.SelectedIndex = Math.Max(0, Array.IndexOf(Theme.Modes, cfg.themeMode));
            mode.SelectedIndexChanged += (s, e) => { cfg.themeMode = Theme.Modes[mode.SelectedIndex]; app.SetTheme(); };
            modeRow.Controls.Add(mode);
            var colorRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            colorRow.Controls.Add(new Label { Text = "สีธีม", AutoSize = true, MinimumSize = new Size(120, 0), Margin = new Padding(3, 8, 6, 0) });
            var tips = new ToolTip();
            var swatches = new List<Button>();
            var colorName = new Label { AutoSize = true, Tag = "muted", Margin = new Padding(6, 8, 0, 0) };
            for (int i = 0; i < Theme.Accents.Length; i++)
            {
                int at = i;
                var sw = new Button { Tag = "swatch", Size = new Size(30, 30), FlatStyle = FlatStyle.Flat, BackColor = Theme.AccentColors[at],
                                      Margin = new Padding(2, 2, 4, 0), Cursor = Cursors.Hand };
                sw.FlatAppearance.MouseOverBackColor = Theme.Mix(Theme.AccentColors[at], Color.White, 0.15);
                sw.FlatAppearance.MouseDownBackColor = Theme.AccentColors[at];
                tips.SetToolTip(sw, Theme.AccentNames[at]);
                sw.Click += (s, e) => { cfg.themeColor = Theme.Accents[at]; app.SetTheme(); };
                swatches.Add(sw);
                colorRow.Controls.Add(sw);
            }
            colorRow.Controls.Add(colorName);
            paintSwatches = () =>
            {
                int on = Math.Max(0, Array.IndexOf(Theme.Accents, cfg.themeColor));
                for (int i = 0; i < swatches.Count; i++)
                {
                    swatches[i].FlatAppearance.BorderSize = i == on ? 3 : 1;
                    swatches[i].FlatAppearance.BorderColor = i == on ? Theme.Text : Theme.Mix(Theme.Back, Theme.Text, 0.25);
                }
                colorName.Text = Theme.AccentNames[on];
            };
            page.Controls.Add(modeRow); page.Controls.Add(colorRow);

            // Pop-ups: all on/off, sound, and each kind under "ขั้นสูง"
            page.Controls.Add(heading("การแจ้งเตือน"));
            var all = new CheckBox { Text = "เปิดการแจ้งเตือนทั้งหมด", Checked = cfg.notifyAll, AutoSize = true };
            var sound = new CheckBox { Text = "เล่นเสียงตอนแจ้งเตือน", Checked = cfg.notifySound, AutoSize = true };
            var more = new LinkLabel { Text = "ขั้นสูง ▸", AutoSize = true, Margin = new Padding(3, 6, 3, 2) };
            var kinds = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Visible = false, Margin = new Padding(22, 0, 0, 0) };
            foreach (var k in Notice.Kinds)
            {
                string id = k[0];
                var box = new CheckBox { Text = k[1], Checked = !cfg.notifyOff.Contains(id), AutoSize = true };
                box.CheckedChanged += (s, e) =>
                {
                    cfg.notifyOff.Remove(id);
                    if (!box.Checked) cfg.notifyOff.Add(id);
                    app.Save();
                };
                kinds.Controls.Add(box);
            }
            // Pop-ups: extra time on top of the 1–8 s by text length, where they show up and how big
            Func<string, FlowLayoutPanel> optionRow = name =>
            {
                var r = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
                r.Controls.Add(new Label { Text = name, AutoSize = true, Width = 120, MinimumSize = new Size(120, 0), Margin = new Padding(3, 7, 6, 0) });
                return r;
            };
            Func<string[], int, Action<int>, ComboBox> pick = (labels, at, set) =>
            {
                var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
                c.Items.AddRange(labels);
                c.SelectedIndex = Math.Max(0, at);
                c.SelectedIndexChanged += (s, e) => { set(c.SelectedIndex); app.Save(); };
                return c;
            };
            // Pick one, or type any number of seconds to add (or take away, e.g. -2): a pop-up still shows at least 1 s
            var extras = new[] { 0, 1, 2, 3, 5, 10, Notice.Forever };
            var stayNames = new[] { "อัตโนมัติ (1–8 วินาที)", "+1 วินาที", "+2 วินาที", "+3 วินาที", "+5 วินาที", "+10 วินาที", "จนกว่าจะกดปิด" };
            var stayRow = optionRow("แสดงนาน");
            var stay = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 160 };
            stay.Items.AddRange(stayNames);
            Action showStay = () =>
            {
                int at = Array.IndexOf(extras, cfg.notifyExtra);
                if (at >= 0) stay.SelectedIndex = at;
                else { stay.SelectedIndex = -1; stay.Text = (cfg.notifyExtra > 0 ? "+" : "") + cfg.notifyExtra + " วินาที"; }
            };
            Action typed = () =>
            {
                int v;
                string x = stay.Text.Replace("วินาที", "").Replace(" ", "").Replace("+", "");
                if (Array.IndexOf(stayNames, stay.Text) < 0 && int.TryParse(x, out v)) { cfg.notifyExtra = Math.Max(-60, Math.Min(3600, v)); app.Save(); }
                showStay();
            };
            showStay();
            stay.SelectionChangeCommitted += (s, e) => { if (stay.SelectedIndex >= 0) { cfg.notifyExtra = extras[stay.SelectedIndex]; app.Save(); } };
            stay.Leave += (s, e) => typed();
            stay.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; typed(); } };
            stayRow.Controls.Add(stay);
            stayRow.Controls.Add(new Label { Text = "(พิมพ์เองได้ เช่น +4 หรือ -2 · อย่างน้อย 1 วินาที)", AutoSize = true, Tag = "muted", Margin = new Padding(6, 7, 0, 0) });
            var placeRow = optionRow("ตำแหน่ง");
            placeRow.Controls.Add(pick(Notice.PlaceNames, Array.IndexOf(Notice.Places, cfg.notifyPlace), i => cfg.notifyPlace = Notice.Places[i]));
            var sizeRow = optionRow("ขนาด");
            sizeRow.Controls.Add(pick(Notice.SizeNames, cfg.notifySize, i => cfg.notifySize = i));
            var tryIt = new Button { Text = "ลองดูป๊อปอัป", AutoSize = true, Height = 30, Margin = new Padding(12, 3, 0, 0) };
            tryIt.Click += (s, e) => Notice.Preview();
            sizeRow.Controls.Add(tryIt);
            Action enable = () => { sound.Enabled = kinds.Enabled = stayRow.Enabled = placeRow.Enabled = sizeRow.Enabled = all.Checked; };
            all.CheckedChanged += (s, e) => { cfg.notifyAll = all.Checked; app.Save(); enable(); };
            sound.CheckedChanged += (s, e) => { cfg.notifySound = sound.Checked; app.Save(); };
            more.LinkClicked += (s, e) => { kinds.Visible = !kinds.Visible; more.Text = kinds.Visible ? "ขั้นสูง ▾" : "ขั้นสูง ▸"; };
            enable();
            // Paused from a pop-up (🔕): say until when, with a way to turn it back on
            var paused = new LinkLabel { AutoSize = true, Margin = new Padding(3, 2, 3, 2), Visible = Notice.Muted };
            if (Notice.Muted)
            {
                string until = Util.Local(cfg.notifyMuteUntil).ToString(Util.Local(cfg.notifyMuteUntil).Date == DateTime.Today ? "HH:mm" : "d/M HH:mm", CultureInfo.InvariantCulture);
                paused.Text = "🔕 ปิดการแจ้งเตือนชั่วคราวถึง " + until + "   เปิดตอนนี้";
                paused.LinkArea = new LinkArea(paused.Text.Length - "เปิดตอนนี้".Length, "เปิดตอนนี้".Length);
                paused.LinkClicked += (s, e) => { cfg.notifyMuteUntil = 0; app.Save(); paused.Visible = false; };
            }
            page.Controls.AddRange(new Control[] { all, paused, sound, stayRow, placeRow, sizeRow, more, kinds });

            // What a friend's "ทำสำเร็จ" pop-up offers: the 4 reaction buttons (pick from the list or type/paste any emoji),
            // a box to type a reply, or nothing
            page.Controls.Add(heading("ปุ่มรีแอคชัน"));
            var answerRow = optionRow("บนป๊อปอัป");
            answerRow.Controls.Add(pick(Notice.AnswerModeNames, Array.IndexOf(Notice.AnswerModes, cfg.answerMode), i => { cfg.answerMode = Notice.AnswerModes[i]; showAnswer(); }));
            page.Controls.Add(answerRow);
            var emojiHint = new Label { Text = "เลือกจากรายการ หรือพิมพ์/วางอีโมจิเองก็ได้", AutoSize = true, Tag = "muted" };
            var replyHint = new Label { Text = "ป๊อปอัปจะมีช่องพิมพ์ตอบกลับ กด Enter หรือ ส่ง แล้วเพื่อนจะเห็นข้อความ (ตอบกลับไปมาได้)", AutoSize = true, Tag = "muted", MaximumSize = new Size(440, 0) };
            page.Controls.Add(emojiHint); page.Controls.Add(replyHint);
            var slots = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            var choices = new object[] { "🔥", "😎", "👍", "❓", "😂", "🤣", "😭", "😱", "🤯", "🥳", "💀", "🫡", "🙏", "👏", "💪", "❤️", "💯", "🎉", "🤔", "😵‍💫", "🐐", "👀", "🫠", "😴", "⭐", "🚀" };
            while (cfg.reactions.Count < 4) cfg.reactions.Add(Notice.DefaultReactions[cfg.reactions.Count]);
            for (int i = 0; i < 4; i++)
            {
                int slot = i;
                var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Font = Util.EmojiFont(14), Width = 76, MaxDropDownItems = 12, Text = cfg.reactions[slot] };
                c.Items.AddRange(choices);
                c.TextChanged += (s, e) =>
                {
                    string v = c.Text.Trim();
                    if (v == "" || v.Length > 12) return;   // keep the last good one
                    cfg.reactions[slot] = v;
                    app.Save();
                };
                c.Leave += (s, e) => c.Text = cfg.reactions[slot];
                slots.Controls.Add(c);
            }
            var reset = new LinkLabel { Text = "ค่าเริ่มต้น", AutoSize = true, Margin = new Padding(8, 12, 0, 0) };
            reset.LinkClicked += (s, e) => { for (int i = 0; i < 4; i++) slots.Controls[i].Text = Notice.DefaultReactions[i]; };
            slots.Controls.Add(reset);
            page.Controls.Add(slots);
            showAnswer = () => { emojiHint.Visible = slots.Visible = cfg.answerMode == "react"; replyHint.Visible = cfg.answerMode == "reply"; };
            showAnswer();

            page.Controls.Add(heading("ทั่วไป"));
            var start = new CheckBox { Text = "เปิดพร้อม Windows", Checked = cfg.startWithWindows, AutoSize = true, Enabled = !app.TestCopy };
            start.CheckedChanged += (s, e) => app.SetStartup(start.Checked);
            page.Controls.Add(start);

            // How long a finished task stays (yours and the friend's, on this PC) after you've seen it
            var keepRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            keepRow.Controls.Add(new Label { Text = "งานที่เสร็จแล้วหายเองหลังจากเห็น", AutoSize = true, Margin = new Padding(3, 7, 6, 0) });
            var hours = new[] { 1, 6, 12, 24, 72, 168, 0 };
            var names = new[] { "1 ชั่วโมง", "6 ชั่วโมง", "12 ชั่วโมง", "1 วัน", "3 วัน", "7 วัน", "ไม่หายเอง" };
            var keep = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
            keep.Items.AddRange(names);
            int keepAt = Array.IndexOf(hours, cfg.doneKeepHours);
            keep.SelectedIndex = keepAt >= 0 ? keepAt : 3;
            keep.SelectedIndexChanged += (s, e) => { cfg.doneKeepHours = hours[keep.SelectedIndex]; app.Save(); app.Tidy(); };
            keepRow.Controls.Add(keep);
            page.Controls.Add(keepRow);

            // What changed in each version
            page.Controls.Add(heading("บันทึกการอัปเดต  (ตอนนี้ v" + Program.Version + ")"));
            var log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 440, Height = 190,
                                    Text = Program.Changelog.Replace("\n", "\r\n"), Font = Util.UiFont(10) };
            page.Controls.Add(log);

            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            var check = new Button { Text = "ตรวจหาอัปเดต", AutoSize = true, Height = 32 };
            check.Click += (s, e) => app.CheckUpdate(true);
            var close = new Button { Text = "ปิด", Width = 90, Height = 32, DialogResult = DialogResult.Cancel };
            close.Click += (s, e) => Close();
            row.Controls.Add(check); row.Controls.Add(close);
            page.Controls.Add(row);
            CancelButton = close;
            Shown += (s, e) => log.Select(0, 0);
            Retheme();
        }

        Action paintSwatches, showAnswer;
        public void Retheme() { Theme.Apply(this); if (paintSwatches != null) paintSwatches(); }
    }

    static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    }

    // ---------- the app: tray icon, state, relay, updates ----------

    class App : ApplicationContext
    {
        public Config Cfg;
        readonly string cfgPath;
        readonly bool testCopy;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly MainForm main;
        readonly Relay relay;
        readonly Control ui = new Control();
        readonly System.Windows.Forms.Timer syncTimer = new System.Windows.Forms.Timer(), updateTimer = new System.Windows.Forms.Timer(),
                                            tidyTimer = new System.Windows.Forms.Timer();
        bool updating;
        ToolStripMenuItem startItem;
        SettingsForm settings;
        public bool TestCopy { get { return testCopy; } }

        public string FriendLabel { get { return Cfg.friendName == "" ? "เพื่อน" : Cfg.friendName; } }

        public App(string dataDir, bool testCopy, string name, string code, bool justUpdated)
        {
            this.testCopy = testCopy;
            Directory.CreateDirectory(dataDir);
            cfgPath = Path.Combine(dataDir, "friendlyfire.json");
            Cfg = File.Exists(cfgPath) ? Util.Json.Deserialize<Config>(File.ReadAllText(cfgPath, Encoding.UTF8)) : new Config();
            if (Cfg.clientId == "") Cfg.clientId = Util.RandomCode(12);
            Theme.Set(Cfg.themeMode, Cfg.themeColor);
            if (!Cfg.stayFixed) { if (Cfg.notifyExtra == -1) Cfg.notifyExtra = Notice.Forever; Cfg.stayFixed = true; }
            ui.CreateControl();

            // First run: name and pair (or from the command line, for testing)
            bool created = false, firstRun = Cfg.code == "";
            if (Cfg.code == "" && name != null && Program.CleanCode(code) != null) { Cfg.name = name; Cfg.code = Program.CleanCode(code); }
            if (Cfg.code == "")
            {
                using (var p = new PairForm(Environment.UserName))
                {
                    if (p.ShowDialog() != DialogResult.OK) { Environment.Exit(0); }
                    Cfg.name = p.PlayerName; Cfg.code = p.Code; created = p.MadeNew;
                }
            }
            Save();
            Notice.Settings = Cfg;
            Notice.SaveSettings = Save;

            main = new MainForm(this);
            relay = new Relay("friendlyfire-" + Cfg.code.ToLowerInvariant(), Cfg.clientId);
            relay.Received += (m, id, time) => ui.BeginInvoke((Action)(() => OnMessage(m, time)));
            relay.Connected += on => ui.BeginInvoke((Action)(() => main.SetStatus(on)));
            relay.Start();
            relay.Publish(Make("sync", null));   // let the friend see my name and list

            var menu = new ContextMenuStrip();
            menu.Items.Add("เปิด Friendly Fire", null, (s, e) => ShowMain());
            menu.Items.Add("รหัสเชิญ...", null, (s, e) => ShowCode());
            menu.Items.Add("ตั้งค่า...", null, (s, e) => ShowSettings());
            startItem = new ToolStripMenuItem("เปิดพร้อม Windows") { Checked = Cfg.startWithWindows, Enabled = !testCopy };
            startItem.Click += (s, e) => SetStartup(!Cfg.startWithWindows);
            menu.Items.Add(startItem);
            menu.Items.Add("ตรวจหาอัปเดต", null, (s, e) => CheckUpdate(true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("ออก", null, (s, e) => Quit());
            tray.Icon = Program.Flame;
            tray.Text = "Friendly Fire" + (testCopy ? " (" + Cfg.name + ")" : "");
            tray.ContextMenuStrip = menu;
            Theme.Style(menu);
            Theme.Changed += () => { main.Retheme(); if (settings != null && !settings.IsDisposed) settings.Retheme(); Theme.Style(menu); };
            // "ตาม Windows": follow Windows when it switches between light and dark
            SystemEvents.UserPreferenceChanged += (s, e) => { if (Cfg.themeMode == "system") ui.BeginInvoke((Action)(() => Theme.Set(Cfg.themeMode, Cfg.themeColor))); };
            tray.DoubleClick += (s, e) => ShowMain();
            tray.Visible = true;

            ApplyStartup();
            // Re-send my list every 6 hours: ntfy only keeps messages ~12 hours
            syncTimer.Interval = 6 * 3600 * 1000;
            syncTimer.Tick += (s, e) => relay.Publish(Make("sync", null));
            syncTimer.Start();
            // Every minute: note what's on screen, remove finished tasks seen more than a day ago
            tidyTimer.Interval = 60 * 1000;
            tidyTimer.Tick += (s, e) => { MarkSeen(false); Tidy(); };
            tidyTimer.Start();
            Tidy();
            if (!testCopy)
            {
                Updater.CleanUp();
                updateTimer.Interval = 10 * 1000;   // first check right after start (once the network is up), then every hour
                updateTimer.Tick += (s, e) => { updateTimer.Interval = 3600 * 1000; Updater.CleanUp(); CheckUpdate(false); };
                updateTimer.Start();
            }

            if (Program.CheckNow) CheckUpdate(true);
            if (firstRun || testCopy) ShowMain();
            if (created) ShowCode();
            if (justUpdated) Notice.Show("update", "Friendly Fire อัปเดตแล้ว", "ตอนนี้เป็นเวอร์ชัน " + Program.Version, null);
        }

        public Msg Make(string type, string text)
        {
            // All my tasks (seenAt stays on this PC: the friend's PC decides when a finished task goes away there)
            var list = Cfg.tasks.Select(t => new TaskItem { id = t.id, text = Short(t.text, 80), done = t.done, doneAt = t.doneAt, offer = t.offer })
                                .ToList();
            var gifts = Cfg.giving.Select(t => new TaskItem { id = t.id, text = Short(t.text, 300) }).ToList();
            var got = Cfg.giftsDone.Skip(Math.Max(0, Cfg.giftsDone.Count - 50)).ToList();
            return new Msg { from = Cfg.clientId, name = Cfg.name, type = type, text = text, time = Util.NowMs(), tasks = list, gifts = gifts, got = got, ver = Program.Version };
        }

        static string Short(string s, int n) { return s.Length > n ? s.Substring(0, n) + "…" : s; }

        // Drag inside my list: put the task at position "at" (counted before it was taken out)
        public void MoveTask(TaskItem t, int at)
        {
            int from = Cfg.tasks.IndexOf(t);
            if (from < 0) return;
            if (at > from) at--;
            at = Math.Max(0, Math.Min(at, Cfg.tasks.Count - 1));
            if (at == from) return;
            Cfg.tasks.RemoveAt(from);
            Cfg.tasks.Insert(at, t);
            Changed("sync", null);
        }

        // Drag onto the friend's list: the task leaves my list and waits on theirs for ✓ / ✗
        public void GiveTask(TaskItem t)
        {
            if (Cfg.friendName == "") { Notice.Show("ยังไม่มีเพื่อนในคู่", "ส่งรหัสเชิญให้เพื่อนก่อน", null); return; }
            Cfg.tasks.Remove(t);
            Cfg.giving.Add(new TaskItem { id = t.id, text = t.text });
            Changed("give", t.text);
            main.FillTheirs();
            Notice.Show("sent", "ส่งงานให้ " + FriendLabel + " แล้ว 🎁", t.text, null);
        }

        // ✓ keeps a gift as my own task, ✗ throws it away; the friend gets told either way
        public void TakeGift(TaskItem t, bool take)
        {
            if (take) t.offer = false; else Cfg.tasks.Remove(t);
            if (!Cfg.giftsDone.Contains(t.id)) Cfg.giftsDone.Add(t.id);
            if (Cfg.giftsDone.Count > 300) Cfg.giftsDone.RemoveRange(0, Cfg.giftsDone.Count - 300);
            Changed(take ? "accept" : "reject", t.text);
        }

        // Something changed on my side: save, redraw, tell the friend
        public void Changed(string type, string text)
        {
            Save();
            main.FillMine();
            relay.Publish(Make(type, text));
        }

        void OnMessage(Msg m, long time)
        {
            if (!string.IsNullOrEmpty(m.name)) Cfg.friendName = m.name;
            if (m.tasks != null && m.time >= Cfg.friendTasksTime) { MergeFriend(m.tasks); Cfg.friendTasksTime = m.time; }
            // My gifts that reached the friend's list (or were already handled there) don't need sending any more
            Cfg.giving.RemoveAll(g => (m.tasks != null && m.tasks.Any(t => t.id == g.id)) || (m.got != null && m.got.Contains(g.id)));
            // Gifts for me: add each one once, on top, waiting for ✓ / ✗
            var newGifts = new List<TaskItem>();
            if (m.gifts != null)
                foreach (var g in m.gifts)
                    if (g.id != null && g.text != null && !Cfg.giftsDone.Contains(g.id) && !Cfg.tasks.Any(t => t.id == g.id))
                    {
                        var t = new TaskItem { id = g.id, text = g.text, offer = true };
                        Cfg.tasks.Insert(newGifts.Count, t);
                        newGifts.Add(t);
                    }
            bool fresh = time > Cfg.lastSeen;   // not shown before (messages are replayed after a restart)
            if (fresh) Cfg.lastSeen = time;
            FollowVersion(m.ver, fresh);
            Save();
            main.FillTheirs();
            MarkSeen(false);
            if (newGifts.Count > 0)
            {
                main.FillMine();
                relay.Publish(Make("sync", null));   // the friend sees them as "รอรับ" and stops sending
                foreach (var g in newGifts) Notice.Show("gift", "🎁 " + FriendLabel + " ส่งงานให้คุณ", g.text + "\n(เปิดแอปแล้วกด ✓ รับ หรือ ✗ ทิ้ง)", null);
            }
            main.SetStatus(true);
            if (!fresh) return;

            string when = Util.NowSec() - time > 300 ? "  (" + Util.Local(time).ToString("HH:mm", CultureInfo.InvariantCulture) + ")" : "";
            if (m.type == "done")
                Notice.Show("done", "🔥 " + FriendLabel + " ทำสำเร็จ!", m.text + when, AnswerFriend);
            else if (m.type == "react")
                Notice.Show("react", FriendLabel + " ส่ง " + m.text + " ให้คุณ" + when, null, null);
            else if (m.type == "reply")
                Notice.Show("reply", "💬 " + FriendLabel + " ตอบกลับ" + when, m.text, AnswerFriend);   // can answer back: a little chat
            else if (m.type == "accept")
                Notice.Show("accept", "👍 " + FriendLabel + " รับงานที่คุณส่งแล้ว" + when, m.text, null);
            else if (m.type == "reject")
                Notice.Show("reject", FriendLabel + " ไม่รับงานที่คุณส่ง" + when, m.text, null);
        }

        // An emoji or a typed answer from a pop-up goes to the friend
        void AnswerFriend(string text, bool reply)
        {
            relay.Publish(Make(reply ? "reply" : "react", text));
            if (reply) Notice.Show("sent", "ตอบกลับ " + FriendLabel + " แล้ว 💬", text, null);
            else Notice.Show("sent", "ส่ง " + text + " ให้ " + FriendLabel + " แล้ว", null, null);
        }

        // The friend's new list, but finished tasks stay here until they've been seen for a day (or removed here)
        void MergeFriend(List<TaskItem> snap)
        {
            var old = new Dictionary<string, TaskItem>();
            foreach (var t in Cfg.friendTasks) old[t.id] = t;
            var ids = new HashSet<string>();
            var list = new List<TaskItem>();
            foreach (var t in snap)
            {
                if (t.id == null || !ids.Add(t.id)) continue;
                if (t.done && Cfg.friendHidden.Contains(t.id)) continue;
                if (!t.done) Cfg.friendHidden.Remove(t.id);   // unticked again: show it again
                TaskItem o;
                t.seenAt = old.TryGetValue(t.id, out o) && o.done == t.done ? o.seenAt : 0;
                list.Add(t);
            }
            // Finished tasks the friend deleted (or an older version stopped sending) stay until they expire here
            foreach (var o in Cfg.friendTasks) if (o.done && !ids.Contains(o.id)) list.Add(o);
            Cfg.friendTasks = list;
        }

        // Finished friend tasks count as read once the window is open in front of you
        public void MarkSeen(bool active)
        {
            if (!active && !main.Watching) return;
            bool changed = false;
            foreach (var t in Cfg.friendTasks) if (t.done && t.seenAt == 0) { t.seenAt = Util.NowSec(); changed = true; }
            if (changed) Save();
        }

        // Remove finished tasks some time (Settings, 1 day by default) after they were seen on this PC
        public void Tidy()
        {
            foreach (var t in Cfg.tasks) if (t.done && t.seenAt == 0) t.seenAt = t.doneAt;   // from v1.0.0
            if (Cfg.doneKeepHours <= 0) return;
            long cutoff = Util.NowSec() - Cfg.doneKeepHours * 3600L;
            int mineGone = Cfg.tasks.RemoveAll(t => t.done && t.seenAt < cutoff);
            var expired = Cfg.friendTasks.Where(t => t.done && t.seenAt > 0 && t.seenAt < cutoff).ToList();
            foreach (var t in expired) HideFriend(t);
            if (mineGone == 0 && expired.Count == 0) return;
            Save();
            if (mineGone > 0) main.FillMine();
            if (expired.Count > 0) main.FillTheirs();
        }

        void HideFriend(TaskItem t)
        {
            Cfg.friendTasks.Remove(t);
            if (!Cfg.friendHidden.Contains(t.id)) Cfg.friendHidden.Add(t.id);
            if (Cfg.friendHidden.Count > 300) Cfg.friendHidden.RemoveRange(0, Cfg.friendHidden.Count - 300);
        }

        public void HideFriendDone()
        {
            foreach (var t in Cfg.friendTasks.Where(x => x.done).ToList()) HideFriend(t);
            Save();
            main.FillTheirs();
        }

        // Dragged out of the window: gone, with a moment to change your mind
        public void DiscardTask(TaskItem t)
        {
            int at = Cfg.tasks.IndexOf(t);
            if (at < 0) return;
            Cfg.tasks.RemoveAt(at);
            Changed("sync", null);
            Notice.ShowUndo("ทิ้งงานแล้ว 🗑", t.text, () =>
            {
                if (Cfg.tasks.Contains(t)) return;
                Cfg.tasks.Insert(Math.Min(at, Cfg.tasks.Count), t);
                Changed("sync", null);
            });
        }

        public void HideFriendTask(TaskItem t)
        {
            HideFriend(t);
            Save();
            main.FillTheirs();
        }

        // Both PCs stay on the same version: when the friend runs a newer one, update (and restart) right away;
        // when theirs is older, send my list again so they see my version (ntfy keeps it for them if they're off)
        DateTime followedAt = DateTime.MinValue, nudgedAt = DateTime.MinValue;
        void FollowVersion(string ver, bool fresh)
        {
            Version theirs, mine = new Version(Program.Version);
            if (string.IsNullOrEmpty(ver) || !Version.TryParse(ver, out theirs)) return;
            if (theirs > mine && (!testCopy || Program.FollowUpdates) && DateTime.Now - followedAt > TimeSpan.FromMinutes(10))
            {
                followedAt = DateTime.Now;
                CheckUpdate(false);
            }
            else if (theirs < mine && fresh && DateTime.Now - nudgedAt > TimeSpan.FromMinutes(10))
            {
                nudgedAt = DateTime.Now;
                relay.Publish(Make("sync", null));
            }
        }

        void ShowMain()
        {
            main.Show();
            if (main.WindowState == FormWindowState.Minimized) main.WindowState = FormWindowState.Normal;
            main.Activate();
        }

        void ShowCode()
        {
            var f = new Form { Text = "รหัสเชิญ", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Icon = Program.Flame,
                               StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(400, 290), Font = Util.UiFont(10.5f), TopMost = true };
            f.Controls.Add(new Label { Text = "ส่งรหัสนี้ให้เพื่อน แล้วให้เพื่อนเลือก \"ใส่รหัสเชิญจากเพื่อน\"\r\n(ใครมีรหัสนี้จะเห็นงานของคุณ ส่งให้เพื่อนคนเดียวพอ)",
                                       Location = new Point(16, 14), Size = new Size(370, 50) });
            var box = new TextBox { Text = Cfg.code, ReadOnly = true, Font = new Font("Consolas", 14), Location = new Point(16, 70), Width = 260 };
            var copy = new Button { Text = "คัดลอก", Location = new Point(284, 68), Size = new Size(100, 34) };
            copy.Click += (s, e) => { Clipboard.SetText(Cfg.code); copy.Text = "คัดลอกแล้ว ✓"; };

            // Both made a pair? One of you enters the other's code here
            f.Controls.Add(new Label { Text = "หรือ ใส่รหัสเชิญจากเพื่อน (ถ้าต่างคนต่างสร้างคู่):", Location = new Point(16, 124), Size = new Size(370, 24) });
            var join = new TextBox { Font = new Font("Consolas", 14), Location = new Point(16, 152), Width = 260 };
            var joinBtn = new Button { Text = "เข้าร่วม", Location = new Point(284, 150), Size = new Size(100, 34) };
            var note = new Label { ForeColor = Color.Firebrick, Location = new Point(16, 192), Size = new Size(370, 24) };
            joinBtn.Click += (s, e) =>
            {
                string c = Program.CleanCode(join.Text);
                if (c == null) { note.Text = "รหัสไม่ถูกต้อง (หน้าตาแบบ FF-XXXXXXXXXXXXXXXX)"; return; }
                if (c == Cfg.code) { note.Text = "นี่คือรหัสของคุณเอง ใส่รหัสของเพื่อน"; return; }
                f.Close();
                SwitchCode(c);
            };
            var ok = new Button { Text = "ปิด", Location = new Point(284, 240), Size = new Size(100, 34), DialogResult = DialogResult.OK };
            ok.Click += (s, e) => f.Close();
            f.Controls.AddRange(new Control[] { box, copy, join, joinBtn, note, ok });
            f.AcceptButton = joinBtn;
            Theme.Apply(f);
            f.Show();
        }

        // Join the friend's pair instead: forget the old friend, then start again on the new channel
        void SwitchCode(string code)
        {
            Cfg.code = code;
            Cfg.friendName = "";
            Cfg.friendTasks.Clear();
            Cfg.friendHidden.Clear();
            Cfg.giving.Clear();
            Cfg.tasks.RemoveAll(t => t.offer);
            Cfg.friendTasksTime = 0;
            Cfg.lastSeen = Util.NowSec();   // no pop-ups for what the friend did before we joined
            Save();
            relay.Stop();
            tray.Visible = false;
            Program.ReleaseLock();
            Process.Start(Application.ExecutablePath, Program.RestartArgs + " --restart");
            ExitThread();
        }

        public void SetTheme()
        {
            Theme.Set(Cfg.themeMode, Cfg.themeColor);
            if (!Cfg.stayFixed) { if (Cfg.notifyExtra == -1) Cfg.notifyExtra = Notice.Forever; Cfg.stayFixed = true; }
            Save();
        }

        public void ShowSettings()
        {
            if (settings == null || settings.IsDisposed) settings = new SettingsForm(this);
            settings.Show();
            settings.Activate();
        }

        public void SetStartup(bool on)
        {
            Cfg.startWithWindows = on;
            startItem.Checked = on;
            Save();
            ApplyStartup();
        }

        void ApplyStartup()
        {
            if (testCopy) return;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (Cfg.startWithWindows) key.SetValue("FriendlyFire", "\"" + Application.ExecutablePath + "\"");
                    else if (key.GetValue("FriendlyFire") != null) key.DeleteValue("FriendlyFire");
                }
            }
            catch { }
        }

        public void CheckUpdate(bool byHand)
        {
            if (updating) return;
            updating = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string newExe = null, error = null, version = null, blocked = null;
                try
                {
                    newExe = Updater.Download(byHand ? null : Cfg.blockedVersion, out version);   // checking by hand tries a blocked version again
                    if (newExe != null && (blocked = Updater.Probe(newExe)) != null) { try { File.Delete(newExe); } catch { } newExe = null; }
                }
                catch (Exception ex) { error = ex.Message; }
                ui.BeginInvoke((Action)(() =>
                {
                    updating = false;
                    if (blocked != null) { Blocked(version, blocked); return; }
                    if (newExe != null) { Cfg.blockedVersion = ""; Save(); Restart(newExe, version); }
                    else if (byHand) Notice.Show(error == null ? "เป็นเวอร์ชันล่าสุดแล้ว" : "ตรวจอัปเดตไม่ได้", error == null ? "v" + Program.Version : error, null);
                }));
            });
        }

        void Restart(string newExe, string version)
        {
            try { Updater.Swap(newExe); }
            catch (Exception ex) { Notice.Show("อัปเดตไม่สำเร็จ", ex.Message, null); return; }
            // Start the new copy before letting go of anything (it waits for this one to exit); if it does not start, keep this version
            try { Process.Start(Application.ExecutablePath, Program.RestartArgs + " --updated"); }
            catch (Exception ex)
            {
                try { Updater.SwapBack(); } catch { }
                Blocked(version, ex.Message);
                return;
            }
            relay.Stop();
            tray.Visible = false;
            Program.ReleaseLock();
            ExitThread();
        }

        // Windows would not run the new version: stay on this one, do not try that version again by itself, and say why
        void Blocked(string version, string why)
        {
            Cfg.blockedVersion = version ?? "";
            Save();
            Notice.Show("อัปเดต v" + version + " ไม่ได้",
                "Windows ไม่ยอมเปิดไฟล์ใหม่ (อาจเป็น Smart App Control) เลยใช้ v" + Program.Version + " ต่อไปก่อน\n" +
                "แก้แล้วกด ⚙ ตั้งค่า → ตรวจหาอัปเดต เพื่อลองใหม่\n(" + why + ")", null);
        }

        void Quit()
        {
            relay.Stop();
            tray.Visible = false;
            main.Exiting = true;
            ExitThread();
        }

        public void Save()
        {
            string tmp = cfgPath + ".tmp";
            File.WriteAllText(tmp, Util.Json.Serialize(Cfg), new UTF8Encoding(false));
            if (File.Exists(cfgPath)) File.Replace(tmp, cfgPath, null); else File.Move(tmp, cfgPath);
        }
    }

    static class Program
    {
        public const string Version = "1.1.0";
        public static Icon Flame;
        static Mutex mutex;
        public static string RestartArgs = "";   // the same -Data/-Name/-Code after an update
        public static bool CheckNow;              // -CheckUpdate: look for an update right away (also in test copies)
        public static bool FollowUpdates;         // -FollowUpdates: a test copy also updates when the friend runs a newer version

        // Shown in Settings → บันทึกการอัปเดต (newest first; add a line with each release)
        public const string Changelog =
            "-----30/10/2569-----\n" +
            "v1.1.0\n" +
            "• โหมดมืด: ตั้งค่า → หน้าตา เลือก สว่าง / มืด / ตาม Windows (เปลี่ยนตาม Windows ได้ทันที)\n" +
            "• สีธีม 6 แบบ: ส้มไฟ, ฟ้าทะเล, ม่วงลาเวนเดอร์, ชมพูซากุระ, เขียวมิ้นต์, เทากราไฟต์ (ใช้กับปุ่ม, หัวข้อ, ป๊อปอัป, เส้นตอนลาก)\n" +
            "• แสดงนาน: พิมพ์จำนวนวินาทีเองได้ (เช่น +4 หรือ -2) ป๊อปอัปแสดงอย่างน้อย 1 วินาที\n" +
            "• เปลี่ยนเวลาแสดงแล้ว ป๊อปอัปที่ขึ้นอยู่เปลี่ยนตามทันที และปุ่ม \"ลองดูป๊อปอัป\" กดซ้ำได้ (แสดงตามค่าตอนนี้ แล้วหายไปตามปกติ)\n" +
            "• ตั้งค่า → ปุ่มรีแอคชัน → บนป๊อปอัป: เลือก รีแอคชัน หรือ ไม่มี (ไม่มีปุ่มบนป๊อปอัป)\n\n" +
            "-----29/10/2569-----\n" +
            "v1.0.9\n" +
            "• อัปเดตปลอดภัยขึ้น: ลองเปิดไฟล์ใหม่ก่อนสลับ ถ้า Windows บล็อก (เช่น Smart App Control) จะใช้เวอร์ชันเดิมต่อและเด้งบอก ไม่หายไปเฉย ๆ\n\n" +
            "v1.0.8\n" +
            "• แก้ภาพกระพริบตอนลากงานผ่านงานอื่น\n" +
            "• ป๊อปอัปที่ซ้ำกัน (กดรัว ๆ) ขึ้นแค่ครั้งเดียว และกด ✕ ครั้งเดียวปิดป๊อปอัปที่รอคิวอยู่ทั้งหมด (มีบอก +N รอ)\n\n" +
            "v1.0.7\n" +
            "• ชี้เมาส์ที่งานแล้วงานยกขึ้นเล็กน้อย, งานที่ลากอยู่มีสีแดงอ่อน\n" +
            "• งานที่เสร็จเป็นสีเทา, งานที่เพื่อนส่งมาเป็นสีเขียว\n" +
            "• ลากงานออกนอกหน้าต่างเพื่อทิ้ง (มีปุ่มเลิกทำ)\n" +
            "• คลิกขวาฝั่งเพื่อน: ลบงานที่เสร็จแล้วทั้งหมดของเพื่อน\n" +
            "• ตั้งค่าได้ว่างานที่เสร็จแล้วจะหายเองหลังกี่ชั่วโมง/วัน\n" +
            "• ป๊อปอัปแสดง 1–8 วินาทีตามความยาวข้อความ และตั้งเวลาเพิ่มได้ (+1 ถึง +10 วินาที หรือจนกว่าจะกดปิด)\n" +
            "• เลือกตำแหน่ง (มุมจอ/กลางบน/กลางล่าง) และขนาดของป๊อปอัปได้ พร้อมปุ่มลองดู\n\n" +
            "v1.0.6\n" +
            "• ติ๊กเสร็จได้เฉพาะตอนกดในช่องติ๊กเท่านั้น (กันเผลอกด)\n" +
            "• แก้แถบสีค้างหลังลากงานแล้วปล่อย\n\n" +
            "v1.0.5\n" +
            "• อัปเดตพร้อมกันสองฝั่ง: พอเครื่องหนึ่งอัปเดต อีกเครื่องจะอัปเดตและเปิดใหม่ทันที (ถ้าปิดเครื่องอยู่ จะอัปเดตทันทีที่เปิดแอป)\n" +
            "• ตรวจอัปเดตทันทีตอนเปิดแอป และทุก 1 ชั่วโมง\n" +
            "• รีแอคชันตั้งต้น: 🔥 😎 👍 ❓ และเลือกปุ่มรีแอคชันเองได้ในตั้งค่า\n\n" +
            "v1.0.4\n" +
            "• เอาเมาส์ไปชี้ที่การแจ้งเตือน จะเห็นปุ่ม 🔕 และ ✕ (ปกติซ่อนไว้)\n" +
            "• 🔕 ปิดการแจ้งเตือน 1 ชั่วโมง, 1 วัน หรือไม่แจ้งเตือนแบบนี้อีก (เปิดคืนได้ในตั้งค่า)\n" +
            "• รีแอคชันใหม่: 👍 🔥 😎 ❓\n\n" +
            "v1.0.3\n" +
            "• ปุ่ม ⚙ ตั้งค่า: เปิด/ปิดการแจ้งเตือนทั้งหมด เปิด/ปิดเสียง และเลือกทีละอย่างใน \"ขั้นสูง\"\n" +
            "• ดูบันทึกการอัปเดตได้ในหน้าตั้งค่า\n" +
            "• การแจ้งเตือนเมื่อเพื่อนรับ/ไม่รับงานที่คุณส่ง ปิดไว้ตั้งต้น (เปิดได้ใน \"ขั้นสูง\")\n\n" +
            "v1.0.2\n" +
            "• ข้อความยาวขึ้นบรรทัดใหม่ ไม่ทะลุกรอบ\n" +
            "• ลากงานขึ้นลงเพื่อเรียงลำดับ\n" +
            "• ลากงานไปวางฝั่งเพื่อนเพื่อส่งให้ เพื่อนกด ✓ รับ หรือ ✗ ทิ้ง\n\n" +
            "v1.0.1\n" +
            "• ถ้าต่างคนต่างสร้างคู่ ใส่รหัสของเพื่อนได้ในหน้า \"รหัสเชิญ...\"\n" +
            "• งานที่เสร็จไม่หายทันที: หายเอง 24 ชั่วโมงหลังเห็น หรือคลิกขวาลบ (แต่ละเครื่องแยกกัน)\n\n" +
            "v1.0.0\n" +
            "• เวอร์ชันแรก: รายการงาน, ติ๊กแล้วเพื่อนเด้ง, ปุ่ม \"เสร็จแล้ว!\", ส่งรีแอคชัน, อัปเดตตัวเองจาก GitHub";

        public static string CleanCode(string s)
        {
            if (s == null) return null;
            s = s.Trim().ToUpperInvariant().Replace(" ", "");
            if (!s.StartsWith("FF-")) s = "FF-" + s;
            return System.Text.RegularExpressions.Regex.IsMatch(s, "^FF-[A-Z0-9]{16}$") ? s : null;
        }

        public static void ReleaseLock() { try { mutex.ReleaseMutex(); } catch { } }

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--probe") { Environment.Exit(Updater.ProbeCode); return; }   // the updater checking Windows lets this exe run
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;   // ntfy.sh and GitHub need TLS 1.2

            string dataDir = null, name = null, code = null;
            bool updated = false, restarted = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "-data" && i + 1 < args.Length) dataDir = args[++i];
                else if (a == "-name" && i + 1 < args.Length) name = args[++i];
                else if (a == "-code" && i + 1 < args.Length) code = args[++i];
                else if (a == "--updated") updated = true;
                else if (a == "--restart") restarted = true;
                else if (a == "-checkupdate") CheckNow = true;
                else if (a == "-followupdates") FollowUpdates = true;
            }
            foreach (string x in args) if (x != "--updated" && x != "--restart") RestartArgs += "\"" + x + "\" ";
            bool testCopy = dataDir != null;
            if (dataDir == null) dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FriendlyFire");
            dataDir = Path.GetFullPath(dataDir);

            // One copy per data folder; after an update the new copy waits for the old one to exit
            bool owned;
            mutex = new Mutex(false, "FriendlyFire-" + dataDir.ToLowerInvariant().GetHashCode().ToString("x"), out owned);
            try { if (!mutex.WaitOne(updated || restarted ? 20000 : 0)) return; }
            catch (AbandonedMutexException) { }   // the old copy was killed: the lock is ours now

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Flame = MakeFlame();
            Application.Run(new App(dataDir, testCopy, name, code, updated));
        }

        static Icon MakeFlame()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var outer = new GraphicsPath())
                using (var inner = new GraphicsPath())
                {
                    outer.AddBezier(16, 1, 30, 12, 28, 30, 16, 30);
                    outer.AddBezier(16, 30, 4, 30, 2, 18, 10, 10);
                    outer.AddBezier(10, 10, 11, 15, 13, 16, 14, 16);
                    outer.AddBezier(14, 16, 12, 10, 14, 5, 16, 1);
                    inner.AddBezier(16, 13, 23, 19, 22, 28, 16, 28);
                    inner.AddBezier(16, 28, 10, 28, 9, 21, 16, 13);
                    using (var b1 = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(255, 200, 40), Color.FromArgb(235, 70, 20), 90f))
                        g.FillPath(b1, outer);
                    using (var b2 = new SolidBrush(Color.FromArgb(255, 235, 120))) g.FillPath(b2, inner);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
