using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace WslSyncMonitor;

/// <summary>
/// Mini‑widget compacto na barra de tarefas, ao lado da área de notificação. Arraste com o botão esquerdo para
/// reposicionar.
/// <para>
/// Por padrão é uma janela filha da própria barra (<see cref="Settings.WidgetInsideTaskbar"/>): fica sempre visível
/// junto com ela, inclusive quando o menu Iniciar do Windows 11 põe a barra numa camada acima de todas as janelas
/// "sempre visíveis" (por cima dela, o widget ficava escondido), desliza junto no auto-ocultar e o mouse sobre ele
/// conta como mouse sobre a barra. No modo antigo é uma janela por cima da barra, que acompanha a posição dela.
/// </para>
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

    IntPtr _parent; // janela da barra de tarefas, no modo dentro da barra

    bool Inside => _parent != IntPtr.Zero;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (Inside)
            {
                cp.Parent = _parent;
                cp.Style = Native.WS_CHILD | Native.WS_CLIPSIBLINGS;
                cp.ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            }
            else cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>Retângulo do widget na tela (dentro da barra, a posição da janela é relativa a ela).</summary>
    public Rectangle ScreenBounds =>
        IsHandleCreated && Native.GetWindowRect(Handle, out var r) ? r.ToRectangle() : Bounds;

    /// <summary>
    /// No desligamento do Windows: solta o widget da barra antes de a interface do app ficar ocupada (uma janela
    /// filha de outro processo liga as filas de entrada dos dois: a barra travaria junto).
    /// </summary>
    public void DetachForShutdown()
    {
        _follow.Stop();
        if (IsHandleCreated) DestroyHandle();
    }

    public void SetSnapshot(Snapshot s) { _snap = s; _dirty = true; Follow(); }

    bool _busy;
    /// <summary>Operação em andamento (iniciar/encerrar WSL): o ponto do WSL fica azul.</summary>
    public bool Busy { get => _busy; set { if (_busy == value) return; _busy = value; _dirty = true; Follow(); } }

    /// <summary>Enquanto devolver true (um menu aberto), o widget não se recoloca no topo e fica atrás dele.</summary>
    public Func<bool>? YieldTo { get; set; }

    /// <summary>Recoloca o widget acima da barra (a barra sobe na ordem Z ao ganhar o foco).</summary>
    public void BringAboveTaskbar()
    {
        if (IsHandleCreated && Visible && !Inside && !(YieldTo?.Invoke() ?? false))
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // reage na hora a qualquer troca de janela em primeiro plano, evitando que o widget "pisque" atrás da barra
    Native.WinEventProc? _fgProc;
    IntPtr _fgHook;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // o WinForms não usa o pai do CreateParams num Form (põe a janela filha numa janela interna oculta):
        // prende-a à barra explicitamente
        if (Inside && Native.SetParent(Handle, _parent) == IntPtr.Zero)
            Log.Warn("widget", "não foi possível pôr o widget dentro da barra de tarefas");
        _fgProc = (_, _, _, _, _, _, _) => BringAboveTaskbar();
        _fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_fgHook != IntPtr.Zero) { Native.UnhookWinEvent(_fgHook); _fgHook = IntPtr.Zero; }
        base.OnHandleDestroyed(e);
    }

    /// <summary>Ponto de ancoragem para o painel detalhado.</summary>
    public Point PopupAnchor => ScreenBounds is var r ? new(r.Left + r.Width / 2, r.Top + r.Height / 2) : default;

    float S => (_tb?.Dpi ?? 96) / 96f;
    int P(float v) => (int)Math.Round(v * S);

    // ícones da barra (Iniciar, apps, clima…), lidos pela UIA num thread de fundo a cada poucos segundos
    volatile List<(int Start, int End)>? _taskbarIcons;
    DateTime _iconsReadAt;
    bool _readingIcons;

    void RefreshTaskbarIcons(TaskbarInfo tb)
    {
        if (_readingIcons || DateTime.Now - _iconsReadAt < TimeSpan.FromSeconds(5)) return;
        _readingIcons = true;
        _iconsReadAt = DateTime.Now;
        var (hwnd, notify, left, cornerWidth) = (tb.Hwnd, tb.NotifyRect, tb.Rect.Left, P(200));
        Task.Run(() => TaskbarIcons.Read(hwnd, notify, left, cornerWidth)).ContinueWith(t =>
        {
            _taskbarIcons = t.Result;
            _readingIcons = false;
        }, TaskScheduler.Default);
    }

    bool _hiddenLogged;

    /// <summary>
    /// Modo dentro da barra: vira janela filha dela, e de novo se a barra for recriada (o Explorer reiniciou).
    /// Desligado nas configurações: volta a ser uma janela por cima da barra.
    /// </summary>
    void AttachToTaskbar(TaskbarInfo tb)
    {
        var wanted = _settings.WidgetInsideTaskbar ? tb.Hwnd : IntPtr.Zero;
        if (wanted == _parent && (IsHandleCreated || wanted == IntPtr.Zero)) return;
        _parent = wanted;
        if (IsHandleCreated) RecreateHandle(); // os estilos e o pai vêm do CreateParams
        _dirty = true;
        Log.Info("widget", Inside ? "dentro da barra de tarefas" : "por cima da barra de tarefas");
    }

    /// <summary>
    /// Onde estava o mouse quando o widget sumiu: com a barra que se oculta sozinha, o Windows pode recolhê-la
    /// quando o mouse está sobre o widget (uma janela por cima dela), e o widget some e volta em ciclo.
    /// </summary>
    string HideContext(TaskbarInfo tb)
    {
        if (!tb.AutoHide) return "";
        try
        {
            var cur = Cursor.Position;
            var under = Native.WindowFromPoint(cur);
            var cls = new System.Text.StringBuilder(64);
            _ = Native.GetClassName(under, cls, cls.Capacity);
            return $" · auto-ocultar; mouse em {cur.X},{cur.Y} ({(ScreenBounds.Contains(cur) ? "sobre o widget" : "fora do widget")}," +
                   $" janela sob o mouse: {(under == Handle ? "o widget" : cls.ToString())}); barra {tb.Rect}";
        }
        catch { return ""; }
    }

    static readonly DateTime AppStart = System.Diagnostics.Process.GetCurrentProcess().StartTime;
    bool _everShown;

    /// <summary>Por que o widget está oculto (vai para o log, para diagnosticar quando ele demora a aparecer).</summary>
    static string WhyHidden(TaskbarInfo tb) =>
        tb.Hwnd == IntPtr.Zero ? "a barra de tarefas ainda não existe (Windows iniciando?)"
        : tb.Thickness <= 0 ? "a barra de tarefas não tem tamanho ainda"
        : tb.FullscreenApp ? $"o Windows informa app em tela cheia ou ocupado (estado {tb.NotificationState})"
        : "a barra de tarefas está oculta ou fora da tela";

    void Follow()
    {
        if (IsDisposed) return;
        var tb = TaskbarInfo.Get();
        _tb = tb;
        if (WslService.SessionEnding) return; // desligando: o widget já foi solto da barra
        AttachToTaskbar(tb);
        // por cima da barra, com ela auto-ocultável, o widget precisa acompanhar a animação; dentro dela, desliza junto
        _follow.Interval = tb.AutoHide && !Inside ? 150 : 500;
        bool show = _settings.ShowTaskbarWidget && tb.Thickness > 0 &&
                    (Inside ? tb.Hwnd != IntPtr.Zero : tb.FullyShown && !tb.FullscreenApp);
        if (!show)
        {
            if (_settings.ShowTaskbarWidget && (Visible || !_hiddenLogged))
                Log.Info("widget", "oculto: " + WhyHidden(tb) + HideContext(tb)); // só na mudança (aparecido → oculto)
            _hiddenLogged = true;
            if (Visible) Hide();
            return;
        }
        bool appearing = !Visible;
        if (appearing)
            Log.Info("widget", _everShown ? "exibido" : $"exibido {(DateTime.Now - AppStart).TotalSeconds:0.0} s após o início do app");
        _everShown = true;

        var size = MeasureSize(tb);
        Point loc;
        if (tb.Horizontal)
        {
            int trayLeft = tb.NotifyRect is { } n && n.Left > tb.Rect.Left ? n.Left : tb.Rect.Right - P(220);
            RefreshTaskbarIcons(tb);
            // sem espaço junto da bandeja, vai para o outro lado; enquanto o usuário arrasta, segue o mouse
            int x = WidgetPlacement.ChooseX(tb.Rect.Left, trayLeft, _dragging ? [] : _taskbarIcons ?? [],
                size.Width, P(6), _settings.WidgetOffset);
            loc = new Point(x, tb.Rect.Top + (tb.Rect.Height - size.Height) / 2);
        }
        else
        {
            int trayTop = tb.NotifyRect is { } n && n.Top > tb.Rect.Top ? n.Top : tb.Rect.Bottom - P(220);
            int y = trayTop - size.Height - P(6) - _settings.WidgetOffset;
            y = Math.Clamp(y, tb.Rect.Top, tb.Rect.Bottom - size.Height);
            loc = new Point(tb.Rect.Left + (tb.Rect.Width - size.Width) / 2, y);
        }

        if (Inside) loc.Offset(-tb.Rect.Left, -tb.Rect.Top); // janela filha: posição relativa à barra
        var key = $"{loc}|{size}|{tb.Horizontal}|{Theme.TaskbarLight}|{_hover}";
        if (!Visible)
        {
            Bounds = new Rectangle(loc, size);
            Show();
            _dirty = true;
        }
        // o WinForms "estaciona" a janela filha numa janela interna dele ao mostrá-la: devolve-a à barra
        if (Inside && Native.GetParent(Handle) != _parent)
        {
            Native.SetParent(Handle, _parent);
            Native.ShowWindow(Handle, Native.SW_SHOWNA);
            _dirty = true;
        }
        if (appearing)
            Log.Info("widget", $"na tela em {ScreenBounds} ({(Inside ? "dentro da barra" : "por cima da barra")}; " +
                $"visível: {Native.IsWindowVisible(Handle)}, pai: {(Native.GetParent(Handle) == _parent && Inside ? "a barra" : "0x" + ((long)Native.GetParent(Handle)).ToString("x", System.Globalization.CultureInfo.InvariantCulture))})");
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
        // dentro da barra a posição já foi definida (e é relativa a ela); por cima, vai junto com o desenho
        Native.SetLayeredBitmap(Handle, bmp, Inside ? null : new Point(Left, Top));
    }

    /// <summary>O conteúdo do widget sobre uma barra da cor do tema, sem exibir a janela (capturas de tela do CI).</summary>
    internal Bitmap RenderPreview(TaskbarInfo tb, Snapshot? snap)
    {
        _tb = tb;
        _snap = snap;
        var size = MeasureSize(tb);
        var bmp = new Bitmap(size.Width + P(16), Math.Max(size.Height, tb.Thickness));
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Theme.TaskbarLight ? Color.FromArgb(238, 238, 238) : Color.FromArgb(28, 28, 28));
        g.TranslateTransform(P(8), (bmp.Height - size.Height) / 2f);
        PaintContent(g, size.Width, size.Height, draw: true);
        return bmp;
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
        var dLevel = s == null || s.St != StState.Running ? Level.Off : s.DevicesLevel;

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
