using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WslSyncMonitor;

public interface IPopupActions
{
    void OpenSyncthing();
    void RefreshNow();
    void StartWsl();
    void StopWsl();
    void ShowDistroMenu(string name, Point screen);
}

/// <summary>Painel detalhado (aberto ao passar o mouse ou clicar no ícone/widget).</summary>
public sealed class DetailPopup : Form
{
    readonly IPopupActions _actions;
    Snapshot? _snap;
    Point _anchor;
    int _scroll, _bodyHeight, _bodyViewport;
    Theme.Palette _pal = Theme.Current;
    readonly List<(Rectangle Rect, string Label, Action Click, bool Primary)> _buttons = new();
    int _hoverButton = -1;
    readonly List<(Rectangle Rect, string Name)> _distroRows = new();

    public bool Pinned { get; private set; }
    public DateTime LastHiddenAt { get; private set; }

    public DetailPopup(IPopupActions actions)
    {
        _actions = actions;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        Font = new Font("Segoe UI", 9f);
        Text = "WSL Sync Monitor";
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND (Windows 11)
        try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
    }

    float S => DeviceDpi / 96f;
    int P(float v) => (int)Math.Round(v * S);

    // ------------------------------------------------------------------ API

    public void ShowAt(Point anchor, bool pin)
    {
        _anchor = anchor;
        _pal = Theme.Current;
        BackColor = _pal.Bg;
        if (pin) Pinned = true;
        Relayout();
        if (!Visible) { _scroll = 0; Show(); }
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        if (pin) { Activate(); Focus(); }
    }

    public void HidePopup()
    {
        if (!Visible) return;
        Pinned = false;
        Hide();
        LastHiddenAt = DateTime.Now;
    }

    string? _busy;
    string? _guard;

    /// <summary>Estado da proteção de sincronização (linha extra na seção WSL).</summary>
    public void SetGuard(string? text) { if (_guard == text) return; _guard = text; if (Visible) { Relayout(); Invalidate(); } }

    /// <summary>Mostra uma operação em andamento no cabeçalho (null = limpa).</summary>
    public void SetBusy(string? text) { _busy = text; if (Visible) { BuildButtons(); Invalidate(); } }

    public void SetSnapshot(Snapshot s)
    {
        _snap = s;
        if (Visible) { Relayout(); Invalidate(); }
    }

    protected override void OnActivated(EventArgs e) { base.OnActivated(e); Pinned = true; }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); if (Pinned) HidePopup(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) HidePopup();
        else if (e.KeyCode == Keys.F5) _actions.RefreshNow();
        base.OnKeyDown(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); if (Visible) Relayout(); }

    // ------------------------------------------------------------------ layout

    int HeaderH => P(46);
    int FooterH => P(48);
    int W => P(400);

    void Relayout()
    {
        using (var bmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(bmp))
            _bodyHeight = DrawBody(g, 0, measureOnly: true);

        var tb = TaskbarInfo.Get();
        var screen = Screen.FromPoint(_anchor).Bounds;
        int maxBody = (int)(screen.Height * 0.78) - HeaderH - FooterH - tb.Thickness;
        _bodyViewport = Math.Min(_bodyHeight, Math.Max(P(120), maxBody));
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _bodyHeight - _bodyViewport));

        var size = new Size(W, HeaderH + _bodyViewport + FooterH);
        var loc = tb.PlacePopup(size, _anchor, P(10));
        Bounds = new Rectangle(loc, size);
        BuildButtons();
    }

    void BuildButtons()
    {
        _buttons.Clear();
        // botões de mesma largura dividindo o rodapé
        int h = P(28), gap = P(8), margin = P(12), y = Height - FooterH + (FooterH - h) / 2;
        void Add(string label, int _, Action a, bool primary) => _buttons.Add((Rectangle.Empty, label, a, primary));
        // "Abrir Syncthing" só aparece quando há uma interface para abrir
        if (_snap?.St == StState.Running) Add("Abrir Syncthing", P(150), _actions.OpenSyncthing, true);
        if (_busy != null) Add("Aguarde…", P(130), () => { }, false);
        else if (_snap?.WslRunning == true) Add("Encerrar WSL", P(130), _actions.StopWsl, false);
        else if (_snap?.Target != null) Add("Iniciar WSL", P(150), _actions.StartWsl, true);
        Add("Atualizar", 0, _actions.RefreshNow, false);

        int n = _buttons.Count, w = (Width - 2 * margin - gap * (n - 1)) / n;
        for (int i = 0; i < n; i++)
            _buttons[i] = _buttons[i] with { Rect = new Rectangle(margin + i * (w + gap), y, w, h) };
    }

    // ------------------------------------------------------------------ pintura

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_pal.Bg);
        using (var pen = new Pen(_pal.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        // cabeçalho
        var lvl = _snap?.Overall ?? Level.Off;
        Dot(g, P(18), HeaderH / 2, P(10), lvl);
        using (var bold = new Font("Segoe UI Semibold", 11f))
            TextRenderer.DrawText(g, "WSL · Syncthing", bold, new Rectangle(P(32), 0, P(220), HeaderH), _pal.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        var when = _busy ?? (_snap == null ? "Carregando…" : $"Atualizado às {_snap.Time:HH:mm:ss}");
        TextRenderer.DrawText(g, when, Font, new Rectangle(0, 0, Width - P(14), HeaderH), _busy != null ? Theme.For(Level.Busy) : _pal.Dim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        using (var pen = new Pen(_pal.Border)) g.DrawLine(pen, 0, HeaderH, Width, HeaderH);

        // corpo rolável
        var body = new Rectangle(0, HeaderH + 1, Width, _bodyViewport - 1);
        // obs.: TextRenderer (GDI) ignora transformações do Graphics, por isso o deslocamento é passado em y
        var state = g.Save();
        g.SetClip(body);
        DrawBody(g, HeaderH - _scroll, measureOnly: false);
        g.Restore(state);

        if (_bodyHeight > _bodyViewport) // barra de rolagem fina
        {
            int trackH = _bodyViewport - P(8);
            int thumbH = Math.Max(P(24), trackH * _bodyViewport / _bodyHeight);
            int thumbY = HeaderH + P(4) + (trackH - thumbH) * _scroll / Math.Max(1, _bodyHeight - _bodyViewport);
            using var b = new SolidBrush(Color.FromArgb(110, _pal.Dim));
            FillRound(g, b, new Rectangle(Width - P(6), thumbY, P(3), thumbH), P(2));
        }

        // rodapé
        int fy = HeaderH + _bodyViewport;
        using (var pen = new Pen(_pal.Border)) g.DrawLine(pen, 0, fy, Width, fy);
        for (int i = 0; i < _buttons.Count; i++)
        {
            var (r, label, _, primary) = _buttons[i];
            var bg = primary ? _pal.Accent : (i == _hoverButton ? _pal.Hover : _pal.Card);
            if (primary && i == _hoverButton) bg = ControlPaint.Light(bg, 0.25f);
            using (var b = new SolidBrush(bg)) FillRound(g, b, r, P(5));
            if (!primary) using (var pen = new Pen(_pal.Border)) DrawRound(g, pen, r, P(5));
            var fg = primary ? (Theme.AppsLight ? Color.White : Color.Black) : _pal.Text;
            TextRenderer.DrawText(g, label, Font, r, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    const TextFormatFlags Clip = TextFormatFlags.PreserveGraphicsClipping;

    /// <summary>Desenha (ou só mede) o conteúdo. Retorna a altura total.</summary>
    int DrawBody(Graphics g, int y, bool measureOnly)
    {
        int left = P(14), right = W - P(14), rowH = P(22), subH = P(18);
        var small = new Font("Segoe UI", 8f);
        var bold = new Font("Segoe UI Semibold", 9f);
        try
        {
            void Section(string title)
            {
                y += P(10);
                if (!measureOnly)
                    TextRenderer.DrawText(g, title, bold, new Rectangle(left, y, right - left, P(16)), _pal.Dim, TextFormatFlags.Left | TextFormatFlags.NoPrefix | Clip);
                y += P(18);
            }

            void Row(Level lvl, string name, string status, Font? f = null, Color? statusColor = null)
            {
                if (!measureOnly)
                {
                    Dot(g, left + P(5), y + rowH / 2, P(8), lvl);
                    var sw = Math.Min(TextRenderer.MeasureText(status, Font).Width, (right - left) / 2 + P(20));
                    TextRenderer.DrawText(g, name, f ?? Font, new Rectangle(left + P(16), y, right - left - P(20) - sw, rowH), _pal.Text,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | Clip);
                    TextRenderer.DrawText(g, status, Font, new Rectangle(right - sw, y, sw, rowH), statusColor ?? Theme.For(lvl),
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | Clip);
                }
                y += rowH;
            }

            void Sub(string text, Color? c = null)
            {
                if (string.IsNullOrEmpty(text)) return;
                if (!measureOnly)
                    TextRenderer.DrawText(g, text, small, new Rectangle(left + P(16), y - P(2), right - left - P(16), subH), c ?? _pal.Dim,
                        TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | Clip);
                y += subH;
            }

            void Wrap(string text, Color c)
            {
                var r = new Rectangle(left + P(16), y, right - left - P(16), 0);
                var sz = TextRenderer.MeasureText(text, small, new Size(r.Width, int.MaxValue), TextFormatFlags.WordBreak);
                r.Height = sz.Height;
                if (!measureOnly) TextRenderer.DrawText(g, text, small, r, c, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | Clip);
                y += sz.Height + P(4);
            }

            void Bar(double pct, Level lvl)
            {
                if (!measureOnly)
                {
                    var r = new Rectangle(left + P(16), y, right - left - P(16), P(4));
                    using (var b = new SolidBrush(_pal.Hover)) FillRound(g, b, r, P(2));
                    var fr = r with { Width = (int)(r.Width * Math.Clamp(pct, 0, 100) / 100) };
                    if (fr.Width > 0) using (var b = new SolidBrush(Theme.For(lvl))) FillRound(g, b, fr, P(2));
                }
                y += P(9);
            }

            var s = _snap;
            if (!measureOnly) _distroRows.Clear();
            if (s == null) { Section("Estado"); Row(Level.Off, "Consultando…", ""); return y + P(10); }

            // ---- WSL ----
            Section("WSL");
            if (!s.WslInstalled) Row(Level.Error, "WSL", "Não instalado");
            foreach (var d in s.Distros)
            {
                bool target = d == s.Target;
                var name = d.Name + (d.IsDefault ? " (padrão)" : "") + (d.Version > 0 ? $" · WSL{d.Version}" : "");
                if (!measureOnly) _distroRows.Add((new Rectangle(0, y, W, rowH), d.Name));
                Row(d.Running ? Level.Ok : Level.Off, name, (d.Running ? "Em execução" : "Parada") + " ▾", target ? bold : null);
            }
            if (s.WslError != null) Wrap(s.WslError, Theme.For(Level.Error));
            if (_guard != null) Wrap("Proteção: " + char.ToLower(_guard[0]) + _guard[1..], _guard.StartsWith("Sincronizado") || _guard.StartsWith("Em uso") ? _pal.Dim : Theme.For(Level.Busy));

            // ---- Syncthing ----
            Section("Syncthing");
            Row(s.StLevel, "Syncthing" + (s.Target != null ? $" em {s.Target.Name}" : ""), s.StStatusText, bold);
            if (s.St == StState.Running)
            {
                Sub($"Ativo há {Fmt.Duration(s.StUptime)} · ID {s.MyId?[..Math.Min(7, s.MyId.Length)]} · ↓ {Fmt.Rate(s.InRate)}  ↑ {Fmt.Rate(s.OutRate)}");
                Sub(s.Url ?? "");
            }
            if (s.StError != null) Wrap(s.StError, Theme.For(Level.Error));

            if (s.St != StState.Running) return y + P(10);

            // ---- Pastas ----
            int hidden = s.AllFolders.Count - s.Folders.Count;
            Section($"Pastas · {s.FoldersOk}/{s.Folders.Count} atualizadas" + (hidden > 0 ? $" · {hidden} oculta(s)" : ""));
            if (s.Folders.Count == 0) Sub(hidden > 0 ? "Todas as pastas estão ocultas" : "Nenhuma pasta configurada");
            foreach (var f in s.Folders.OrderBy(f => f.Label, StringComparer.CurrentCultureIgnoreCase))
            {
                var (txt, lvl) = f.Status;
                Row(lvl, f.Label, txt);
                var type = f.Type switch { "sendonly" => " · somente envio", "receiveonly" => " · somente recebimento", "receiveencrypted" => " · criptografada", _ => "" };
                Sub($"{Fmt.Bytes(f.GlobalBytes)} · {f.SharedWith} dispositivo(s){type} · {f.Path}");
                if (lvl == Level.Busy && f.State == "syncing") Bar(f.Percent, lvl);
            }

            // ---- Dispositivos ----
            Section($"Dispositivos · {s.DevicesOnline}/{s.Devices.Count} conectados");
            if (s.Devices.Count == 0) Sub("Nenhum dispositivo remoto");
            foreach (var d in s.Devices.OrderByDescending(d => d.Connected).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var (txt, lvl) = d.Status;
                Row(lvl, d.Name, txt);
                if (d.Connected)
                {
                    var addr = d.Address;
                    var kind = d.ConnType.Contains("relay") ? "relay" : d.ConnType.Split('-').FirstOrDefault() ?? "";
                    Sub($"↓ {Fmt.Rate(d.InRate)}  ↑ {Fmt.Rate(d.OutRate)} · {kind} {addr} · {d.ClientVersion}");
                    if (lvl == Level.Busy) Bar(d.Completion, lvl);
                }
                else Sub("ID " + d.Id[..Math.Min(15, d.Id.Length)] + "…");
            }
            return y + P(10);
        }
        finally { small.Dispose(); bold.Dispose(); }
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_bodyHeight > _bodyViewport)
        {
            _scroll = Math.Clamp(_scroll - e.Delta / 120 * P(40), 0, _bodyHeight - _bodyViewport);
            Invalidate();
        }
        base.OnMouseWheel(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int idx = _buttons.FindIndex(b => b.Rect.Contains(e.Location));
        bool overDistro = InBody(e.Location) && _distroRows.Any(r => r.Rect.Contains(e.Location));
        Cursor = idx >= 0 || overDistro ? Cursors.Hand : Cursors.Default;
        if (idx != _hoverButton) { _hoverButton = idx; Invalidate(); }
        base.OnMouseMove(e);
    }

    bool InBody(Point p) => p.Y > HeaderH && p.Y < HeaderH + _bodyViewport;

    protected override void OnMouseLeave(EventArgs e) { if (_hoverButton != -1) { _hoverButton = -1; Invalidate(); } base.OnMouseLeave(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            var b = _buttons.FirstOrDefault(b => b.Rect.Contains(e.Location));
            b.Click?.Invoke();
            if (b.Click == null && InBody(e.Location) && _distroRows.FirstOrDefault(r => r.Rect.Contains(e.Location)) is { Name: not null } row)
                _actions.ShowDistroMenu(row.Name, PointToScreen(e.Location));
        }
        base.OnMouseUp(e);
    }

    // ------------------------------------------------------------------ helpers

    static void Dot(Graphics g, int cx, int cy, int d, Level l)
    {
        var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(Theme.For(l));
        g.FillEllipse(b, cx - d / 2f, cy - d / 2f, d, d);
        g.SmoothingMode = old;
    }

    static GraphicsPath RoundPath(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = Math.Max(1, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void FillRound(Graphics g, Brush b, Rectangle r, int rad) { using var p = RoundPath(r, rad); g.FillPath(b, p); }
    static void DrawRound(Graphics g, Pen pen, Rectangle r, int rad) { using var p = RoundPath(r, rad); g.DrawPath(pen, p); }
}
