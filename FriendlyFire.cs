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
    }

    // One message on the topic. Every message carries the sender's task list, so the other side is always up to date.
    public class Msg
    {
        public string from;    // clientId
        public string name;
        public string type;    // sync | done | react | give | accept | reject
        public string text;
        public long time;      // unix ms, newer lists replace older ones
        public List<TaskItem> tasks;
        public List<TaskItem> gifts;   // tasks given to the receiver (sent again until they show up in the receiver's list)
        public List<string> got;       // ids of gifts the sender has already handled
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
        const int TextLeft = 32, Btn = 26;
        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
        static readonly Color Green = Color.FromArgb(40, 160, 70), Red = Color.FromArgb(215, 60, 50), Orange = Color.FromArgb(255, 140, 40);
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

        public Rectangle OkRect(int i) { var b = GetItemRectangle(i); return new Rectangle(b.Right - 2 * Btn - 12, b.Y + 4, Btn, Btn); }
        public Rectangle NoRect(int i) { var b = GetItemRectangle(i); return new Rectangle(b.Right - Btn - 6, b.Y + 4, Btn, Btn); }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Rows.Count) return;
            var r = Rows[e.Index];
            var b = e.Bounds;
            var g = e.Graphics;
            bool sel = (e.State & DrawItemState.Selected) != 0 && r.task != null;
            Color back = r.grey ? Color.FromArgb(234, 234, 234) : sel ? Color.FromArgb(255, 238, 220) : BackColor;
            using (var br = new SolidBrush(back)) g.FillRectangle(br, b);

            int lineY = b.Y + 6;   // the first text line; marks and buttons line up with it
            if (r.check)
                CheckBoxRenderer.DrawCheckBox(g, new Point(b.X + 9, lineY + 2), r.task.done ? CheckBoxState.CheckedNormal : CheckBoxState.UncheckedNormal);
            else if (r.task != null && !r.buttons)
                TextRenderer.DrawText(g, r.task.done ? "✓" : "○", Font, new Rectangle(b.X + 4, lineY, 26, 22), r.task.done ? Green : Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            else if (r.buttons)
                TextRenderer.DrawText(g, "🎁", Font, new Rectangle(b.X + 4, lineY, 26, 22), Orange, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

            Color fore = r.task == null || r.grey || r.task.done ? Color.Gray : ForeColor;
            TextRenderer.DrawText(g, r.text, Font, new Rectangle(b.X + TextLeft, lineY, TextWidth(r), b.Height - 6), fore, Wrap);

            if (r.tag != null)
                using (var small = Util.UiFont(8.5f))
                    TextRenderer.DrawText(g, r.tag, small, new Rectangle(b.Right - 68, lineY + 2, 62, 20), Color.DimGray, TextFormatFlags.Right | TextFormatFlags.NoPrefix);

            if (r.buttons)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                DrawButton(g, OkRect(e.Index), Green, true);
                DrawButton(g, NoRect(e.Index), Red, false);
                g.SmoothingMode = SmoothingMode.Default;
            }

            using (var p = new Pen(Color.FromArgb(238, 238, 238))) g.DrawLine(p, b.X + 6, b.Bottom - 1, b.Right - 6, b.Bottom - 1);
            if (DropLine == e.Index || (DropLine == Rows.Count && e.Index == Rows.Count - 1))
                using (var br = new SolidBrush(Orange)) g.FillRectangle(br, b.X + 2, DropLine == e.Index ? b.Y : b.Bottom - 3, b.Width - 4, 3);
        }

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

        public void SelectAt(Point p) { int i = IndexFromPoint(p); if (i >= 0 && i < Rows.Count) SelectedIndex = i; }

        // Insert position for a drop at screen point p: before the row under it, or after it when in its lower half
        public int DropIndexAt(Point screen)
        {
            var p = PointToClient(screen);
            int i = IndexFromPoint(p);
            if (i < 0 || i >= Rows.Count || Rows[i].task == null) return Rows.Count(r => r.task != null);
            var b = GetItemRectangle(i);
            return p.Y > b.Y + b.Height / 2 ? i + 1 : i;
        }

        public void ShowDropLine(int i) { if (DropLine != i) { DropLine = i; Invalidate(); } }
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

            // My tasks: add, tick, drag to reorder or over to the friend, delete
            var myBox = new GroupBox { Text = "งานของฉัน  (ลากเพื่อเรียง หรือลากไปให้เพื่อน)", Dock = DockStyle.Fill, Padding = new Padding(8) };
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
            mine.KeyDown += (s, e) =>
            {
                var t = mine.SelectedTask;
                if (t == null) return;
                if (e.KeyCode == Keys.Delete) DeleteTask(t);
                else if (e.KeyCode == Keys.Space && !t.offer) { e.SuppressKeyPress = true; Toggle(t); }
            };
            mine.DragOver += (s, e) => { var t = Dragged(e); e.Effect = t != null ? DragDropEffects.Move : DragDropEffects.None; mine.ShowDropLine(t != null ? mine.DropIndexAt(new Point(e.X, e.Y)) : -1); };
            mine.DragLeave += (s, e) => mine.ShowDropLine(-1);
            mine.DragDrop += (s, e) =>
            {
                var t = Dragged(e);
                int at = mine.DropIndexAt(new Point(e.X, e.Y));
                mine.ShowDropLine(-1);
                if (t != null) app.MoveTask(t, at);
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("ลบงานนี้", null, (s, e) => { var t = mine.SelectedTask; if (t != null) DeleteTask(t); });
            menu.Items.Add("ลบงานที่เสร็จแล้วทั้งหมด", null, (s, e) => { app.Cfg.tasks.RemoveAll(t => t.done); app.Changed("sync", null); });
            menu.Opening += (s, e) => { var t = mine.SelectedTask; e.Cancel = t != null && t.offer; };   // gifts have their own ✓ / ✗
            mine.ContextMenuStrip = menu;
            myBox.Controls.Add(mine); myBox.Controls.Add(addRow);

            // Friend's tasks: read only; finished ones can be removed from this PC; drop a task here to give it
            theirBox.Dock = DockStyle.Fill; theirBox.Padding = new Padding(8);
            theirs.Dock = DockStyle.Fill; theirs.AllowDrop = true;
            var theirMenu = new ContextMenuStrip();
            theirMenu.Items.Add("ลบงานนี้ (เฉพาะเครื่องนี้)", null, (s, e) => { var t = theirs.SelectedTask; if (t != null) app.HideFriendTask(t); });
            theirMenu.Opening += (s, e) => { var t = theirs.SelectedTask; e.Cancel = t == null || !t.done; };   // unfinished tasks belong to the friend
            theirs.ContextMenuStrip = theirMenu;
            theirs.MouseDown += (s, e) => { if (e.Button == MouseButtons.Right) theirs.SelectAt(e.Location); };
            theirs.KeyDown += (s, e) => { var t = theirs.SelectedTask; if (e.KeyCode == Keys.Delete && t != null && t.done) app.HideFriendTask(t); };
            theirs.DragOver += (s, e) =>
            {
                var t = Dragged(e);
                bool ok = t != null && !t.done;
                e.Effect = ok ? DragDropEffects.Move : DragDropEffects.None;
                theirs.BackColor = ok ? Color.FromArgb(255, 244, 232) : SystemColors.Window;
            };
            theirs.DragLeave += (s, e) => theirs.BackColor = SystemColors.Window;
            theirs.DragDrop += (s, e) => { theirs.BackColor = SystemColors.Window; var t = Dragged(e); if (t != null && !t.done) app.GiveTask(t); };
            theirBox.Controls.Add(theirs);
            Activated += (s, e) => app.MarkSeen(true);

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

        static TaskItem Dragged(DragEventArgs e) { return e.Data.GetData(typeof(TaskItem)) as TaskItem; }

        void MineDown(object sender, MouseEventArgs e)
        {
            int i = mine.IndexFromPoint(e.Location);
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
            mine.DoDragDrop(t, DragDropEffects.Move);
            mine.ShowDropLine(-1);
            theirs.BackColor = SystemColors.Window;
        }

        // A click without a drag: tick / untick, or the ✓ / ✗ of a gift
        void MineUp(object sender, MouseEventArgs e)
        {
            int i = pressed;
            pressed = -1;
            if (e.Button != MouseButtons.Left || i < 0 || mine.IndexFromPoint(e.Location) != i) return;
            var t = mine.TaskAt(i);
            if (t == null) return;
            if (!t.offer) Toggle(t);
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
            Notice.Show("ส่งให้ " + app.FriendLabel + " แล้ว 🎉", text, null);
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
        readonly System.Windows.Forms.Timer syncTimer = new System.Windows.Forms.Timer(), updateTimer = new System.Windows.Forms.Timer(),
                                            tidyTimer = new System.Windows.Forms.Timer();
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
            // Every minute: note what's on screen, remove finished tasks seen more than a day ago
            tidyTimer.Interval = 60 * 1000;
            tidyTimer.Tick += (s, e) => { MarkSeen(false); Tidy(); };
            tidyTimer.Start();
            Tidy();
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
            // All my tasks (seenAt stays on this PC: the friend's PC decides when a finished task goes away there)
            var list = Cfg.tasks.Select(t => new TaskItem { id = t.id, text = Short(t.text, 80), done = t.done, doneAt = t.doneAt, offer = t.offer })
                                .ToList();
            var gifts = Cfg.giving.Select(t => new TaskItem { id = t.id, text = Short(t.text, 300) }).ToList();
            var got = Cfg.giftsDone.Skip(Math.Max(0, Cfg.giftsDone.Count - 50)).ToList();
            return new Msg { from = Cfg.clientId, name = Cfg.name, type = type, text = text, time = Util.NowMs(), tasks = list, gifts = gifts, got = got };
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
            Notice.Show("ส่งงานให้ " + FriendLabel + " แล้ว 🎁", t.text, null);
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
            Save();
            main.FillTheirs();
            MarkSeen(false);
            if (newGifts.Count > 0)
            {
                main.FillMine();
                relay.Publish(Make("sync", null));   // the friend sees them as "รอรับ" and stops sending
                foreach (var g in newGifts) Notice.Show("🎁 " + FriendLabel + " ส่งงานให้คุณ", g.text + "\n(เปิดแอปแล้วกด ✓ รับ หรือ ✗ ทิ้ง)", null);
            }
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
            else if (m.type == "accept")
                Notice.Show("👍 " + FriendLabel + " รับงานที่คุณส่งแล้ว" + when, m.text, null);
            else if (m.type == "reject")
                Notice.Show(FriendLabel + " ไม่รับงานที่คุณส่ง" + when, m.text, null);
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

        // Remove finished tasks a day after they were seen on this PC
        void Tidy()
        {
            long cutoff = Util.NowSec() - 86400;
            foreach (var t in Cfg.tasks) if (t.done && t.seenAt == 0) t.seenAt = t.doneAt;   // from v1.0.0
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

        public void HideFriendTask(TaskItem t)
        {
            HideFriend(t);
            Save();
            main.FillTheirs();
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
        public const string Version = "1.0.2";
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
