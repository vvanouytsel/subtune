using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using static Subtune.Native;

namespace Subtune;

/// <summary>
/// A transparent, always-on-top, click-through window that shows the current lyric line.
/// It's a per-pixel-alpha layered window: the text is drawn once into a bitmap whenever it
/// changes, and Windows composites that bitmap; nothing is repainted in between.
/// </summary>
sealed class Overlay : Form
{
    const int ResizeGrip = 14;
    const int FadeStep = 40;

    readonly Settings _settings;
    readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    readonly System.Windows.Forms.Timer _stayOnTop = new() { Interval = 2000 };
    FontFamily _family;

    string _current = "", _next = "";
    bool _editing, _lyricsShown;
    byte _alpha, _targetAlpha;

    public Overlay(Settings settings)
    {
        _settings = settings;
        _family = ResolveFamily(settings.FontFamily);
        _lyricsShown = settings.LyricsShown;

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(240, 60);
        Text = "Subtune";
        Bounds = InitialBounds(settings);

        _fade.Tick += (_, _) => StepFade();
        _stayOnTop.Tick += (_, _) => KeepOnTop();
    }

    /// <summary>Text shown in move/resize mode telling the user how to lock it again.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string LockHint { get; set; } = "Use the tray menu";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool LyricsShown
    {
        get => _lyricsShown;
        set { _lyricsShown = value; UpdateVisibility(); }
    }

    /// <summary>Move/resize mode: the overlay takes the mouse and shows a frame to drag.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Editing
    {
        get => _editing;
        set
        {
            if (_editing == value)
                return;
            _editing = value;
            if (IsHandleCreated)
            {
                long style = GetWindowLongPtr(Handle, GWL_EXSTYLE);
                style = value ? style & ~WS_EX_TRANSPARENT : style | WS_EX_TRANSPARENT;
                SetWindowLongPtr(Handle, GWL_EXSTYLE, (IntPtr)style);
            }
            Render();
            UpdateVisibility();
        }
    }

    public void SetLines(string current, string next)
    {
        if (current == _current && next == _next)
            return;
        _current = current;
        _next = next;
        Render();
        UpdateVisibility();
    }

    /// <summary>Re-reads layout-affecting settings (next line on/off).</summary>
    public void Relayout() => Render();

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            if (!_editing)
                cp.ExStyle |= WS_EX_TRANSPARENT; // clicks fall through to the game
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Render();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Render();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST && _editing)
        {
            long lp = m.LParam.ToInt64();
            var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            bool left = p.X < ResizeGrip, right = p.X >= Width - ResizeGrip;
            bool top = p.Y < ResizeGrip, bottom = p.Y >= Height - ResizeGrip;
            m.Result = (left, right, top, bottom) switch
            {
                (true, _, true, _) => HTTOPLEFT,
                (_, true, true, _) => HTTOPRIGHT,
                (true, _, _, true) => HTBOTTOMLEFT,
                (_, true, _, true) => HTBOTTOMRIGHT,
                (true, _, _, _) => HTLEFT,
                (_, true, _, _) => HTRIGHT,
                (_, _, true, _) => HTTOP,
                (_, _, _, true) => HTBOTTOM,
                _ => HTCAPTION, // drag anywhere else to move
            };
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fade.Dispose();
            _stayOnTop.Dispose();
            _family.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- showing and hiding ----

    void UpdateVisibility()
    {
        bool show = _editing || (_lyricsShown && (_current.Length > 0 || _next.Length > 0));
        _targetAlpha = show ? (byte)255 : (byte)0;
        if (show && !Visible)
        {
            Show();
            _stayOnTop.Start();
        }
        if (_alpha != _targetAlpha)
            _fade.Start();
    }

    void StepFade()
    {
        _alpha = (byte)(_alpha < _targetAlpha
            ? Math.Min(_targetAlpha, _alpha + FadeStep)
            : Math.Max(_targetAlpha, _alpha - FadeStep));
        if (IsHandleCreated)
        {
            var blend = BLENDFUNCTION.Alpha(_alpha);
            UpdateLayeredWindowAlpha(Handle, 0, 0, 0, 0, 0, 0, ref blend, ULW_ALPHA);
        }
        if (_alpha != _targetAlpha)
            return;
        _fade.Stop();
        if (_alpha == 0)
        {
            Hide();
            _stayOnTop.Stop();
        }
    }

    // Borderless games sit on top too; re-assert our place now and then.
    void KeepOnTop() =>
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    // ---- drawing ----

    void Render()
    {
        if (!IsHandleCreated || Width < 1 || Height < 1)
            return;

        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            string current = _current, next = _next;
            if (_editing)
            {
                DrawEditFrame(g);
                if (current.Length == 0) current = "Drag to move, drag the edges to resize";
                if (next.Length == 0) next = $"{LockHint} to lock it in place";
            }

            float w = Width, h = Height, pad = h * 0.06f;
            if (_settings.ShowNextLine || next.Length > 0)
            {
                float split = h * 0.62f;
                DrawText(g, current, new RectangleF(pad, pad, w - 2 * pad, split - pad), h * 0.30f,
                    Color.White, StringAlignment.Far);
                DrawText(g, next, new RectangleF(pad, split + h * 0.02f, w - 2 * pad, h - split - pad), h * 0.17f,
                    Color.FromArgb(225, 205, 205, 205), StringAlignment.Near);
            }
            else
            {
                DrawText(g, current, new RectangleF(pad, pad, w - 2 * pad, h - 2 * pad), h * 0.40f,
                    Color.White, StringAlignment.Center);
            }
        }
        Present(bitmap);
    }

    /// <summary>Outlined text that stays readable on any background; shrinks to fit its box.</summary>
    void DrawText(Graphics g, string text, RectangleF box, float emSize, Color fill, StringAlignment vertical)
    {
        if (text.Length == 0 || box.Width < 8 || box.Height < 8)
            return;

        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = vertical };
        var path = new GraphicsPath();
        for (int i = 0; i < 12; i++)
        {
            path.Reset();
            path.AddString(text, _family, (int)FontStyle.Regular, emSize, box, format);
            var bounds = path.GetBounds();
            if (bounds.Height <= box.Height && bounds.Width <= box.Width)
                break;
            emSize *= 0.9f;
        }

        using (path)
        using (var outline = new Pen(Color.FromArgb(235, 10, 10, 10), Math.Max(2f, emSize * 0.13f)) { LineJoin = LineJoin.Round })
        using (var brush = new SolidBrush(fill))
        {
            g.DrawPath(outline, path);
            g.FillPath(brush, path);
        }
    }

    void DrawEditFrame(Graphics g)
    {
        var rect = new RectangleF(1, 1, Width - 3, Height - 3);
        using var frame = RoundedRect(rect, 10);
        using var background = new SolidBrush(Color.FromArgb(150, 18, 18, 22));
        using var border = new Pen(Color.FromArgb(230, 127, 119, 221), 2) { DashStyle = DashStyle.Dash };
        g.FillPath(background, frame);
        g.DrawPath(border, frame);

        using var grip = new Pen(Color.FromArgb(230, 220, 220, 220), 2);
        for (int i = 1; i <= 3; i++)
        {
            int o = i * 5;
            g.DrawLine(grip, Width - 4 - o, Height - 4, Width - 4, Height - 4 - o);
        }
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Hands the bitmap to Windows as the window's new look.</summary>
    void Present(Bitmap bitmap)
    {
        IntPtr screen = GetDC(0);
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr previous = SelectObject(memory, hBitmap);
        try
        {
            var size = new SIZE(bitmap.Width, bitmap.Height);
            var source = new POINT(0, 0);
            var destination = new POINT(Left, Top);
            var blend = BLENDFUNCTION.Alpha(_alpha);
            UpdateLayeredWindow(Handle, screen, ref destination, ref size, memory, ref source, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(hBitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    static FontFamily ResolveFamily(string name)
    {
        try { return new FontFamily(name); }
        catch (ArgumentException) { return new FontFamily("Segoe UI"); }
    }

    static Rectangle InitialBounds(Settings s)
    {
        if (s is { X: { } x, Y: { } y, Width: { } w, Height: { } h })
        {
            var saved = new Rectangle(x, y, w, h);
            if (Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(saved)))
                return saved; // only if that monitor is still attached
        }
        var area = Screen.PrimaryScreen!.WorkingArea;
        int width = (int)(area.Width * 0.6), height = Math.Max(90, (int)(area.Height * 0.11));
        return new Rectangle(area.Left + (area.Width - width) / 2, area.Bottom - height - (int)(area.Height * 0.06), width, height);
    }
}
