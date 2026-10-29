// 부스 호출 버튼 (윈도우 프로그램)
// 주소 줄 없이 화면 맨 위에 항상 떠 있는 호출 버튼. 웹 버전(index.html)과 같은 ntfy 채널·메시지 형식을 쓰므로
// 현황판·휴대폰 알림·웹 호출 버튼과 그대로 섞어 쓸 수 있다.
//
//   왼쪽 클릭: 호출 / 끌기: 옮기기 / 오른쪽 아래 모서리 끌기: 크기 / 오른쪽 클릭: 메뉴(취소·설정·크기·종료)
//   설정은 %APPDATA%\BoothCall\config.txt. 실행 인자로 참가 링크를 주면 그 채널로 설정된다.
//   --selftest <폴더>: 실제 ntfy로 호출·응답·취소를 주고받아 보고 그림을 PNG로 남긴다(CI용).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("부스 호출 버튼")]
[assembly: System.Reflection.AssemblyProduct("부스 호출 버튼")]

namespace BoothCall
{
    // ---------- ntfy 메시지 ----------
    [DataContract]
    class Msg
    {
        [DataMember(Name = "id")] public string Id;
        [DataMember(Name = "time")] public long Time;
        [DataMember(Name = "event")] public string Event;
        [DataMember(Name = "title")] public string Title;
        [DataMember(Name = "message")] public string Message;
        [DataMember(Name = "tags")] public string[] Tags;
    }

    [DataContract]
    class NtfyAction
    {
        [DataMember(Name = "action")] public string Kind = "http";
        [DataMember(Name = "label")] public string Label;
        [DataMember(Name = "url")] public string Url;
        [DataMember(Name = "method")] public string Method = "POST";
        [DataMember(Name = "body")] public string Body;
        [DataMember(Name = "clear")] public bool Clear = true;
    }

    [DataContract]
    class Pub
    {
        [DataMember(Name = "topic")] public string Topic;
        [DataMember(Name = "title")] public string Title;
        [DataMember(Name = "message")] public string Message;
        [DataMember(Name = "tags")] public string[] Tags;
        [DataMember(Name = "priority")] public int Priority;
        [DataMember(Name = "actions", EmitDefaultValue = false)] public NtfyAction[] Actions;
    }

    static class Ntfy
    {
        public const string Base = "https://ntfy.sh/";

        public static string ToJson<T>(T obj)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                ser.WriteObject(ms, obj);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static void Post(string url, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.Timeout = 10000;
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            using (req.GetResponse()) { }
        }

        public static void Publish(Pub p) { Post(Base, ToJson(p)); }

        static Msg Parse(string line)
        {
            try
            {
                var ser = new DataContractJsonSerializer(typeof(Msg));
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(line))) return ser.ReadObject(ms) as Msg;
            }
            catch { return null; }
        }

        // since부터 받아서 계속 이어 받는다. 끊기면 3초 뒤 마지막 메시지 다음부터 다시 붙는다.
        public static void Listen(string topic, string since, Action<Msg> onMsg, Action<bool> onConn, CancellationToken ct)
        {
            var seen = new HashSet<string>();
            string last = since;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(Base + Uri.EscapeDataString(topic) + "/json?since=" + last);
                    req.Timeout = 15000;
                    req.ReadWriteTimeout = 100000; // ntfy가 45초마다 keepalive를 보낸다
                    using (ct.Register(() => { try { req.Abort(); } catch { } }))
                    using (var res = req.GetResponse())
                    using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                    {
                        onConn(true);
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            var m = Parse(line);
                            if (m == null || m.Event != "message" || m.Id == null || !seen.Add(m.Id)) continue;
                            last = m.Id;
                            onMsg(m);
                        }
                    }
                }
                catch { }
                if (ct.IsCancellationRequested) return;
                onConn(false);
                if (ct.WaitHandle.WaitOne(3000)) return;
            }
        }
    }

    // ---------- 호출 상태 (웹 버전과 같은 규칙) ----------
    enum State { Idle, Waiting, Coming }

    class Core
    {
        public const int ComingSeconds = 90; // "오는 중" 표시 유지 시간
        public readonly string Topic, Booth, Name;
        public State State = State.Idle;
        public string Who = "";
        public DateTime ComingUntil;
        public bool Failed;
        public event Action Changed;

        public Core(string topic, string booth, string name)
        {
            Topic = topic; Booth = booth; Name = name;
        }

        static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }

        public static string KindOf(Msg m)
        {
            if (m.Tags == null) return null;
            foreach (var k in new[] { "call", "ack", "cancel" })
                if (Array.IndexOf(m.Tags, k) >= 0) return k;
            return null;
        }

        public static string BoothOf(Msg m)
        {
            if (m.Tags == null) return null;
            foreach (var t in m.Tags) if (t.StartsWith("b-")) return t.Substring(2);
            return null;
        }

        public void Set(State s, string who = "")
        {
            State = s; Who = who ?? ""; Failed = false;
            if (s == State.Coming) ComingUntil = DateTime.UtcNow.AddSeconds(ComingSeconds);
            var h = Changed; if (h != null) h();
        }

        public void OnMessage(Msg m)
        {
            if (BoothOf(m) != Booth) return;
            string kind = KindOf(m);
            long age = Now() - m.Time;
            if (kind == "call")
            {
                if (age < 600 && State != State.Waiting) Set(State.Waiting);
            }
            else if (kind == "ack")
            {
                if (age > ComingSeconds) { if (State != State.Idle) Set(State.Idle); return; }
                string msg = m.Message ?? "";
                string who = msg.StartsWith("휴대폰") ? "" : Regex.Replace(msg, " 가는 중$", "");
                Set(State.Coming, who);
                ComingUntil = DateTime.UtcNow.AddSeconds(ComingSeconds - Math.Max(0, age));
            }
            else if (kind == "cancel")
            {
                if (State != State.Idle) Set(State.Idle);
            }
        }

        public void Tick()
        {
            if (State == State.Coming && DateTime.UtcNow > ComingUntil) Set(State.Idle);
        }

        public Pub CallPub()
        {
            // 휴대폰 알림의 [내가 갈게] 버튼이 그대로 응답 메시지를 보낸다.
            var ack = new Pub { Topic = Topic, Title = "✅ " + Name + " 응답", Message = "휴대폰에서 응답했어요", Tags = new[] { "ack", "b-" + Booth }, Priority = 1 };
            return new Pub
            {
                Topic = Topic,
                Title = "🔔 " + Name + " 호출",
                Message = Name + "에서 부원을 부르고 있어요!",
                Tags = new[] { "call", "b-" + Booth },
                Priority = 5,
                Actions = new[] { new NtfyAction { Label = "내가 갈게", Url = Ntfy.Base, Body = Ntfy.ToJson(ack) } },
            };
        }

        public Pub CancelPub()
        {
            return new Pub { Topic = Topic, Title = Name + " 호출 취소", Message = "노트북에서 호출을 취소했어요", Tags = new[] { "cancel", "b-" + Booth }, Priority = 1 };
        }
    }

    // ---------- 설정 파일 ----------
    class Config
    {
        public string Topic = "", Booth = "1", Name = "";
        public int X = int.MinValue, Y = int.MinValue, W = 0, H = 0;

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BoothCall"); } }
        static string FilePath { get { return Path.Combine(Dir, "config.txt"); } }

        public string DisplayName { get { return string.IsNullOrEmpty(Name) ? Booth + "번 노트북" : Name; } }
        public bool Valid { get { return Regex.IsMatch(Topic ?? "", "^[A-Za-z0-9_-]+$"); } }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i < 0) continue;
                    string k = line.Substring(0, i), v = line.Substring(i + 1);
                    int n;
                    switch (k)
                    {
                        case "topic": c.Topic = v; break;
                        case "booth": c.Booth = v; break;
                        case "name": c.Name = v; break;
                        case "x": if (int.TryParse(v, out n)) c.X = n; break;
                        case "y": if (int.TryParse(v, out n)) c.Y = n; break;
                        case "w": if (int.TryParse(v, out n)) c.W = n; break;
                        case "h": if (int.TryParse(v, out n)) c.H = n; break;
                    }
                }
            }
            catch { }
            return c;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new[] { "topic=" + Topic, "booth=" + Booth, "name=" + Name, "x=" + X, "y=" + Y, "w=" + W, "h=" + H }, Encoding.UTF8);
            }
            catch { }
        }

        // 참가 링크(…/terabyte/?t=채널&booth=3&name=…)나 채널 이름만 넣어도 된다.
        public bool ApplyLink(string text)
        {
            text = (text ?? "").Trim();
            int q = text.IndexOf('?');
            if (q < 0) { if (text.Length > 0 && !text.Contains("/")) { Topic = text; return true; } return false; }
            bool any = false;
            foreach (var part in text.Substring(q + 1).Split('&', '#'))
            {
                int i = part.IndexOf('=');
                if (i < 0) continue;
                string k = part.Substring(0, i), v = Uri.UnescapeDataString(part.Substring(i + 1).Replace('+', ' '));
                if (k == "t") { Topic = v; any = true; }
                else if (k == "booth") Booth = v;
                else if (k == "name") Name = v;
            }
            return any;
        }
    }

    // ---------- 그리기 (화면·selftest 공용) ----------
    static class Painter
    {
        static readonly Color Red = Color.FromArgb(229, 72, 77), RedDark = Color.FromArgb(184, 40, 45), RedLight = Color.FromArgb(255, 112, 117);
        static readonly Color Green = Color.FromArgb(31, 157, 85), GreenDark = Color.FromArgb(19, 122, 64);

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Font FitFont(Graphics g, string text, float size, float maxW, FontStyle style)
        {
            while (true)
            {
                var f = new Font("Malgun Gothic", size, style, GraphicsUnit.Pixel);
                if (size <= 9 || g.MeasureString(text, f).Width <= maxW) return f;
                f.Dispose();
                size *= 0.9f;
            }
        }

        public static void Paint(Graphics g, Size sz, Core core, bool blinkOn, bool connected, float scale)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Black);

            State s = core.State;
            Color fill = s == State.Coming ? Green : (s == State.Waiting && blinkOn ? RedLight : Red);
            Color dark = s == State.Coming ? GreenDark : RedDark;
            var rect = new Rectangle(0, 0, sz.Width, sz.Height);
            int radius = (int)(14 * scale);
            using (var p = Rounded(rect, radius)) using (var b = new SolidBrush(dark)) g.FillPath(b, p);
            var top = new Rectangle(0, 0, sz.Width, sz.Height - Math.Max(3, (int)(5 * scale)));
            using (var p = Rounded(top, radius)) using (var b = new SolidBrush(fill)) g.FillPath(b, p);

            string main = s == State.Coming ? "오는 중!" : s == State.Waiting ? "호출함" : "부원 호출";
            string sub = core.Failed ? "전송 실패 · 인터넷 확인 후 다시 눌러 주세요"
                : s == State.Coming ? (core.Who.Length > 0 ? core.Who + " 오고 있어요" : "부원이 오고 있어요")
                : s == State.Waiting ? "부원이 곧 와요"
                : core.Name;

            float h = top.Height, w = sz.Width;
            bool showSub = h >= 64 * scale;
            float mainSize = Math.Min(h * (showSub ? 0.36f : 0.5f), w * 0.2f);
            using (var mf = FitFont(g, main, mainSize, w * 0.88f, FontStyle.Bold))
            using (var sf = FitFont(g, sub, Math.Max(10 * scale, mainSize * 0.38f), w * 0.9f, FontStyle.Regular))
            using (var white = new SolidBrush(Color.White))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                float mh = g.MeasureString(main, mf).Height, sh = showSub ? g.MeasureString(sub, sf).Height : 0;
                float y = (h - mh - sh) / 2;
                g.DrawString(main, mf, white, new RectangleF(0, y, w, mh), fmt);
                if (showSub)
                    using (var soft = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                        g.DrawString(sub, sf, soft, new RectangleF(0, y + mh, w, sh), fmt);
            }

            // 연결 상태 점 (초록 = 연결됨, 회색 = 끊김)
            float dot = 7 * scale;
            using (var b = new SolidBrush(connected ? Color.FromArgb(120, 255, 160) : Color.FromArgb(200, 200, 200)))
                g.FillEllipse(b, 7 * scale, 7 * scale, dot, dot);

            // 크기 조절 손잡이 (오른쪽 아래)
            using (var pen = new Pen(Color.FromArgb(150, 255, 255, 255), Math.Max(1, scale)))
                for (int i = 1; i <= 3; i++)
                {
                    float o = i * 4 * scale;
                    g.DrawLine(pen, sz.Width - o - 3 * scale, sz.Height - 4 * scale, sz.Width - 4 * scale, sz.Height - o - 3 * scale);
                }
        }
    }

    // ---------- 호출 버튼 창 ----------
    class CallForm : Form
    {
        const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        Config cfg;
        Core core;
        CancellationTokenSource listenCts;
        volatile bool connected;
        bool blinkOn;
        DateTime lastPress = DateTime.MinValue;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 600 };
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripMenuItem cancelItem;
        readonly float scale;

        // 마우스: 누르고 4px 넘게 움직이면 옮기기, 모서리에서 누르면 크기 조절, 그냥 떼면 호출
        Point downScreen, startLoc;
        Size startSize;
        bool down, moved, resizing;

        public CallForm(Config c)
        {
            cfg = c;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = Color.Black;
            Text = "부스 호출";
            MinimumSize = new Size((int)(120 * scale), (int)(48 * scale));

            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(cfg.W > 0 ? cfg.W : (int)(240 * scale), cfg.H > 0 ? cfg.H : (int)(100 * scale));
            var loc = new Point(cfg.X, cfg.Y);
            bool onScreen = false;
            foreach (var sc in Screen.AllScreens) if (sc.WorkingArea.IntersectsWith(new Rectangle(loc, new Size(40, 40)))) onScreen = true;
            Location = (cfg.X != int.MinValue && onScreen) ? loc : new Point(wa.Right - Width - (int)(16 * scale), wa.Top + (int)(16 * scale));

            BuildMenu();
            timer.Tick += (s, e) =>
            {
                core.Tick();
                if (core.State == State.Waiting) { blinkOn = !blinkOn; Invalidate(); }
                // 게임이 앞으로 나와도 다시 맨 위로 (포커스는 뺏지 않는다)
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            };
            timer.Start();
            StartCore();
        }

        // 눌러도 게임 창의 포커스를 뺏지 않고, 작업 표시줄·Alt+Tab에도 안 나온다.
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        void StartCore()
        {
            if (listenCts != null) listenCts.Cancel();
            core = new Core(cfg.Topic, cfg.Booth, cfg.DisplayName);
            core.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)UpdateView); };
            listenCts = new CancellationTokenSource();
            var ct = listenCts.Token;
            var c = core;
            // 다시 켜도 진행 중인 호출 상태가 남도록 최근 10분 기록부터 받는다.
            var th = new Thread(() => Ntfy.Listen(cfg.Topic, "10m",
                m => c.OnMessage(m),
                on => { connected = on; if (IsHandleCreated) BeginInvoke((Action)Invalidate); }, ct)) { IsBackground = true };
            th.Start();
            UpdateView();
        }

        void UpdateView()
        {
            if (cancelItem != null) cancelItem.Enabled = core.State == State.Waiting;
            Invalidate();
        }

        void BuildMenu()
        {
            cancelItem = new ToolStripMenuItem("호출 취소", null, (s, e) =>
            {
                core.Set(State.Idle);
                var p = core.CancelPub();
                ThreadPool.QueueUserWorkItem(_ => { try { Ntfy.Publish(p); } catch { } });
            });
            var sizes = new ToolStripMenuItem("크기");
            foreach (var t in new[] { Tuple.Create("작게", 170, 64), Tuple.Create("보통", 240, 100), Tuple.Create("크게", 340, 150) })
            {
                var tt = t;
                sizes.DropDownItems.Add(tt.Item1, null, (s, e) => { Size = new Size((int)(tt.Item2 * scale), (int)(tt.Item3 * scale)); SavePos(); });
            }
            menu.Items.Add(cancelItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("설정 (채널·번호)…", null, (s, e) => OpenSettings());
            menu.Items.Add(sizes);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, (s, e) => Close());
        }

        void OpenSettings()
        {
            var copy = new Config { Topic = cfg.Topic, Booth = cfg.Booth, Name = cfg.Name };
            using (var dlg = new SettingsForm(copy))
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                cfg.Topic = copy.Topic; cfg.Booth = copy.Booth; cfg.Name = copy.Name;
                cfg.Save();
                StartCore();
            }
        }

        void Press()
        {
            if ((DateTime.UtcNow - lastPress).TotalSeconds < 5) return; // 연타 방지
            lastPress = DateTime.UtcNow;
            core.Set(State.Waiting);
            var p = core.CallPub();
            var c = core;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Ntfy.Publish(p); }
                catch
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke((Action)(() => { lastPress = DateTime.MinValue; c.Set(State.Idle); c.Failed = true; Invalidate(); }));
                }
            });
        }

        bool InGrip(Point p) { int g = (int)(18 * scale); return p.X >= Width - g && p.Y >= Height - g; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            down = true; moved = false; resizing = InGrip(e.Location);
            downScreen = Cursor.Position; startLoc = Location; startSize = Size;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = (resizing || InGrip(e.Location)) ? Cursors.SizeNWSE : Cursors.Hand;
            if (!down) return;
            int dx = Cursor.Position.X - downScreen.X, dy = Cursor.Position.Y - downScreen.Y;
            if (!moved && Math.Abs(dx) + Math.Abs(dy) < 5) return;
            moved = true;
            if (resizing) Size = new Size(startSize.Width + dx, startSize.Height + dy);
            else Location = new Point(startLoc.X + dx, startLoc.Y + dy);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { menu.Show(this, e.Location); return; }
            if (e.Button != MouseButtons.Left || !down) return;
            down = false; Capture = false;
            if (moved) SavePos();
            else if (!resizing) Press();
            resizing = false;
        }

        void SavePos()
        {
            cfg.X = Left; cfg.Y = Top; cfg.W = Width; cfg.H = Height;
            cfg.Save();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (var p = Painter.Rounded(new Rectangle(0, 0, Width, Height), (int)(14 * scale))) Region = new Region(p);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Painter.Paint(e.Graphics, ClientSize, core, blinkOn, connected, scale);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (listenCts != null) listenCts.Cancel();
            SavePos();
            base.OnFormClosed(e);
        }
    }

    // ---------- 설정 창 ----------
    class SettingsForm : Form
    {
        public SettingsForm(Config cfg)
        {
            Text = "부스 호출 버튼 설정";
            Font = new Font("Malgun Gothic", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);

            var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            Func<string, Label> label = s => new Label { Text = s, AutoSize = true, Margin = new Padding(0, 10, 0, 3) };
            var topic = new TextBox { Text = cfg.Topic, Width = 380 };
            var booth = new NumericUpDown { Minimum = 1, Maximum = 99, Width = 80 };
            int bn; booth.Value = int.TryParse(cfg.Booth, out bn) && bn >= 1 && bn <= 99 ? bn : 1;
            var name = new TextBox { Text = cfg.Name, Width = 380 };
            var err = new Label { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(0, 8, 0, 0) };

            // 링크 텍스트 파일의 주소를 통째로 붙여넣으면 채널·번호·이름을 채운다.
            topic.TextChanged += (s, e) =>
            {
                if (!topic.Text.Contains("?")) return;
                var tmp = new Config { Booth = booth.Value.ToString(), Name = name.Text };
                if (!tmp.ApplyLink(topic.Text)) return;
                topic.Text = tmp.Topic;
                int n; if (int.TryParse(tmp.Booth, out n) && n >= 1 && n <= 99) booth.Value = n;
                name.Text = tmp.Name;
            };

            var ok = new Button { Text = "확인", AutoSize = true, DialogResult = DialogResult.None };
            var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                var tmp = new Config { Topic = topic.Text.Trim() };
                if (!tmp.Valid) { err.Text = "채널 이름을 확인해 주세요 (예: booth-x7k2q9)"; topic.Focus(); return; }
                cfg.Topic = tmp.Topic; cfg.Booth = booth.Value.ToString(); cfg.Name = name.Text.Trim();
                DialogResult = DialogResult.OK;
            };
            AcceptButton = ok; CancelButton = cancel;

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);

            t.Controls.Add(label("채널 이름 (참가 링크를 통째로 붙여넣어도 돼요)"));
            t.Controls.Add(topic);
            t.Controls.Add(label("노트북 번호"));
            t.Controls.Add(booth);
            t.Controls.Add(label("이름 (선택, 비우면 \"N번 노트북\")"));
            t.Controls.Add(name);
            t.Controls.Add(err);
            t.Controls.Add(buttons);
            Controls.Add(t);
        }
    }

    // ---------- 시작 ----------
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            if (args.Length >= 1 && args[0] == "--selftest") return SelfTest.Run(args.Length > 1 ? args[1] : ".");

            bool fresh;
            using (var mutex = new Mutex(true, "BoothCall.SingleInstance", out fresh))
            {
                if (!fresh) return 0; // 이미 떠 있다
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var cfg = Config.Load();
                if (args.Length >= 1) cfg.ApplyLink(args[0]);
                if (!cfg.Valid)
                {
                    using (var dlg = new SettingsForm(cfg))
                        if (dlg.ShowDialog() != DialogResult.OK) return 0;
                }
                cfg.Save();
                Application.Run(new CallForm(cfg));
            }
            return 0;
        }
    }

    // ---------- 자체 시험 (CI에서 실제 윈도우로 실행) ----------
    static class SelfTest
    {
        static StreamWriter log;

        static void Log(string s) { log.WriteLine(s); log.Flush(); }

        static bool WaitFor(Func<bool> cond, int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until) { if (cond()) return true; Thread.Sleep(100); }
            return cond();
        }

        static void Check(bool ok, string what)
        {
            Log((ok ? "PASS " : "FAIL ") + what);
            if (!ok) throw new Exception(what);
        }

        public static int Run(string dir)
        {
            Directory.CreateDirectory(dir);
            using (log = new StreamWriter(Path.Combine(dir, "selftest.log"), false, new UTF8Encoding(false)))
            {
                try
                {
                    string topic = "selftest-" + Guid.NewGuid().ToString("N").Substring(0, 12);
                    var core = new Core(topic, "7", "7번 노트북");
                    var other = new Core(topic, "8", "8번 노트북");
                    bool conn = false;
                    var cts = new CancellationTokenSource();
                    new Thread(() => Ntfy.Listen(topic, "10m", m => { lock (core) { core.OnMessage(m); other.OnMessage(m); } }, on => conn = on, cts.Token)) { IsBackground = true }.Start();
                    Check(WaitFor(() => conn, 15000), "ntfy 연결");

                    var call = core.CallPub();
                    Log("call json: " + Ntfy.ToJson(call));
                    Ntfy.Publish(call);
                    Check(WaitFor(() => core.State == State.Waiting, 15000), "호출 → 대기 상태");
                    Check(other.State == State.Idle, "다른 노트북은 그대로");

                    // 현황판의 [내가 갈게]와 같은 메시지
                    Ntfy.Publish(new Pub { Topic = topic, Title = "✅ 7번 노트북 응답", Message = "민수 가는 중", Tags = new[] { "ack", "b-7" }, Priority = 1 });
                    Check(WaitFor(() => core.State == State.Coming && core.Who == "민수", 15000), "현황판 응답 → 오는 중 (민수)");

                    Ntfy.Publish(core.CancelPub());
                    Check(WaitFor(() => core.State == State.Idle, 15000), "취소 → 대기 해제");

                    // 휴대폰 알림 버튼: 호출 메시지에 담긴 http 동작을 그대로 실행
                    Ntfy.Publish(core.CallPub());
                    Check(WaitFor(() => core.State == State.Waiting, 15000), "다시 호출");
                    var act = call.Actions[0];
                    Ntfy.Post(act.Url, act.Body);
                    Check(WaitFor(() => core.State == State.Coming && core.Who == "", 15000), "휴대폰 [내가 갈게] → 오는 중");

                    // 링크 해석
                    var c = new Config();
                    Check(c.ApplyLink("https://vecar0.github.io/terabyte/?t=booth-yjsj9s&booth=3&name=3%EB%B2%88+%EB%85%B8%ED%8A%B8%EB%B6%81") && c.Topic == "booth-yjsj9s" && c.Booth == "3" && c.Name == "3번 노트북", "참가 링크 해석");
                    cts.Cancel();

                    // 그림: 상태·크기별 PNG
                    var states = new[] { State.Idle, State.Waiting, State.Coming };
                    foreach (var sz in new[] { new Size(170, 64), new Size(240, 100), new Size(340, 150) })
                        foreach (var st in states)
                        {
                            var pc = new Core(topic, "3", "3번 노트북");
                            pc.Set(st, st == State.Coming ? "민수" : "");
                            using (var bmp = new Bitmap(sz.Width, sz.Height))
                            {
                                using (var g = Graphics.FromImage(bmp)) Painter.Paint(g, sz, pc, false, true, 1f);
                                bmp.Save(Path.Combine(dir, "preview-" + sz.Width + "x" + sz.Height + "-" + st.ToString().ToLower() + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                            }
                        }
                    Log("PASS 그림 저장");

                    // 설정 창이 만들어지는지
                    using (var f = new SettingsForm(new Config())) { f.CreateControl(); }
                    using (var f = new CallForm(new Config { Topic = topic, Booth = "3" })) { f.CreateControl(); }
                    Log("PASS 창 생성");
                    Log("ALL PASS");
                    return 0;
                }
                catch (Exception ex)
                {
                    Log("ERROR " + ex);
                    return 1;
                }
            }
        }
    }
}
