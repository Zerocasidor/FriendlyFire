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
using Microsoft.Win32;

namespace FriendlyFire
{
    public class TaskItem
    {
        public string id;
        public string text;
        public bool done;
        public long doneAt;   // unix seconds
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
    }

    // One message on the topic. Every message carries the sender's task list, so the other side is always up to date.
    public class Msg
    {
        public string from;    // clientId
        public string name;
        public string type;    // sync | done | react
        public string text;
        public long time;      // unix ms, newer lists replace older ones
        public List<TaskItem> tasks;
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

        // Downloads a newer FriendlyFire.exe next to this one (as .new) and returns its path, or null
        public static string Download()
        {
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

            string url = null;
            foreach (Dictionary<string, object> a in (System.Collections.ArrayList)rel["assets"])
                if ("FriendlyFire.exe".Equals(a["name"])) url = (string)a["browser_download_url"];
            if (url == null) return null;

            string exe = Application.ExecutablePath, tmp = exe + ".new";
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "FriendlyFire/" + Program.Version;
                wc.DownloadFile(url, tmp);
            }
            if (new FileInfo(tmp).Length < 20000) { File.Delete(tmp); return null; }   // not a real exe
            return tmp;
        }

        // A running exe can be renamed but not overwritten: move it aside, put the new one in its place
        public static void Swap(string newExe)
        {
            string exe = Application.ExecutablePath, old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            File.Move(newExe, exe);
        }

        public static void CleanUp()
        {
            try { string old = Application.ExecutablePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
        }
    }

    // ---------- corner pop-ups (one at a time, the rest wait in line) ----------

    class Notice : Form
    {
        static readonly Queue<Action> waiting = new Queue<Action>();
        static Notice current;
        protected override bool ShowWithoutActivation { get { return true; } }

        public static void Show(string title, string text, Action<string> react)
        {
            waiting.Enqueue(() => Open(title, text, react));
            if (current == null || current.IsDisposed) Next();
        }

        static void Next()
        {
            current = null;
            if (waiting.Count > 0) waiting.Dequeue()();
        }

        static void Open(string title, string text, Action<string> react)
        {
            var f = current = new Notice
            {
                Text = "Friendly Fire", TopMost = true, FormBorderStyle = FormBorderStyle.FixedToolWindow, ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Font = Util.UiFont(10), BackColor = Color.FromArgb(255, 243, 230)
            };
            var head = new Label { Text = title, Font = new Font("Segoe UI", 11, FontStyle.Bold), ForeColor = Color.FromArgb(190, 70, 0),
                                   Location = new Point(12, 10), AutoSize = true, MaximumSize = new Size(320, 0) };
            f.Controls.Add(head);
            int bottom = head.Bottom;
            if (!string.IsNullOrEmpty(text))
            {
                var body = new Label { Text = text, Location = new Point(12, bottom + 4), AutoSize = true, MaximumSize = new Size(320, 0) };
                f.Controls.Add(body);
                bottom = body.Bottom;
            }
            int right = 0;
            foreach (Control c in f.Controls) right = Math.Max(right, c.Right);
            if (react != null)
            {
                int x = 12;
                foreach (string e in new[] { "🔥", "👏", "💪", "😎" })
                {
                    string emoji = e;
                    var b = new Button { Text = emoji, Font = Util.EmojiFont(13), Location = new Point(x, bottom + 8), Size = new Size(48, 38), FlatStyle = FlatStyle.Flat };
                    b.Click += (s, ev) => { react(emoji); f.Close(); };
                    f.Controls.Add(b);
                    x += 54;
                }
                bottom += 8 + 38;
                right = Math.Max(right, x - 6);
            }
            f.ClientSize = new Size(Math.Max(right + 12, 220), bottom + 12);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            f.Location = new Point(wa.Right - f.Width - 12, wa.Bottom - f.Height - 12);
            f.FormClosed += (s, e) => Next();

            // Stay 12 s (longer with reaction buttons), then fade out over the last second
            DateTime fadeAt = DateTime.Now.AddSeconds(react != null ? 20 : 8);
            var t = new System.Windows.Forms.Timer { Interval = 50 };
            t.Tick += (s, e) =>
            {
                if (f.IsDisposed) { t.Dispose(); return; }
                if (f.Bounds.Contains(Cursor.Position)) { fadeAt = DateTime.Now.AddSeconds(3); f.Opacity = 1; return; }   // hovering keeps it
                double into = (DateTime.Now - fadeAt).TotalSeconds;
                if (into < 0) return;
                if (into >= 1) { t.Dispose(); f.Close(); return; }
                f.Opacity = 1 - into;
            };
            t.Start();
            f.Show();
            PlaySound();
        }

        static void PlaySound()
        {
            try
            {
                string wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Media\Windows Notify Messaging.wav");
                if (File.Exists(wav)) new SoundPlayer(wav).Play(); else SystemSounds.Asterisk.Play();
            }
            catch { }
        }
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

    class MainForm : Form
    {
        readonly App app;
        readonly Label status = new Label();
        readonly CheckedListBox mine = new CheckedListBox();
        readonly ListBox theirs = new ListBox();
        readonly GroupBox theirBox = new GroupBox();
        readonly TextBox newTask = new TextBox(), brag = new TextBox();
        bool filling;
        public bool Exiting;

        public MainForm(App app)
        {
            this.app = app;
            Text = "Friendly Fire"; Icon = Program.Flame; Font = Util.UiFont(10.5f); ClientSize = new Size(720, 500);
            StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(560, 380);

            status.Dock = DockStyle.Top; status.Height = 30; status.Padding = new Padding(10, 6, 0, 0); status.ForeColor = Color.DimGray;

            // Bottom: tell the friend about anything you finished
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(10, 8, 10, 10) };
            var bragBtn = new Button { Text = "เสร็จแล้ว! 🎉", Dock = DockStyle.Right, Width = 150, Font = new Font("Segoe UI", 11, FontStyle.Bold),
                                       BackColor = Color.FromArgb(255, 140, 40), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            brag.Dock = DockStyle.Fill; brag.Font = Util.UiFont(12);
            SetCue(brag, "ทำอะไรเสร็จ? เช่น อ่านฟิสิกส์จบบท 3");
            bragBtn.Click += (s, e) => Brag();
            brag.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Brag(); } };
            bottom.Controls.Add(brag); bottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); bottom.Controls.Add(bragBtn);

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(6, 0, 6, 0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            // My tasks: add, tick, delete
            var myBox = new GroupBox { Text = "งานของฉัน", Dock = DockStyle.Fill, Padding = new Padding(8) };
            var addRow = new Panel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(0, 6, 0, 0) };
            var addBtn = new Button { Text = "เพิ่ม", Dock = DockStyle.Right, Width = 70 };
            newTask.Dock = DockStyle.Fill; SetCue(newTask, "งานใหม่...");
            addBtn.Click += (s, e) => AddTask();
            newTask.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddTask(); } };
            addRow.Controls.Add(newTask); addRow.Controls.Add(addBtn);
            mine.Dock = DockStyle.Fill; mine.CheckOnClick = true; mine.IntegralHeight = false; mine.BorderStyle = BorderStyle.None;
            mine.ItemCheck += OnTick;
            mine.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) DeleteSelected(); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("ลบงานนี้", null, (s, e) => DeleteSelected());
            menu.Items.Add("ลบงานที่เสร็จแล้วทั้งหมด", null, (s, e) => { app.Cfg.tasks.RemoveAll(t => t.done); app.Changed("sync", null); });
            mine.ContextMenuStrip = menu;
            mine.MouseDown += (s, e) => { if (e.Button == MouseButtons.Right) { int i = mine.IndexFromPoint(e.Location); if (i >= 0) mine.SelectedIndex = i; } };
            myBox.Controls.Add(mine); myBox.Controls.Add(addRow);

            // Friend's tasks: read only
            theirBox.Dock = DockStyle.Fill; theirBox.Padding = new Padding(8);
            theirs.Dock = DockStyle.Fill; theirs.IntegralHeight = false; theirs.BorderStyle = BorderStyle.None; theirs.SelectionMode = SelectionMode.None;
            theirBox.Controls.Add(theirs);

            grid.Controls.Add(myBox, 0, 0); grid.Controls.Add(theirBox, 1, 0);
            Controls.Add(grid); Controls.Add(bottom); Controls.Add(status);

            FormClosing += (s, e) => { if (!Exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
            FillMine(); FillTheirs(); SetStatus(false);
        }

        // Grey hint text inside an empty text box
        static void SetCue(TextBox box, string cue)
        {
            box.HandleCreated += (s, e) => Native.SendMessage(box.Handle, 0x1501, (IntPtr)1, cue);
        }

        void AddTask()
        {
            string text = newTask.Text.Trim();
            if (text == "") return;
            app.Cfg.tasks.Add(new TaskItem { id = Util.RandomCode(8), text = text });
            newTask.Text = "";
            app.Changed("sync", null);
        }

        void DeleteSelected()
        {
            var t = mine.SelectedItem as TaskItem;
            if (t == null) return;
            app.Cfg.tasks.Remove(t);
            app.Changed("sync", null);
        }

        void OnTick(object sender, ItemCheckEventArgs e)
        {
            if (filling) return;
            var t = (TaskItem)mine.Items[e.Index];
            t.done = e.NewValue == CheckState.Checked;
            t.doneAt = t.done ? Util.NowSec() : 0;
            // After this event the box has its new state; send once it's applied
            BeginInvoke((Action)(() => app.Changed(t.done ? "done" : "sync", t.done ? t.text : null)));
        }

        void Brag()
        {
            string text = brag.Text.Trim();
            if (text == "") { brag.Focus(); return; }
            brag.Text = "";
            app.Changed("done", text);
            Notice.Show("ส่งให้ " + app.FriendLabel + " แล้ว 🎉", text, null);
        }

        public void FillMine()
        {
            filling = true;
            mine.BeginUpdate();
            mine.Items.Clear();
            foreach (var t in app.Cfg.tasks) mine.Items.Add(t, t.done);
            mine.EndUpdate();
            filling = false;
        }

        public void FillTheirs()
        {
            theirBox.Text = "งานของ " + app.FriendLabel;
            theirs.BeginUpdate();
            theirs.Items.Clear();
            // Finished tasks stay for 1 day (also when the friend's PC has been off since)
            var shown = app.Cfg.friendTasks.Where(t => !t.done || t.doneAt > Util.NowSec() - 86400).ToList();
            foreach (var t in shown) theirs.Items.Add((t.done ? "✓  " : "○  ") + t.text);
            if (shown.Count == 0) theirs.Items.Add(app.Cfg.friendName == "" ? "(รอเพื่อนเข้าร่วม...)" : "(ยังไม่มีงาน)");
            theirs.EndUpdate();
        }

        public void SetStatus(bool online)
        {
            status.Text = (online ? "● เชื่อมต่อแล้ว" : "○ กำลังเชื่อมต่อ...") + "   ·   คุณ: " + app.Cfg.name + "   ·   เพื่อน: " + app.FriendLabel + "   ·   v" + Program.Version;
            status.ForeColor = online ? Color.FromArgb(30, 130, 60) : Color.DimGray;
        }
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
        readonly System.Windows.Forms.Timer syncTimer = new System.Windows.Forms.Timer(), updateTimer = new System.Windows.Forms.Timer();
        bool updating;

        public string FriendLabel { get { return Cfg.friendName == "" ? "เพื่อน" : Cfg.friendName; } }

        public App(string dataDir, bool testCopy, string name, string code, bool justUpdated)
        {
            this.testCopy = testCopy;
            Directory.CreateDirectory(dataDir);
            cfgPath = Path.Combine(dataDir, "friendlyfire.json");
            Cfg = File.Exists(cfgPath) ? Util.Json.Deserialize<Config>(File.ReadAllText(cfgPath, Encoding.UTF8)) : new Config();
            if (Cfg.clientId == "") Cfg.clientId = Util.RandomCode(12);
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

            main = new MainForm(this);
            relay = new Relay("friendlyfire-" + Cfg.code.ToLowerInvariant(), Cfg.clientId);
            relay.Received += (m, id, time) => ui.BeginInvoke((Action)(() => OnMessage(m, time)));
            relay.Connected += on => ui.BeginInvoke((Action)(() => main.SetStatus(on)));
            relay.Start();
            relay.Publish(Make("sync", null));   // let the friend see my name and list

            var menu = new ContextMenuStrip();
            menu.Items.Add("เปิด Friendly Fire", null, (s, e) => ShowMain());
            menu.Items.Add("รหัสเชิญ...", null, (s, e) => ShowCode());
            var startItem = new ToolStripMenuItem("เปิดพร้อม Windows") { Checked = Cfg.startWithWindows, Enabled = !testCopy };
            startItem.Click += (s, e) => { Cfg.startWithWindows = !Cfg.startWithWindows; startItem.Checked = Cfg.startWithWindows; Save(); ApplyStartup(); };
            menu.Items.Add(startItem);
            menu.Items.Add("ตรวจหาอัปเดต", null, (s, e) => CheckUpdate(true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("ออก", null, (s, e) => Quit());
            tray.Icon = Program.Flame;
            tray.Text = "Friendly Fire" + (testCopy ? " (" + Cfg.name + ")" : "");
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ShowMain();
            tray.Visible = true;

            ApplyStartup();
            // Re-send my list every 6 hours: ntfy only keeps messages ~12 hours
            syncTimer.Interval = 6 * 3600 * 1000;
            syncTimer.Tick += (s, e) => relay.Publish(Make("sync", null));
            syncTimer.Start();
            if (!testCopy)
            {
                Updater.CleanUp();
                updateTimer.Interval = 60 * 1000;   // first check a minute after start, then every 3 hours
                updateTimer.Tick += (s, e) => { updateTimer.Interval = 3 * 3600 * 1000; Updater.CleanUp(); CheckUpdate(false); };
                updateTimer.Start();
            }

            if (Program.CheckNow) CheckUpdate(true);
            if (firstRun || testCopy) ShowMain();
            if (created) ShowCode();
            if (justUpdated) Notice.Show("Friendly Fire อัปเดตแล้ว", "ตอนนี้เป็นเวอร์ชัน " + Program.Version, null);
        }

        public Msg Make(string type, string text)
        {
            // Unfinished tasks + those finished in the last day (the friend sees a finished task for 1 day)
            long cutoff = Util.NowSec() - 86400;
            var list = Cfg.tasks.Where(t => !t.done || t.doneAt > cutoff)
                                .Select(t => new TaskItem { id = t.id, text = t.text.Length > 80 ? t.text.Substring(0, 80) + "…" : t.text, done = t.done, doneAt = t.doneAt })
                                .ToList();
            return new Msg { from = Cfg.clientId, name = Cfg.name, type = type, text = text, time = Util.NowMs(), tasks = list };
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
            if (m.tasks != null && m.time >= Cfg.friendTasksTime) { Cfg.friendTasks = m.tasks; Cfg.friendTasksTime = m.time; }
            bool fresh = time > Cfg.lastSeen;   // not shown before (messages are replayed after a restart)
            if (fresh) Cfg.lastSeen = time;
            Save();
            main.FillTheirs();
            main.SetStatus(true);
            if (!fresh) return;

            string when = Util.NowSec() - time > 300 ? "  (" + Util.Local(time).ToString("HH:mm", CultureInfo.InvariantCulture) + ")" : "";
            if (m.type == "done")
                Notice.Show("🔥 " + FriendLabel + " ทำสำเร็จ!", m.text + when, emoji =>
                {
                    relay.Publish(Make("react", emoji));
                    Notice.Show("ส่ง " + emoji + " ให้ " + FriendLabel + " แล้ว", null, null);
                });
            else if (m.type == "react")
                Notice.Show(FriendLabel + " ส่ง " + m.text + " ให้คุณ" + when, null, null);
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
                               StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(400, 170), Font = Util.UiFont(10.5f), TopMost = true };
            f.Controls.Add(new Label { Text = "ส่งรหัสนี้ให้เพื่อน แล้วให้เพื่อนเลือก \"ใส่รหัสเชิญจากเพื่อน\"\r\n(ใครมีรหัสนี้จะเห็นงานของคุณ ส่งให้เพื่อนคนเดียวพอ)",
                                       Location = new Point(16, 14), Size = new Size(370, 50) });
            var box = new TextBox { Text = Cfg.code, ReadOnly = true, Font = new Font("Consolas", 14), Location = new Point(16, 70), Width = 260 };
            var copy = new Button { Text = "คัดลอก", Location = new Point(284, 68), Size = new Size(100, 34) };
            copy.Click += (s, e) => { Clipboard.SetText(Cfg.code); copy.Text = "คัดลอกแล้ว ✓"; };
            var ok = new Button { Text = "ปิด", Location = new Point(284, 120), Size = new Size(100, 34), DialogResult = DialogResult.OK };
            f.Controls.AddRange(new Control[] { box, copy, ok });
            f.AcceptButton = ok;
            f.Show();
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

        void CheckUpdate(bool byHand)
        {
            if (updating) return;
            updating = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string newExe = null, error = null;
                try { newExe = Updater.Download(); }
                catch (Exception ex) { error = ex.Message; }
                ui.BeginInvoke((Action)(() =>
                {
                    updating = false;
                    if (newExe != null) Restart(newExe);
                    else if (byHand) Notice.Show(error == null ? "เป็นเวอร์ชันล่าสุดแล้ว" : "ตรวจอัปเดตไม่ได้", error == null ? "v" + Program.Version : error, null);
                }));
            });
        }

        void Restart(string newExe)
        {
            try { Updater.Swap(newExe); }
            catch (Exception ex) { Notice.Show("อัปเดตไม่สำเร็จ", ex.Message, null); return; }
            relay.Stop();
            tray.Visible = false;
            Program.ReleaseLock();
            Process.Start(Application.ExecutablePath, Program.RestartArgs + " --updated");
            ExitThread();
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
        public const string Version = "1.0.0";
        public static Icon Flame;
        static Mutex mutex;
        public static string RestartArgs = "";   // the same -Data/-Name/-Code after an update
        public static bool CheckNow;              // -CheckUpdate: look for an update right away (also in test copies)

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
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;   // ntfy.sh and GitHub need TLS 1.2

            string dataDir = null, name = null, code = null;
            bool updated = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "-data" && i + 1 < args.Length) dataDir = args[++i];
                else if (a == "-name" && i + 1 < args.Length) name = args[++i];
                else if (a == "-code" && i + 1 < args.Length) code = args[++i];
                else if (a == "--updated") updated = true;
                else if (a == "-checkupdate") CheckNow = true;
            }
            foreach (string x in args) if (x != "--updated") RestartArgs += "\"" + x + "\" ";
            bool testCopy = dataDir != null;
            if (dataDir == null) dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FriendlyFire");
            dataDir = Path.GetFullPath(dataDir);

            // One copy per data folder; after an update the new copy waits for the old one to exit
            bool owned;
            mutex = new Mutex(false, "FriendlyFire-" + dataDir.ToLowerInvariant().GetHashCode().ToString("x"), out owned);
            try { if (!mutex.WaitOne(updated ? 20000 : 0)) return; }
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
