using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace WslSyncMonitor;

/// <summary>
/// Mini‑widget compacto desenhado sobre a barra de tarefas, ao lado da área de notificação.
/// Acompanha a barra: some quando ela se auto‑oculta (ou quando há app em tela cheia) e volta junto com ela.
/// Arraste com o botão esquerdo para reposicionar.
/// </summary>
public sealed class TaskbarWidget : Form
{
    readonly Settings _settings;
    readonly System.Windows.Forms.Timer _follow = new() { Interval = 150 };
    Snapshot? _snap;
    TaskbarInfo? _tb;
    bool _hover, _dirty = true;
    string _lastKey = "";

    // arrastar
    bool _down, _dragging;
    Point _downPt;
    int _downOffset;

    public event Action? Clicked;
    public event Action? RightClicked;
    public event Action? HoverStarted;
    public event Action? OffsetChanged;

    public TaskbarWidget(Settings settings)
    {
        _settings = settings;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "WSL Sync Monitor Widget";
        _follow.Tick += (_, _) => Follow();
        _follow.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void SetSnapshot(Snapshot s) { _snap = s; _dirty = true; Follow(); }

    bool _busy;
    /// <summary>Operação em andamento (iniciar/encerrar WSL): o ponto do WSL fica azul.</summary>
    public bool Busy { get => _busy; set { if (_busy == value) return; _busy = value; _dirty = true; Follow(); } }

    /// <summary>Recoloca o widget acima da barra (a barra sobe na ordem Z ao ganhar o foco).</summary>
    public void BringAboveTaskbar()
    {
        if (IsHandleCreated && Visible)
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // reage na hora a qualquer troca de janela em primeiro plano, evitando que o widget "pisque" atrás da barra
    Native.WinEventProc? _fgProc;
    IntPtr _fgHook;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _fgProc = (_, _, _, _, _, _, _) => BringAboveTaskbar();
        _fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_fgHook != IntPtr.Zero) { Native.UnhookWinEvent(_fgHook); _fgHook = IntPtr.Zero; }
        base.OnHandleDestroyed(e);
    }

    /// <summary>Ponto de ancoragem para o painel detalhado.</summary>
    public Point PopupAnchor => new(Left + Width / 2, Top + Height / 2);

    float S => (_tb?.Dpi ?? 96) / 96f;
    int P(float v) => (int)Math.Round(v * S);

    void Follow()
    {
        if (IsDisposed) return;
        var tb = TaskbarInfo.Get();
        _tb = tb;
        // com a barra auto-ocultável o widget precisa acompanhar a animação; fixa, basta conferir de vez em quando
        _follow.Interval = tb.AutoHide ? 150 : 500;
        bool show = _settings.ShowTaskbarWidget && tb.FullyShown && !tb.FullscreenApp && tb.Thickness > 0;
        if (!show)
        {
            if (Visible) Hide();
            return;
        }

        var size = MeasureSize(tb);
        Point loc;
        if (tb.Horizontal)
        {
            int trayLeft = tb.NotifyRect is { } n && n.Left > tb.Rect.Left ? n.Left : tb.Rect.Right - P(220);
            int x = trayLeft - size.Width - P(6) - _settings.WidgetOffset;
            x = Math.Clamp(x, tb.Rect.Left, tb.Rect.Right - size.Width);
            loc = new Point(x, tb.Rect.Top + (tb.Rect.Height - size.Height) / 2);
        }
        else
        {
            int trayTop = tb.NotifyRect is { } n && n.Top > tb.Rect.Top ? n.Top : tb.Rect.Bottom - P(220);
            int y = trayTop - size.Height - P(6) - _settings.WidgetOffset;
            y = Math.Clamp(y, tb.Rect.Top, tb.Rect.Bottom - size.Height);
            loc = new Point(tb.Rect.Left + (tb.Rect.Width - size.Width) / 2, y);
        }

        var key = $"{loc}|{size}|{tb.Horizontal}|{Theme.TaskbarLight}|{_hover}";
        if (!Visible)
        {
            Bounds = new Rectangle(loc, size);
            Show();
            _dirty = true;
        }
        key += $"|{_busy}";
        if (_dirty || key != _lastKey)
        {
            Bounds = new Rectangle(loc, size);
            Render();
            _lastKey = key;
            _dirty = false;
        }
        // a barra fica acima de janelas topmost quando clicada; reafirma a ordem
        BringAboveTaskbar();
    }

    (Snapshot?, int, int) _measureKey;
    Size _measured;

    bool TwoLines(TaskbarInfo tb) => tb.Horizontal && tb.Thickness >= P(38);

    Size MeasureSize(TaskbarInfo tb)
    {
        if (!tb.Horizontal) return new Size(Math.Max(P(30), tb.Thickness - P(6)), P(70));
        int h = tb.Thickness - P(8);
        // largura dinâmica: o mínimo para caber o conteúdo atual (recalculada só quando algo muda)
        var key = (_snap, h, tb.Dpi);
        if (key == _measureKey) return _measured;
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _measureKey = key;
        return _measured = new Size((int)Math.Ceiling(PaintContent(g, 0, h, draw: false)), h);
    }

    // ------------------------------------------------------------------ desenho

    const string FolderGlyph = "", DeviceGlyph = "";

    void Render()
    {
        if (_tb == null || Width <= 0 || Height <= 0) return;
        bool light = Theme.TaskbarLight;

        using var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // fundo quase invisível (alpha 1) para receber o mouse; realce ao passar o mouse
            var bgAlpha = _hover ? 40 : 1;
            using (var bg = new SolidBrush(Color.FromArgb(bgAlpha, light ? Color.Black : Color.White)))
            using (var path = Round(new Rectangle(0, 0, Width, Height), P(5)))
                g.FillPath(bg, path);

            PaintContent(g, Width, Height, draw: true);
        }
        Native.SetLayeredBitmap(Handle, bmp, Left, Top);
    }

    /// <summary>Desenha (draw = true) ou só mede o conteúdo. Retorna a largura necessária.</summary>
    float PaintContent(Graphics g, int W, int H, bool draw)
    {
        var s = _snap;
        var fg = Theme.TaskbarLight ? Color.FromArgb(20, 20, 20) : Color.FromArgb(245, 245, 245);
        using var font = new Font("Segoe UI", P(11.5f), FontStyle.Regular, GraphicsUnit.Pixel);
        using var icons = new Font("Segoe MDL2 Assets", P(11f), FontStyle.Regular, GraphicsUnit.Pixel);
        using var fb = new SolidBrush(fg);

        var wsl = Busy ? Level.Busy : s?.WslLevel ?? Level.Off;
        var st = s?.StLevel ?? Level.Off;
        var folders = s == null || s.St != StState.Running ? "–" : $"{s.FoldersOk}/{s.Folders.Count}";
        var devices = s == null || s.St != StState.Running ? "–" : $"{s.DevicesOnline}/{s.Devices.Count}";
        var fLevel = s == null || s.St != StState.Running ? Level.Off : Worst(s.Folders.Select(f => f.Status.Level));
        var dLevel = s == null || s.St != StState.Running ? Level.Off : Worst(s.Devices.Select(d => d.Status.Level));

        int dot = P(7);
        float x0 = P(8);
        float lineH = font.GetHeight(g);

        float maxX = 0;
        float MeasureW(string t, Font f) => g.MeasureString(t, f, PointF.Empty, StringFormat.GenericTypographic).Width;
        void DotAt(float x, float cy, Level l) { maxX = Math.Max(maxX, x + dot); if (!draw) return; using var b = new SolidBrush(Theme.For(l)); g.FillEllipse(b, x, cy - dot / 2f, dot, dot); }
        float Text(string t, Font f, float x, float cy, Brush? br = null)
        {
            var sz = g.MeasureString(t, f, PointF.Empty, StringFormat.GenericTypographic);
            if (draw) g.DrawString(t, f, br ?? fb, x, cy - sz.Height / 2f, StringFormat.GenericTypographic);
            maxX = Math.Max(maxX, x + sz.Width);
            return x + sz.Width;
        }
        float Item(float x, float cy, Level l, string label)
        {
            DotAt(x, cy, l);
            return Text(label, font, x + dot + P(4), cy);
        }
        float Count(float x, float cy, string glyph, Level l, string value)
        {
            using var gb = new SolidBrush(l == Level.Off ? fg : Theme.For(l));
            x = Text(glyph, icons, x, cy, gb) + P(3);
            return Text(value, font, x, cy);
        }

        if (_tb != null && !_tb.Horizontal)
        {
            if (!draw) return W;
            float cy = P(10), step = (H - P(20)) / 3f;
            DotAt(W / 2f - dot - P(2), cy, wsl); DotAt(W / 2f + P(2), cy, st);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var small = new Font("Segoe UI", P(10f), GraphicsUnit.Pixel);
            g.DrawString(folders, small, fb, new RectangleF(0, cy + step - lineH / 2, W, lineH), sf);
            g.DrawString(devices, small, fb, new RectangleF(0, cy + 2 * step - lineH / 2, W, lineH), sf);
        }
        else if (_tb != null && TwoLines(_tb))
        {
            // duas colunas alinhadas, independente da largura de cada valor
            float cy1 = H * 0.30f, cy2 = H * 0.72f;
            float col1 = Math.Max(dot + P(4) + MeasureW("WSL", font), MeasureW(FolderGlyph, icons) + P(3) + MeasureW(folders, font));
            float col2 = x0 + col1 + P(10);
            Item(x0, cy1, wsl, "WSL");
            Item(col2, cy1, st, "Sync");
            Count(x0, cy2, FolderGlyph, fLevel, folders);
            Count(col2, cy2, DeviceGlyph, dLevel, devices);
        }
        else
        {
            float cy = H / 2f;
            float x = Item(x0, cy, wsl, "WSL") + P(8);
            x = Item(x, cy, st, "Sync") + P(10);
            x = Count(x, cy, FolderGlyph, fLevel, folders) + P(8);
            Count(x, cy, DeviceGlyph, dLevel, devices);
        }
        return maxX + P(8);
    }

    static Level Worst(IEnumerable<Level> levels)
    {
        var l = levels.ToList();
        if (l.Count == 0) return Level.Off;
        foreach (var x in new[] { Level.Error, Level.Busy, Level.Warn, Level.Ok }) if (l.Contains(x)) return x;
        return Level.Off;
    }

    static GraphicsPath Round(Rectangle r, int rad)
    {
        var p = new GraphicsPath(); int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseEnter(EventArgs e) { _hover = true; _dirty = true; HoverStarted?.Invoke(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _dirty = true; base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _down = true; _dragging = false; _downPt = Cursor.Position; _downOffset = _settings.WidgetOffset; Capture = true; }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_down && _tb != null)
        {
            var cur = Cursor.Position;
            int delta = _tb.Horizontal ? _downPt.X - cur.X : _downPt.Y - cur.Y;
            if (!_dragging && Math.Abs(delta) > P(4)) _dragging = true;
            if (_dragging)
            {
                _settings.WidgetOffset = Math.Max(-P(400), _downOffset + delta);
                Follow();
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _down)
        {
            _down = false; Capture = false;
            if (_dragging) OffsetChanged?.Invoke();
            else Clicked?.Invoke();
            _dragging = false;
        }
        else if (e.Button == MouseButtons.Right) RightClicked?.Invoke();
        base.OnMouseUp(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _follow.Dispose();
        base.Dispose(disposing);
    }
}
