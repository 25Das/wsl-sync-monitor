namespace WslSyncMonitor;

/// <summary>
/// Janela do widget na barra de tarefas, ao lado da área de notificação (o desenho fica em <see cref="WidgetPainter"/>).
/// Arraste com o botão esquerdo para reposicionar. Roda no thread próprio do <see cref="WidgetHost"/>.
/// <para>
/// Por padrão é uma janela nativa filha da própria barra (<see cref="Settings.WidgetInsideTaskbar"/>), criada
/// diretamente dentro dela: aparece junto com a barra mesmo quando o menu Iniciar do Windows 11 a põe numa camada
/// acima das janelas "sempre visíveis", desliza junto no auto-ocultar e o mouse sobre ele conta como mouse sobre a
/// barra. No modo antigo é uma janela "sempre visível" por cima da barra, que acompanha a posição dela.
/// </para>
/// <para>
/// O estado de "visível" é o da própria janela do Windows (não há um segundo estado a sincronizar). Se a barra for
/// recriada (o Explorer reiniciou), a janela filha é destruída junto e o widget cria outra na barra nova.
/// </para>
/// </summary>
public sealed class TaskbarWidget : NativeWindow, IDisposable
{
    public const string Title = "WSL Sync Monitor Widget";

    readonly Settings _settings;
    readonly WidgetPainter _painter = new();
    readonly System.Windows.Forms.Timer _follow = new() { Interval = 500 };
    TaskbarInfo _tb = new();
    IntPtr _parent;         // a barra, no modo dentro dela; zero no modo por cima
    volatile IntPtr _hwnd;  // lido de outros threads (WidgetHost)
    Rectangle _placed;      // última posição (relativa à barra, dentro dela) e tamanho aplicados
    bool _hover, _dirty = true, _disposed;

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
        _follow.Tick += (_, _) => Follow();
        _follow.Start();
        Follow();
    }

    public void SetSnapshot(Snapshot s) { _painter.Snapshot = s; _dirty = true; Follow(); }

    public bool Busy
    {
        set { if (_painter.Busy == value) return; _painter.Busy = value; _dirty = true; Follow(); }
    }

    /// <summary>Retângulo do widget na tela; vazio sem janela (pode ser lido de outro thread).</summary>
    public Rectangle ScreenBounds =>
        _hwnd != IntPtr.Zero && Native.GetWindowRect(_hwnd, out var r) ? r.ToRectangle() : Rectangle.Empty;

    /// <summary>A janela do widget está à mostra (pode ser lido de outro thread).</summary>
    public bool IsShown => _hwnd != IntPtr.Zero && Native.IsWindowVisible(_hwnd);

    /// <summary>Centro do widget na tela: âncora do painel detalhado.</summary>
    public Point PopupAnchor
    {
        get
        {
            var r = ScreenBounds;
            return new(r.Left + r.Width / 2, r.Top + r.Height / 2);
        }
    }

    bool Inside => _parent != IntPtr.Zero;
    float S => _tb.Dpi / 96f;
    int P(float v) => (int)Math.Round(v * S);

    // ------------------------------------------------------------------ modo por cima da barra

    /// <summary>Enquanto devolver true (um menu do app aberto), o widget não se recoloca acima dele.</summary>
    public Func<bool>? YieldTo { get; set; }

    /// <summary>Modo por cima da barra: recoloca o widget acima dela (a barra sobe na ordem Z ao ganhar o foco).</summary>
    public void BringAboveTaskbar()
    {
        if (IsShown && !Inside && !(YieldTo?.Invoke() ?? false))
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // reage na hora a qualquer troca de janela em primeiro plano, para o widget não ficar atrás da barra
    Native.WinEventProc? _fgProc;
    IntPtr _fgHook;

    // ------------------------------------------------------------------ janela

    /// <summary>Cria a janela no modo pedido; no modo dentro da barra, já como filha dela.</summary>
    void Create(TaskbarInfo tb)
    {
        _parent = _settings.WidgetInsideTaskbar ? tb.Hwnd : IntPtr.Zero;
        CreateHandle(new CreateParams
        {
            Caption = Title,
            Parent = _parent,
            Style = Inside ? Native.WS_CHILD | Native.WS_CLIPSIBLINGS : Native.WS_POPUP,
            ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | (Inside ? 0 : Native.WS_EX_TOPMOST),
        });
        _hwnd = Handle;
        _placed = Rectangle.Empty;
        _dirty = true;
        if (!Inside)
        {
            _fgProc = (_, _, _, _, _, _, _) => BringAboveTaskbar();
            _fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _fgProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        }
        Log.Info("widget", Inside ? "criado dentro da barra de tarefas" : "criado por cima da barra de tarefas");
    }

    void Destroy()
    {
        if (_fgHook != IntPtr.Zero) { Native.UnhookWinEvent(_fgHook); _fgHook = IntPtr.Zero; }
        if (Handle != IntPtr.Zero) DestroyHandle();
        _hwnd = IntPtr.Zero;
    }

    /// <summary>A janela não existe mais (a barra foi destruída com ela) ou está no modo/barra errados.</summary>
    bool NeedsRecreate(TaskbarInfo tb) =>
        Handle == IntPtr.Zero || !Native.IsWindow(Handle) ||
        _settings.WidgetInsideTaskbar != Inside || (Inside && tb.Hwnd != _parent);

    // ------------------------------------------------------------------ acompanhar a barra

    static readonly DateTime AppStart = System.Diagnostics.Process.GetCurrentProcess().StartTime;
    bool _everShown;

    /// <summary>Confere a barra e ajusta o widget (a cada meio segundo e quando algo muda).</summary>
    internal void Follow()
    {
        if (_disposed) return;
        var tb = TaskbarInfo.Get();
        _tb = tb;
        if (tb.Hwnd == IntPtr.Zero) return; // barra ainda não existe (Windows iniciando, Explorer reiniciando)
        if (NeedsRecreate(tb)) { Destroy(); Create(tb); }

        // por cima da barra auto-ocultável, o widget acompanha a animação dela; dentro, desliza junto sozinho
        _follow.Interval = tb.AutoHide && !Inside ? 150 : 500;
        bool show = _settings.ShowTaskbarWidget && tb.Thickness > 0 && (Inside || (tb.FullyShown && !tb.FullscreenApp));
        if (!show)
        {
            SetShown(false, tb);
            return;
        }

        var size = _painter.Measure(tb);
        var bounds = new Rectangle(Place(tb, size), size);
        if (Inside) bounds.Offset(-tb.Rect.Left, -tb.Rect.Top); // janela filha: posição relativa à barra
        if (bounds != _placed || _dirty)
        {
            if (Inside) Native.SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            _placed = bounds;
            Render();
        }
        SetShown(true, tb);
        BringAboveTaskbar();
    }

    /// <summary>Mostra ou esconde a janela, registrando no log só as mudanças.</summary>
    void SetShown(bool shown, TaskbarInfo tb)
    {
        if (Handle == IntPtr.Zero || Native.IsWindowVisible(Handle) == shown) return;
        Native.ShowWindow(Handle, shown ? Native.SW_SHOWNA : Native.SW_HIDE);
        if (shown)
            Log.Info("widget", _everShown ? "exibido" : $"exibido {(DateTime.Now - AppStart).TotalSeconds:0.0} s após o início do app");
        else if (_settings.ShowTaskbarWidget)
            Log.Info("widget", "oculto: " + (tb.FullscreenApp
                ? $"o Windows informa app em tela cheia ou ocupado (estado {tb.NotificationState})"
                : "a barra de tarefas está oculta ou fora da tela"));
        _everShown |= shown;
    }

    /// <summary>Posição do widget na tela, junto da bandeja (ou do outro lado, sem espaço; ver WidgetPlacement).</summary>
    Point Place(TaskbarInfo tb, Size size)
    {
        if (tb.Horizontal)
        {
            int trayLeft = tb.NotifyRect is { } n && n.Left > tb.Rect.Left ? n.Left : tb.Rect.Right - P(220);
            RefreshTaskbarIcons(tb);
            // enquanto o usuário arrasta, segue o mouse
            int x = WidgetPlacement.ChooseX(tb.Rect.Left, trayLeft, _dragging ? [] : _taskbarIcons ?? [],
                size.Width, P(6), _settings.WidgetOffset);
            return new Point(x, tb.Rect.Top + (tb.Rect.Height - size.Height) / 2);
        }
        int trayTop = tb.NotifyRect is { } nv && nv.Top > tb.Rect.Top ? nv.Top : tb.Rect.Bottom - P(220);
        int y = Math.Clamp(trayTop - size.Height - P(6) - _settings.WidgetOffset, tb.Rect.Top, tb.Rect.Bottom - size.Height);
        return new Point(tb.Rect.Left + (tb.Rect.Width - size.Width) / 2, y);
    }

    void Render()
    {
        if (Handle == IntPtr.Zero || _placed.Width <= 0 || _placed.Height <= 0) return;
        using var bmp = _painter.Render(_tb, _placed.Size, _hover);
        // dentro da barra a posição já foi aplicada (e é relativa a ela); por cima, vai junto com o desenho
        Native.SetLayeredBitmap(Handle, bmp, Inside ? null : _placed.Location);
        _dirty = false;
    }

    // ícones da barra (Iniciar, apps, clima…), lidos pela UIA num thread de fundo a cada poucos segundos
    volatile List<(int Start, int End)>? _taskbarIcons;
    string? _iconsDesc;
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
            var desc = t.Result == null ? "não lidos" : string.Join(" ", t.Result.Select(i => $"{i.Start}–{i.End}"));
            if (desc != _iconsDesc) { _iconsDesc = desc; Log.Info("widget", "ícones da barra: " + desc); }
            _taskbarIcons = t.Result;
            _readingIcons = false;
        }, TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ mouse

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_MOUSEACTIVATE:
                m.Result = (IntPtr)Native.MA_NOACTIVATE; // clicar não tira o foco de quem tem
                return;
            case Native.WM_MOUSEMOVE:
                if (!_hover)
                {
                    // realce na hora, e pede o aviso de quando o mouse sair
                    _hover = true;
                    Native.TrackMouseLeave(Handle);
                    Render();
                    HoverStarted?.Invoke();
                }
                if (_down) Drag();
                break;
            case Native.WM_MOUSELEAVE:
                _hover = false;
                Render();
                break;
            case Native.WM_LBUTTONDOWN:
                _down = true;
                _dragging = false;
                _downPt = Cursor.Position;
                _downOffset = _settings.WidgetOffset;
                Native.SetCapture(Handle);
                break;
            case Native.WM_LBUTTONUP:
                if (_down)
                {
                    bool dragged = _dragging;
                    EndDrag();
                    if (dragged) OffsetChanged?.Invoke();
                    else Clicked?.Invoke();
                }
                break;
            case Native.WM_CAPTURECHANGED:
                if (_down && m.LParam != Handle) EndDrag(); // perdeu o mouse no meio do arrasto
                break;
            case Native.WM_RBUTTONUP:
                RightClicked?.Invoke();
                return; // sem o tratamento padrão, que geraria o pedido de menu de contexto
            case Native.WM_CONTEXTMENU:
                return; // o tratamento padrão repassaria à barra (a janela pai), que abriria o menu dela
        }
        base.WndProc(ref m);
    }

    void Drag()
    {
        var cur = Cursor.Position;
        int delta = _tb.Horizontal ? _downPt.X - cur.X : _downPt.Y - cur.Y;
        if (!_dragging && Math.Abs(delta) > P(4)) _dragging = true;
        if (!_dragging) return;
        _settings.WidgetOffset = Math.Max(-P(400), _downOffset + delta);
        Follow();
    }

    void EndDrag()
    {
        _down = false;
        _dragging = false;
        Native.ReleaseCapture();
    }

    public bool IsDisposed => _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _follow.Dispose();
        Destroy();
    }
}
