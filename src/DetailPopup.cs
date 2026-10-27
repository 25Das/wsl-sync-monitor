using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WslSyncMonitor;

/// <summary>O que o painel pede ao app (o painel só desenha e repassa cliques).</summary>
public interface IPopupActions
{
    void RefreshNow();
    void CheckForUpdate();
    void InstallUpdate();
    void ShowTechInfo();
    void RunFooterAction(FooterAction action);
    void ShowItemMenu(PanelItem item, Point screen);
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
    readonly List<(Rectangle Rect, PanelItem Item)> _itemRows = new(); // linhas clicáveis (distro, Syncthing, pasta, dispositivo)

    bool _clicked, _locked;

    /// <summary>Estado para as regras de <see cref="PanelBehavior"/> (aberto por clique, fixado pelo pin).</summary>
    public PanelState State => new(Visible, _clicked, _locked);

    /// <summary>Aplica a regra do evento: fecha ou traz para frente. "Abrir por clique" fica com quem chama.</summary>
    public PanelAction Apply(PanelEvent e)
    {
        var action = PanelBehavior.Decide(State, e);
        if (action == PanelAction.Hide) HidePopup();
        else if (action == PanelAction.BringToFront)
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE);
            Activate();
        }
        return action;
    }
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
        try { _ = DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } // no Windows 10 falha e fica quadrado: tudo bem
    }

    float S => DeviceDpi / 96f;
    int P(float v) => (int)Math.Round(v * S);

    // ------------------------------------------------------------------ API

    public void ShowAt(Point anchor, bool pin)
    {
        _anchor = anchor;
        _pal = Theme.Current;
        BackColor = _pal.Bg;
        if (pin) _clicked = true;
        Relayout();
        if (!Visible) { _scroll = 0; Show(); }
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        if (pin) { Activate(); Focus(); }
    }

    /// <summary>Fecha o painel. Fixado pelo pin, só fecha com <paramref name="evenIfLocked"/>.</summary>
    public void HidePopup(bool evenIfLocked = false)
    {
        if (!Visible || (_locked && !evenIfLocked)) return;
        _clicked = _locked = false;
        _tip.Hide(this);
        Hide();
        LastHiddenAt = DateTime.Now;
    }

    string? _busy;
    string? _guard;

    /// <summary>Estado da proteção de sincronização (linha extra na seção WSL).</summary>
    public void SetGuard(string? text) { if (_guard == text) return; _guard = text; if (Visible) { Relayout(); Invalidate(); } }

    /// <summary>Mostra uma operação em andamento no cabeçalho (null = limpa).</summary>
    public void SetBusy(string? text) { _busy = text; if (Visible) { BuildButtons(); Invalidate(); } }

    UpdateState _update = UpdateState.NotChecked;

    /// <summary>Estado da busca por versão nova do app (canto direito da primeira linha do cabeçalho).</summary>
    public void SetUpdate(UpdateState u) { _update = u; if (Visible) Invalidate(); }

    PanelHeader Header => PanelHeader.For(_snap, _busy, Updater.Current, _update);

    public void SetSnapshot(Snapshot s)
    {
        _snap = s;
        if (Visible) { Relayout(); Invalidate(); }
    }

    /// <summary>O painel no tamanho real, sem exibi-lo (capturas de tela do CI).</summary>
    internal Bitmap RenderPreview()
    {
        _pal = Theme.Current;
        BackColor = _pal.Bg;
        Relayout();
        var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
        return bmp;
    }

    protected override void OnActivated(EventArgs e) { base.OnActivated(e); _clicked = true; }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Apply(PanelEvent.Deactivated); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Apply(PanelEvent.Escape);
        else if (e.KeyCode == Keys.F5) RefreshAll();
        base.OnKeyDown(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); if (Visible) Relayout(); }

    // ------------------------------------------------------------------ layout

    int HeaderH => P(60);
    int Line1 => P(32); // altura da primeira linha do cabeçalho; a segunda vai até HeaderH
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
        var t = _snap?.Target;
        foreach (var b in PanelFooter.For(t?.Name, t?.Running == true, _snap?.St == StState.Running, _busy))
            _buttons.Add((Rectangle.Empty, b.Label, () => _actions.RunFooterAction(b.Action), b.Primary));
        if (_buttons.Count == 0) return;

        // botões de mesma largura dividindo o rodapé
        int h = P(28), gap = P(8), margin = P(12), y = Height - FooterH + (FooterH - h) / 2;
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

        DrawHeader(g);
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
    const string Menu = " ▾"; // indica que a linha abre um menu

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
            if (!measureOnly) _itemRows.Clear();
            void Clickable(PanelItem item) { if (!measureOnly) _itemRows.Add((new Rectangle(0, y, W, rowH), item)); }
            if (s == null) { Section("Estado"); Row(Level.Unknown, "Consultando…", ""); return y + P(10); }

            // ---- WSL ----
            Section("WSL");
            if (!s.WslInstalled) Row(Level.Error, "WSL", "Não instalado");
            foreach (var d in s.Distros)
            {
                bool target = d == s.Target;
                var name = d.Name + (d.IsDefault ? " (padrão)" : "") + (d.Version > 0 ? $" · WSL{d.Version}" : "") + (target ? " · Syncthing" : "");
                Clickable(new(PanelItemKind.Distro, d.Name));
                Row(d.Running ? Level.Ok : Level.Off, name, (d.Running ? "Em execução" : "Parada") + Menu, target ? bold : null);
            }
            if (s.WslError != null) Wrap(s.WslError, Theme.For(s.WslLevel)); // erro ou desconhecido (leitura falhou)

            // ---- Proteção (seção própria: o texto começa pelo nome da distro) ----
            if (_guard != null)
            {
                Section("Proteção da sincronização");
                bool working = _guard.Contains("verificando") || _guard.Contains("aguardando") || _guard.Contains("confirmando");
                Wrap(_guard, working ? Theme.For(Level.Busy) : _pal.Text);
            }

            // ---- Syncthing ----
            Section("Syncthing");
            Clickable(new(PanelItemKind.Syncthing, ""));
            Row(s.StLevel, "Syncthing" + (s.Target != null ? $" em {s.Target.Name}" : ""), s.StStatusText + Menu, bold);
            if (s.St == StState.Running)
            {
                Sub($"Ativo há {Fmt.Duration(s.StUptime)} · ID {s.MyId?[..Math.Min(7, s.MyId.Length)]} · ↓ {Fmt.Rate(s.InRate)}  ↑ {Fmt.Rate(s.OutRate)}");
                Sub(s.Url ?? "");
            }
            if (s.StError != null) Wrap(s.StError, Theme.For(s.StLevel));

            if (s.St != StState.Running) return y + P(10);

            // ---- Pastas ----
            int hidden = s.AllFolders.Count - s.Folders.Count;
            Section($"Pastas · {s.FoldersHealthy}/{s.Folders.Count} sem problemas"
                + (s.FoldersPaused > 0 ? $" · {s.FoldersPaused} pausada(s)" : "") + (hidden > 0 ? $" · {hidden} oculta(s)" : ""));
            if (s.Folders.Count == 0) Sub(hidden > 0 ? "Todas as pastas estão ocultas" : "Nenhuma pasta configurada");
            foreach (var f in s.Folders.OrderBy(f => f.Label, StringComparer.CurrentCultureIgnoreCase))
            {
                var (txt, lvl) = f.Status;
                Clickable(new(PanelItemKind.Folder, f.Id));
                Row(lvl, f.Label + (f.Hidden ? " (oculta, com problema)" : ""), txt + Menu);
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
                Clickable(new(PanelItemKind.Device, d.Id));
                Row(lvl, d.Name, txt + Menu);
                if (d.Connected)
                {
                    Sub(d.ConnectionText);
                    if (lvl == Level.Busy) Bar(d.Completion, lvl);
                }
                else Sub("ID " + d.Id[..Math.Min(15, d.Id.Length)] + "…");
            }

            // ---- Ignorados: fora de tudo, sem estado; o clique só oferece "Deixar de ignorar" ----
            if (s.IgnoredFolders.Count + s.IgnoredDevices.Count > 0)
            {
                Section($"Ignorados · {s.IgnoredFolders.Count + s.IgnoredDevices.Count} (não monitorados)");
                foreach (var f in s.IgnoredFolders.OrderBy(f => f.Label, StringComparer.CurrentCultureIgnoreCase))
                {
                    Clickable(new(PanelItemKind.IgnoredFolder, f.Id));
                    Row(Level.Off, f.Label, (f.Missing ? f.Status.Text : "Pasta ignorada") + Menu, statusColor: _pal.Dim);
                }
                foreach (var d in s.IgnoredDevices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    Clickable(new(PanelItemKind.IgnoredDevice, d.Id));
                    Row(Level.Off, d.Name, (d.Missing ? d.Status.Text : "Dispositivo ignorado") + Menu, statusColor: _pal.Dim);
                }
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

    // ------------------------------------------------------------------ cabeçalho

    // linha 1: (estado) WSL · Syncthing ............ [versão / Atualizar para X] (ficha técnica) (pin)
    // linha 2:          status ou operação ....................... Verificado às HH:mm:ss (recarregar)
    enum HeaderButton { None, Version, Info, Pin, Status, Refresh }

    void DrawHeader(Graphics g)
    {
        var h = Header;
        int left = P(32);
        Dot(g, P(18), Line1 / 2 + P(2), P(10), _snap?.Overall ?? Level.Unknown);
        using (var bold = new Font("Segoe UI Semibold", 11f))
            TextRenderer.DrawText(g, PanelHeader.Title, bold, new Rectangle(left, P(4), HeaderRect(HeaderButton.Version).Left - left, Line1 - P(4)),
                _pal.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        // versão do app: discreta quando está em dia, botão de destaque quando há versão nova
        var vr = HeaderRect(HeaderButton.Version);
        if (h.VersionHighlight)
            using (var b = new SolidBrush(_headerHover == HeaderButton.Version ? ControlPaint.Light(_pal.Accent, 0.25f) : _pal.Accent))
                FillRound(g, b, vr, P(4));
        else if (_headerHover == HeaderButton.Version && h.VersionAction != HeaderAction.None)
            using (var b = new SolidBrush(_pal.Hover)) FillRound(g, b, vr, P(4));
        TextRenderer.DrawText(g, h.Version, Font, vr, h.VersionHighlight ? Color.White : _pal.Dim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        using (var icons = new Font("Segoe MDL2 Assets", 10f))
            foreach (var hb in HeaderIcons)
            {
                var r = HeaderRect(hb.Id);
                if (_headerHover == hb.Id)
                    using (var b = new SolidBrush(_pal.Hover)) FillRound(g, b, r, P(4));
                TextRenderer.DrawText(g, hb.Glyph, icons, r, hb.Active ? _pal.Accent : _pal.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

        TextRenderer.DrawText(g, h.Checked, Font, CheckedRect(h), _pal.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        TextRenderer.DrawText(g, h.Status, Font, HeaderRect(HeaderButton.Status),
            h.StatusLevel is Level.Busy or Level.Warn or Level.Error ? Theme.For(h.StatusLevel) : _pal.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    record struct HeaderIcon(HeaderButton Id, string Glyph, string Tip, bool Active);

    // glifos da Segoe MDL2 Assets
    IEnumerable<HeaderIcon> HeaderIcons =>
    [
        new(HeaderButton.Pin, _locked ? "\uE840" : "\uE718", _locked ? "Liberar o painel" : "Fixar o painel aberto", _locked),
        new(HeaderButton.Info, "\uE946", "Ficha técnica", false),
        new(HeaderButton.Refresh, "\uE72C", PanelHeader.RefreshTip, false),
    ];

    Rectangle HeaderRect(HeaderButton id)
    {
        int size = P(26), right = Width - P(10), y1 = (Line1 - size) / 2 + P(3), y2 = Line1 + (HeaderH - Line1 - size) / 2;
        var pin = new Rectangle(right - size, y1, size, size);
        var info = new Rectangle(pin.Left - P(2) - size, y1, size, size);
        switch (id)
        {
            case HeaderButton.Pin: return pin;
            case HeaderButton.Info: return info;
            case HeaderButton.Refresh: return new Rectangle(right - size, y2, size, size);
            case HeaderButton.Version:
                int w = TextRenderer.MeasureText(Header.Version, Font).Width + P(16);
                return new Rectangle(info.Left - P(6) - w, y1 + P(2), w, size - P(4));
            case HeaderButton.Status:
                int left = P(13), end = CheckedRect(Header).Left - P(8);
                return new Rectangle(left, y2, Math.Max(0, end - left), size);
            default: return Rectangle.Empty;
        }
    }

    Rectangle CheckedRect(PanelHeader h)
    {
        var refresh = HeaderRect(HeaderButton.Refresh);
        int w = h.Checked.Length == 0 ? 0 : TextRenderer.MeasureText(h.Checked, Font).Width;
        return new Rectangle(refresh.Left - P(4) - w, refresh.Top, w, refresh.Height);
    }

    HeaderButton HeaderButtonAt(Point p)
    {
        if (p.Y >= HeaderH) return HeaderButton.None;
        foreach (var id in new[] { HeaderButton.Pin, HeaderButton.Info, HeaderButton.Refresh, HeaderButton.Version, HeaderButton.Status })
            if (HeaderRect(id).Contains(p)) return id;
        return HeaderButton.None;
    }

    bool Clickable(HeaderButton id) => id switch
    {
        HeaderButton.None or HeaderButton.Status => false,
        HeaderButton.Version => Header.VersionAction != HeaderAction.None,
        _ => true,
    };

    /// <summary>Dica do item; o status só tem dica quando foi cortado.</summary>
    string? TipFor(HeaderButton id)
    {
        var h = Header;
        return id switch
        {
            HeaderButton.Version => h.VersionTip,
            HeaderButton.Status => TextRenderer.MeasureText(h.Status, Font).Width > HeaderRect(id).Width ? h.Status : null,
            HeaderButton.None => null,
            _ => HeaderIcons.First(b => b.Id == id).Tip,
        };
    }

    HeaderButton _headerHover;
    readonly ToolTip _tip = new();

    void RefreshAll()
    {
        _actions.RefreshNow();
        _actions.CheckForUpdate();
    }

    void ClickHeader(HeaderButton id)
    {
        switch (id)
        {
            case HeaderButton.Refresh: RefreshAll(); break;
            case HeaderButton.Info: _actions.ShowTechInfo(); break;
            case HeaderButton.Version:
                if (Header.VersionAction == HeaderAction.InstallUpdate) _actions.InstallUpdate();
                else if (Header.VersionAction == HeaderAction.CheckUpdate) _actions.CheckForUpdate();
                break;
            case HeaderButton.Pin:
                var s = PanelBehavior.ToggleLock(State);
                _clicked = s.Clicked;
                _locked = s.Locked;
                _tip.Show(_locked ? "Fixado: o painel fica aberto até ser liberado" : "Liberado", this,
                    HeaderRect(id).Left, HeaderRect(id).Bottom + P(4), 1500);
                Invalidate();
                break;
        }
    }

    PanelItem? ItemAt(Point p) =>
        InBody(p) && _itemRows.FirstOrDefault(r => r.Rect.Contains(p)) is { Rect.Width: > 0 } row ? row.Item : null;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int idx = _buttons.FindIndex(b => b.Rect.Contains(e.Location));
        var header = HeaderButtonAt(e.Location);
        if (header != _headerHover)
        {
            _headerHover = header;
            if (TipFor(header) is { } tip)
                _tip.Show(tip, this, HeaderRect(header).Left, HeaderRect(header).Bottom + P(4), 4000);
            else _tip.Hide(this);
            Invalidate();
        }
        Cursor = idx >= 0 || Clickable(header) || ItemAt(e.Location) != null ? Cursors.Hand : Cursors.Default;
        if (idx != _hoverButton) { _hoverButton = idx; Invalidate(); }
        base.OnMouseMove(e);
    }

    bool InBody(Point p) => p.Y > HeaderH && p.Y < HeaderH + _bodyViewport;

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverButton != -1 || _headerHover != HeaderButton.None)
        {
            _hoverButton = -1; _headerHover = HeaderButton.None; _tip.Hide(this); Invalidate();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (HeaderButtonAt(e.Location) is var header && Clickable(header)) ClickHeader(header);
            else if (_buttons.FirstOrDefault(b => b.Rect.Contains(e.Location)) is { Click: not null } button) button.Click();
            else if (ItemAt(e.Location) is { } item) _actions.ShowItemMenu(item, PointToScreen(e.Location));
        }
        base.OnMouseUp(e);
    }

    // ------------------------------------------------------------------ helpers

    static void Dot(Graphics g, int cx, int cy, int d, Level l) => Theme.Dot(g, cx - d / 2f, cy - d / 2f, d, l);

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
