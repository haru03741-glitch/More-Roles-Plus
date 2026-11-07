using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MoreRolesPlus.Splash
{
    // MRP の起動中の画面。左に MRP の役職の絵 (入れ替わる)、右に MRP の名前・4 つの段階の道のり・今の様子・区切りの棒。
    // 枠の無い横長の板で、どこを持っても動かせる。右上の × で閉じる (ゲームはそのまま)
    internal sealed class SplashForm : Form
    {
        // ── 色 (夜の紫と金) ──
        private static readonly Color Back = Color.FromArgb(20, 17, 31);
        private static readonly Color Panel = Color.FromArgb(33, 28, 51);
        private static readonly Color Line = Color.FromArgb(62, 54, 92);
        private static readonly Color Fore = Color.FromArgb(244, 240, 252);
        private static readonly Color Sub = Color.FromArgb(160, 152, 186);
        private static readonly Color Gold = Color.FromArgb(255, 201, 74);
        private static readonly Color Pink = Color.FromArgb(255, 92, 138);
        private static readonly Color Slow = Color.FromArgb(255, 140, 90);

        private const int StageCount = 4;
        private const int LW = 560, LH = 320, ArtW = 206; // 論理の大きさ (96 dpi)
        private readonly bool _ja;
        private readonly Process _game;
        private readonly string _cacheDir;
        private readonly string[] _labels;
        private readonly ConcurrentQueue<string> _inbox = new ConcurrentQueue<string>();
        private readonly Timer _timer;

        // 役職の絵 (絵・名前・一言)
        private readonly List<(Image img, string name, string blurb)> _roles = new List<(Image, string, string)>();
        private readonly int _roleStart;

        private readonly double[] _start = new double[StageCount + 1];
        private readonly double[] _end = new double[StageCount + 1];
        private readonly double[] _expect = { 0, 3, 2, 5, 8 }; // 前の回の時間 (無ければこの目安)
        private readonly DateTime _gameStart;
        private readonly DateTime _shownAt = DateTime.Now;
        private int _stage = 1;
        private int _plugins, _pluginIndex;
        private string _pluginName = "";
        private string _detail = "";
        private bool _done;
        private DateTime _doneAt;
        private float _shown;
        private bool _closeHover;

        public SplashForm(Process game, bool ja, string gameExe, string cacheDir)
        {
            _game = game;
            _ja = ja;
            _cacheDir = cacheDir;
            try { _gameStart = game.StartTime; }
            catch { _gameStart = DateTime.Now; }
            _labels = ja
                ? new[] { "", "下準備", "仕掛け", "MOD", "メニュー" }
                : new[] { "", "Loader", "Patches", "Mods", "Menu" };
            LoadExpect();
            LoadRoles();
            _roleStart = (int)((DateTime.Now.Ticks / TimeSpan.TicksPerSecond) % Math.Max(1, _roles.Count));

            Text = "More Roles Plus";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            TopMost = true;
            BackColor = Back;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            ClientSize = new Size(LW, LH);
            Font = new Font(PickFont(), 9f);
            try { if (File.Exists(gameExe)) Icon = Icon.ExtractAssociatedIcon(gameExe); }
            catch { }

            _timer = new Timer { Interval = 33 };
            _timer.Tick += (s, e) => Pump();
            _timer.Start();
        }

        // 影と角の丸み (Windows 11 は角を丸める)
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        }

        public void Post(string line) => _inbox.Enqueue(line);

        private double Now => (DateTime.Now - _gameStart).TotalSeconds;
        private float S => DeviceDpi / 96f;

        // ── 届いた行 ─────────────────────────────

        private void Pump()
        {
            while (_inbox.TryDequeue(out var line)) OnLine(line);
            bool gameGone;
            try { gameGone = _game.HasExited; }
            catch { gameGone = true; }
            if (gameGone) { Close(); return; }
            // メニューは出てから 1 秒余りかけて明るくなるので、それまで見せてから薄くして閉じる
            if (_done && (DateTime.Now - _doneAt).TotalSeconds > 1.8) { Close(); return; }
            float target = Progress();
            _shown += (target - _shown) * 0.18f;
            if (_done) Opacity = Math.Max(0.05, Math.Min(1.0, (1.8 - (DateTime.Now - _doneAt).TotalSeconds) / 0.6));
            Invalidate();
        }

        private void OnLine(string line)
        {
            int sp = line.IndexOf(' ');
            string cmd = sp < 0 ? line : line.Substring(0, sp);
            string arg = sp < 0 ? "" : line.Substring(sp + 1);
            switch (cmd)
            {
                case "STAGE":
                {
                    var parts = arg.Split(' ');
                    if (int.TryParse(parts[0], out int n)) GoTo(Math.Min(n, StageCount), At(parts));
                    break;
                }
                case "COUNT": int.TryParse(arg, out _plugins); break;
                case "PLUGIN": _pluginIndex++; _pluginName = arg; break;
                case "LOG": _detail = arg; break;
                case "DONE": GoTo(StageCount + 1, At(new[] { "", arg })); break;
                case "EOF":
                    if (!_done) { _done = true; _doneAt = DateTime.Now.AddSeconds(3); }
                    break;
            }
        }

        private double At(string[] parts) =>
            parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : Now;

        private void GoTo(int stage, double now)
        {
            while (_stage < stage)
            {
                _end[_stage] = now;
                if (_stage + 1 <= StageCount) _start[_stage + 1] = now;
                _stage++;
            }
            if (_stage > StageCount && !_done)
            {
                _done = true;
                _doneAt = DateTime.Now;
                _shown = 1f;
                SaveExpect();
            }
        }

        // 段階ごとの中の進み (0〜1)。今の段階は「経った時間 / 前の回の時間」(95% で止める)
        private float StageFill(int i)
        {
            if (_done || i < _stage) return 1f;
            if (i > _stage) return 0f;
            double k = Math.Min(0.95, (Now - _start[i]) / Math.Max(0.3, _expect[i]));
            if (i == 3 && _plugins > 0) k = Math.Max(k, Math.Min(0.95, (_pluginIndex - 0.5) / _plugins));
            return (float)Math.Max(0, k);
        }

        private float Progress()
        {
            double total = 0, got = 0;
            for (int i = 1; i <= StageCount; i++)
            {
                double w = Math.Max(0.3, _expect[i]);
                total += w;
                got += w * StageFill(i);
            }
            return (float)(got / total);
        }

        // ── 前の回の時間 ─────────────────────────────

        private string ExpectPath => string.IsNullOrEmpty(_cacheDir) ? null : Path.Combine(_cacheDir, "morerolesplus-splash.txt");

        private void LoadExpect()
        {
            try
            {
                var path = ExpectPath;
                if (path == null || !File.Exists(path)) return;
                var parts = File.ReadAllText(path).Trim().Split(' ');
                for (int i = 0; i < Math.Min(parts.Length, StageCount); i++)
                    if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 && v < 600) _expect[i + 1] = v;
            }
            catch { }
        }

        private void SaveExpect()
        {
            try
            {
                var path = ExpectPath;
                if (path == null) return;
                var sb = new StringBuilder();
                for (int i = 1; i <= StageCount; i++)
                    sb.Append((_end[i] - _start[i]).ToString("0.00", CultureInfo.InvariantCulture)).Append(' ');
                File.WriteAllText(path, sb.ToString().Trim());
            }
            catch { }
        }

        // ── 役職の絵 (exe に埋めた MRP の絵) ─────────────────────

        private void LoadRoles()
        {
            var list = new[]
            {
                ("Architect", "フォートレスメイカー", "設計図から船を作り変えろ", "Architect", "Reshape the ship from your blueprint"),
                ("Demolisher", "デモリッシャー", "ハンマーで壁を壊して道を作れ", "Demolisher", "Smash walls to open new paths"),
                ("PortalMaker", "ポータルメーカー", "2 つのポータルで船の中をつなげ", "Portal Maker", "Link two places with portals"),
                ("Rewinder", "クロノリバース", "時を戻して、起きたことをなかったことにしろ", "Rewinder", "Turn back time and undo what happened"),
            };
            var asm = typeof(SplashForm).Assembly;
            foreach (var r in list)
            {
                try
                {
                    using (var st = asm.GetManifestResourceStream("Illus." + r.Item1 + ".jpg"))
                    {
                        if (st == null) continue;
                        var img = Image.FromStream(st);
                        _roles.Add((img, _ja ? r.Item2 : r.Item4, _ja ? r.Item3 : r.Item5));
                    }
                }
                catch { }
            }
        }

        // ── 動かす・閉じる ─────────────────────────────

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        private RectangleF CloseRect => new RectangleF((LW - 34) * S, 8 * S, 26 * S, 26 * S);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (CloseRect.Contains(e.Location)) { Close(); return; }
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // 題の帯を持ったのと同じ
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool h = CloseRect.Contains(e.Location);
            if (h != _closeHover) { _closeHover = h; Cursor = h ? Cursors.Hand : Cursors.Default; }
        }

        // ── 描く ────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float s = S;
            float W = LW * s, H = LH * s, aw = ArtW * s;

            DrawArt(g, new RectangleF(0, 0, aw, H), s);
            using (var pen = new Pen(Line, 1f * s)) g.DrawRectangle(pen, 0, 0, W - 1, H - 1);

            float x0 = aw + 24 * s, x1 = W - 24 * s;

            // 名前
            using (var big = new Font(Font.FontFamily, 19f, FontStyle.Bold))
            using (var pill = new Font(Font.FontFamily, 10f, FontStyle.Bold))
            using (var b = new SolidBrush(Fore))
            {
                const string name = "MORE ROLES";
                g.DrawString(name, big, b, x0 - 3 * s, 22 * s);
                var sz = g.MeasureString(name, big);
                var pr = new RectangleF(x0 + sz.Width - 2 * s, 30 * s, 46 * s, 22 * s);
                using (var lg = new LinearGradientBrush(pr, Gold, Pink, LinearGradientMode.Horizontal)) FillRound(g, lg, pr, 11 * s);
                using (var tb = new SolidBrush(Back))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("PLUS", pill, tb, pr, sf);
            }
            using (var small = new Font(Font.FontFamily, 8.5f))
            using (var b = new SolidBrush(Sub))
                g.DrawString(_ja ? "起動しています  ·  " + Secs(Now) : "Starting up  ·  " + Secs(Now), small, b, x0, 58 * s);

            // 閉じる
            var cr = CloseRect;
            if (_closeHover) using (var hb = new SolidBrush(Panel)) FillRound(g, hb, cr, 6 * s);
            using (var pen = new Pen(_closeHover ? Fore : Sub, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float m = 9 * s;
                g.DrawLine(pen, cr.X + m, cr.Y + m, cr.Right - m, cr.Bottom - m);
                g.DrawLine(pen, cr.Right - m, cr.Y + m, cr.X + m, cr.Bottom - m);
            }

            DrawRoute(g, x0, x1, 112 * s, s);

            // 今の様子
            float y = 186 * s;
            using (var bold = new Font(Font.FontFamily, 10.5f, FontStyle.Bold))
            using (var b = new SolidBrush(Fore))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(Headline(), bold, b, new RectangleF(x0, y, x1 - x0, 22 * s), sf);
            using (var small = new Font(Font.FontFamily, 8f))
            using (var b = new SolidBrush(Sub))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter })
                g.DrawString(_detail, small, b, new RectangleF(x0, y + 24 * s, x1 - x0, 30 * s), sf);

            DrawBar(g, new RectangleF(x0, H - 46 * s, x1 - x0, 8 * s), s);
            using (var small = new Font(Font.FontFamily, 8f))
            using (var b = new SolidBrush(Sub))
            {
                string pct = ((int)Math.Round(_shown * 100)).ToString(CultureInfo.InvariantCulture) + "%";
                var sz = g.MeasureString(pct, small);
                g.DrawString(pct, small, b, x1 - sz.Width + 3 * s, H - 32 * s);
                g.DrawString(_ja ? "遅い段階は橙色で出ます" : "Slow steps turn orange", small, b, x0 - 3 * s, H - 32 * s);
            }
        }

        // 左の絵: 4 秒ごとに入れ替わる。下に役職の名前と一言
        private void DrawArt(Graphics g, RectangleF r, float s)
        {
            using (var bb = new SolidBrush(Panel)) g.FillRectangle(bb, r);
            if (_roles.Count > 0)
            {
                double t = (DateTime.Now - _shownAt).TotalSeconds;
                const double each = 4.0, fade = 0.6;
                int idx = (int)(t / each);
                double inEach = t - idx * each;
                var cur = _roles[(_roleStart + idx) % _roles.Count];
                var prev = _roles[(_roleStart + idx + _roles.Count - 1) % _roles.Count];
                float a = idx == 0 ? 1f : (float)Math.Min(1.0, inEach / fade);
                var state = g.Save();
                g.SetClip(r);
                if (a < 1f) DrawCover(g, prev.img, r, 1f, (float)(each + inEach) / (float)each);
                DrawCover(g, cur.img, r, a, (float)inEach / (float)each);
                // 下の暗がり
                var shade = new RectangleF(r.X, r.Bottom - 110 * s, r.Width, 110 * s);
                using (var lg = new LinearGradientBrush(shade, Color.FromArgb(0, Back), Color.FromArgb(235, Back), LinearGradientMode.Vertical))
                    g.FillRectangle(lg, shade.X, shade.Y + 1, shade.Width, shade.Height);
                using (var nf = new Font(Font.FontFamily, 11f, FontStyle.Bold))
                using (var bf = new Font(Font.FontFamily, 8f))
                using (var nb = new SolidBrush(Color.FromArgb((int)(255 * a), Gold)))
                using (var tb = new SolidBrush(Color.FromArgb((int)(220 * a), Fore)))
                {
                    g.DrawString(cur.name, nf, nb, r.X + 14 * s, r.Bottom - 74 * s);
                    g.DrawString(cur.blurb, bf, tb, new RectangleF(r.X + 14 * s, r.Bottom - 52 * s, r.Width - 24 * s, 46 * s));
                }
                g.Restore(state);
            }
            // 右の縁の光
            using (var lg = new LinearGradientBrush(new RectangleF(r.Right - 2 * s, 0, 2 * s, r.Height), Gold, Pink, LinearGradientMode.Vertical))
                g.FillRectangle(lg, r.Right - 2 * s, 0, 2 * s, r.Height);
        }

        // 絵を枠いっぱいに (はみ出す分は切る)。k (0〜1) でゆっくり寄る
        private static void DrawCover(Graphics g, Image img, RectangleF r, float alpha, float k)
        {
            float zoom = 1.04f + 0.05f * Math.Max(0f, Math.Min(1.4f, k));
            float scale = Math.Max(r.Width / img.Width, r.Height / img.Height) * zoom;
            float w = img.Width * scale, h = img.Height * scale;
            var dst = new RectangleF(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2.6f, w, h);
            if (alpha >= 0.999f) { g.DrawImage(img, dst); return; }
            using (var ia = new System.Drawing.Imaging.ImageAttributes())
            {
                var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha };
                ia.SetColorMatrix(cm);
                g.DrawImage(img, new[] { dst.Location, new PointF(dst.Right, dst.Top), new PointF(dst.Left, dst.Bottom) }, new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, ia);
            }
        }

        // 4 つの段階の道のり: 丸 (済み = 金の丸と印・今 = 脈打つ輪・まだ = 薄い丸) と、その間の線が進みに合わせて伸びる
        private void DrawRoute(Graphics g, float x0, float x1, float y, float s)
        {
            float rad = 11 * s;
            float step = (x1 - x0 - rad * 2) / (StageCount - 1);
            using (var dim = new Pen(Line, 3f * s))
            using (var lit = new Pen(Gold, 3f * s))
                for (int i = 1; i < StageCount; i++)
                {
                    float ax = x0 + rad + step * (i - 1) + rad, bx = x0 + rad + step * i - rad;
                    g.DrawLine(dim, ax, y, bx, y);
                    float f = i < _stage || _done ? 1f : i == _stage ? StageFill(i) : 0f;
                    if (f > 0) g.DrawLine(lit, ax, y, ax + (bx - ax) * f, y);
                }
            using (var lf = new Font(Font.FontFamily, 8.5f, FontStyle.Bold))
            using (var tf = new Font(Font.FontFamily, 8f))
            using (var center = new StringFormat { Alignment = StringAlignment.Center })
                for (int i = 1; i <= StageCount; i++)
                {
                    float cx = x0 + rad + step * (i - 1);
                    bool done = i < _stage || _done;
                    bool cur = i == _stage && !_done;
                    var c = new RectangleF(cx - rad, y - rad, rad * 2, rad * 2);
                    if (done)
                    {
                        using (var b = new SolidBrush(Gold)) g.FillEllipse(b, c);
                        using (var pen = new Pen(Back, 2.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                            g.DrawLines(pen, new[] { new PointF(cx - 4.5f * s, y + 0.2f * s), new PointF(cx - 1.2f * s, y + 3.6f * s), new PointF(cx + 4.8f * s, y - 3.4f * s) });
                    }
                    else if (cur)
                    {
                        float pulse = (float)(0.5 + 0.5 * Math.Sin(Now * 5));
                        float halo = rad + (3 + 4 * pulse) * s;
                        using (var hb = new SolidBrush(Color.FromArgb((int)(60 + 50 * pulse), Pink))) g.FillEllipse(hb, cx - halo, y - halo, halo * 2, halo * 2);
                        using (var b = new SolidBrush(Back)) g.FillEllipse(b, c);
                        using (var pen = new Pen(Pink, 2.4f * s)) g.DrawEllipse(pen, c);
                        float a = (float)(Now * 300 % 360);
                        using (var pen = new Pen(Gold, 2.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawArc(pen, cx - rad + 4.5f * s, y - rad + 4.5f * s, rad * 2 - 9 * s, rad * 2 - 9 * s, a, 220);
                    }
                    else
                    {
                        using (var b = new SolidBrush(Panel)) g.FillEllipse(b, c);
                        using (var pen = new Pen(Line, 2f * s)) g.DrawEllipse(pen, c);
                    }
                    using (var b = new SolidBrush(done ? Fore : cur ? Gold : Sub))
                        g.DrawString(_labels[i], lf, b, new RectangleF(cx - step / 2, y + rad + 6 * s, step, 18 * s), center);
                    double secs = done ? _end[i] - _start[i] : cur ? Now - _start[i] : -1;
                    if (secs >= 0)
                        using (var b = new SolidBrush(done && secs > 5 ? Slow : Sub))
                            g.DrawString(Secs(secs), tf, b, new RectangleF(cx - step / 2, y + rad + 23 * s, step, 16 * s), center);
                }
        }

        // 段階ごとに区切った棒 (幅は前の回の時間の割合)
        private void DrawBar(Graphics g, RectangleF r, float s)
        {
            double total = 0;
            for (int i = 1; i <= StageCount; i++) total += Math.Max(0.3, _expect[i]);
            float gap = 4 * s, avail = r.Width - gap * (StageCount - 1), x = r.X;
            for (int i = 1; i <= StageCount; i++)
            {
                float w = (float)(avail * Math.Max(0.3, _expect[i]) / total);
                var seg = new RectangleF(x, r.Y, w, r.Height);
                using (var b = new SolidBrush(Panel)) FillRound(g, b, seg, r.Height / 2);
                float f = StageFill(i);
                if (f > 0)
                {
                    var fill = new RectangleF(x, r.Y, Math.Max(r.Height, w * f), r.Height);
                    using (var lg = new LinearGradientBrush(new RectangleF(r.X, r.Y, r.Width, r.Height), Gold, Pink, LinearGradientMode.Horizontal)) FillRound(g, lg, fill, r.Height / 2);
                }
                x += w + gap;
            }
        }

        private string Secs(double v) => v.ToString("0.0", CultureInfo.InvariantCulture) + (_ja ? "秒" : "s");

        private string Headline()
        {
            if (_done) return _ja ? "準備ができました。いってらっしゃい！" : "All set. Have fun!";
            switch (_stage)
            {
                case 1: return _ja ? "MOD の土台を起こしています" : "Waking up the mod loader";
                case 2: return _ja ? "起動を速くする仕掛けを当てています" : "Applying boot speed-ups";
                case 3:
                    if (_plugins > 0 && _pluginIndex > 0)
                        return (_ja ? "読み込み中  " : "Loading  ") + _pluginName + $"  ({_pluginIndex}/{_plugins})";
                    return _ja ? "MOD を読み込んでいます" : "Loading mods";
                default: return _ja ? "メニューを用意しています" : "Getting the menu ready";
            }
        }

        // ── 小物 ────────────────────────────────

        private static string PickFont()
        {
            foreach (var name in new[] { "Yu Gothic UI", "Meiryo UI", "Segoe UI" })
                using (var f = new Font(name, 9f))
                    if (f.Name == name) return name;
            return SystemFonts.MessageBoxFont.Name;
        }

        private static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static void FillRound(Graphics g, Brush b, RectangleF r, float rad)
        {
            using (var p = Round(r, rad)) g.FillPath(b, p);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            base.OnFormClosed(e);
        }
    }
}
