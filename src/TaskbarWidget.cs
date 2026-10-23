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
    readonly System.Windows.Forms.Timer _follow = new() { Interval = 2000 };  // rede de segurança (ver Hooks)
    readonly System.Windows.Forms.Timer _followSoon = new() { Interval = 16 }; // junta uma rajada de avisos numa conferência
    readonly System.Windows.Forms.Timer _iconsSoon = new() { Interval = (int)WidgetPlacement.IconsSettle.TotalMilliseconds };
    TaskbarInfo _tb = new();
    IntPtr _parent;         // a barra, no modo dentro dela; zero no modo por cima
    volatile IntPtr _hwnd;  // lido de outros threads (WidgetHost)
    Rectangle _placed;      // última posição (relativa à barra, dentro dela) e tamanho aplicados
    bool _hover, _dirty = true, _disposed, _light;
    string? _hiddenReason;  // último motivo de estar oculto registrado no log

    // arrastar
    bool _down, _dragging;
    Point _downPt;
    int _downOffset;

    public event Action? Clicked;
    public event Action? RightClicked;
    public event Action? HoverStarted;
    public event Action? HoverEnded;
    public event Action? OffsetChanged;

    public TaskbarWidget(Settings settings)
    {
        _settings = settings;
        _follow.Tick += (_, _) => Follow();
        _followSoon.Tick += (_, _) => { _followSoon.Stop(); Follow(); };
        _iconsSoon.Tick += (_, _) =>
        {
            _iconsSoon.Stop();
            Follow();
            // ainda não deu para reler (leitura em andamento ou a última foi há pouco): tenta de novo em seguida
            if (_windowsChangedAt > _iconsReadAt && IsShown) _iconsSoon.Start();
        };
    }

    /// <summary>Cria a janela e passa a acompanhar a barra (chamar depois de ligar os eventos e o YieldTo).</summary>
    public void Start()
    {
        _follow.Start();
        StartHooks();
        Follow();
    }

    // ------------------------------------------------------------------ avisos do Windows

    // Em vez de conferir a barra a cada meio segundo, o widget reage aos avisos do Windows: a barra (ou algo dentro
    // dela, como a área de notificação) mudou de lugar ou tamanho; uma janela abriu, fechou ou foi para o primeiro
    // plano (os botões da barra mudam e um app pode ter entrado em tela cheia); o tema ou os monitores mudaram.
    // O timer _follow continua como rede de segurança, bem mais espaçado (WidgetPlacement.FollowIntervalMs).
    Native.WinEventProc? _hookProc;
    IntPtr _windowsHook, _foregroundHook, _taskbarHook;
    uint _taskbarPid;

    void StartHooks()
    {
        _hookProc = OnWinEvent;
        const uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
        _windowsHook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_HIDE, IntPtr.Zero, _hookProc, 0, 0, flags);
        _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookProc, 0, 0, flags);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemChange; // tema claro/escuro, barra
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnSystemChange; // monitores, resolução, escala
    }

    /// <summary>Avisos de mudança de lugar só do processo da barra (o Explorer); refeito quando ele reinicia.</summary>
    void HookTaskbar(IntPtr taskbar)
    {
        uint pid = Native.GetWindowThreadProcessId(taskbar, out var p) != 0 ? p : 0;
        if (pid == _taskbarPid) return;
        if (_taskbarHook != IntPtr.Zero) Native.UnhookWinEvent(_taskbarHook);
        _taskbarPid = pid;
        _taskbarHook = pid == 0 ? IntPtr.Zero : Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE,
            Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _hookProc!, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    void StopHooks()
    {
        foreach (var h in new[] { _windowsHook, _foregroundHook, _taskbarHook })
            if (h != IntPtr.Zero) Native.UnhookWinEvent(h);
        _windowsHook = _foregroundHook = _taskbarHook = IntPtr.Zero;
        _taskbarPid = 0;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemChange;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnSystemChange;
    }

    void OnSystemChange(object? sender, EventArgs e) => FollowSoon();

    /// <summary>Chega no thread do widget (avisos "fora de contexto" vêm pela fila de mensagens de quem pediu).</summary>
    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed || idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF || hwnd == IntPtr.Zero) return;
        if (evt == Native.EVENT_OBJECT_LOCATIONCHANGE)
        {
            // a barra ou algo dentro dela (área de notificação, botões) mudou de lugar ou tamanho
            if (hwnd != _hwnd && (hwnd == _tb.Hwnd || Native.GetAncestor(hwnd, Native.GA_ROOT) == _tb.Hwnd)) FollowSoon();
            return;
        }
        if (evt == Native.EVENT_SYSTEM_FOREGROUND) { FollowSoon(); return; } // tela cheia começa ou termina
        // janela com botão na barra apareceu ou sumiu (ao fechar, ela some antes de ser destruída): os botões da barra
        // mudam. Relê depois da animação, contando da primeira mudança ainda não lida (WidgetPlacement.IconsReadDue).
        // (o aviso chega depois: se a janela já foi destruída, não dá para ver o estilo; conta como mudança)
        bool button = !Native.IsWindow(hwnd) || WidgetPlacement.HasTaskbarButton(topLevel: Native.GetAncestor(hwnd, Native.GA_ROOT) == hwnd,
            owned: Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero, exStyle: Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE));
        if (!button) return;
        if (_windowsChangedAt is not { } pending || pending <= _iconsReadAt) _windowsChangedAt = DateTime.Now;
        if (!_iconsSoon.Enabled) _iconsSoon.Start();
    }

    /// <summary>Confere a barra em instantes (uma vez por rajada de avisos).</summary>
    void FollowSoon()
    {
        if (!_followSoon.Enabled && !_disposed) _followSoon.Start();
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
        _placed = Rectangle.Empty;
        _dirty = true;
        // a janela nova começa sem mouse em cima e sem arrasto
        _hover = _down = _dragging = false;
        Log.Info("widget", Inside ? "criado dentro da barra de tarefas" : "criado por cima da barra de tarefas");
    }

    bool _destroying;

    void Destroy()
    {
        WatchForeground(false);
        _destroying = true;
        try { if (Handle != IntPtr.Zero) DestroyHandle(); }
        finally { _destroying = false; }
    }

    /// <summary>A janela nativa mudou (criada, destruída por nós ou junto com a barra): atualiza a cópia lida por outros threads.</summary>
    protected override void OnHandleChange() => _hwnd = Handle;

    /// <summary>Modo por cima da barra, visível: recoloca-se acima dela a cada troca de janela em primeiro plano.</summary>
    void WatchForeground(bool on)
    {
        if (on == (_fgHook != IntPtr.Zero)) return;
        if (on)
        {
            _fgProc = (_, _, _, _, _, _, _) => BringAboveTaskbar();
            _fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _fgProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        }
        else
        {
            Native.UnhookWinEvent(_fgHook);
            _fgHook = IntPtr.Zero;
        }
    }

    /// <summary>A janela não existe mais (a barra foi destruída com ela) ou está no modo/barra errados.</summary>
    bool NeedsRecreate(TaskbarInfo tb) =>
        Handle == IntPtr.Zero || !Native.IsWindow(Handle) ||
        _settings.WidgetInsideTaskbar != Inside || (Inside && tb.Hwnd != _parent);

    // ------------------------------------------------------------------ acompanhar a barra

    static readonly DateTime AppStart = System.Diagnostics.Process.GetCurrentProcess().StartTime;
    bool _everShown;

    /// <summary>Confere a barra e ajusta o widget (quando o Windows avisa que algo mudou e, por segurança, de tempos em tempos).</summary>
    internal void Follow()
    {
        if (_disposed) return;
        var tb = TaskbarInfo.Get();
        _tb = tb;
        bool taskbar = tb.Hwnd != IntPtr.Zero; // sem barra: Windows iniciando ou Explorer reiniciando
        if (taskbar && NeedsRecreate(tb)) { Destroy(); Create(tb); }
        if (taskbar) HookTaskbar(tb.Hwnd);

        int interval = WidgetPlacement.FollowIntervalMs(tb.AutoHide, Inside, taskbar);
        if (_follow.Interval != interval) _follow.Interval = interval; // trocar reinicia a contagem: só quando muda
        var (show, reason) = WidgetPlacement.Visibility(_settings.ShowTaskbarWidget, _settings.WidgetInsideTaskbar,
            taskbar, tb.Thickness, tb.FullyShown, tb.FullscreenApp, tb.NotificationState);
        if (!show)
        {
            SetShown(false);
            if (reason != null && reason != _hiddenReason) Log.Info("widget", "oculto: " + reason);
            _hiddenReason = reason;
            return;
        }

        if (Theme.TaskbarLight != _light) { _light = Theme.TaskbarLight; _dirty = true; } // tema mudou: redesenha
        var size = _painter.Measure(tb);
        var bounds = new Rectangle(Place(tb, size), size);
        if (Inside)
        {
            // posição relativa à barra e no topo entre as janelas filhas dela: a barra do Windows 11 é desenhada
            // por uma janela filha (a ponte XAML), que de outro modo cobriria o widget
            bounds.Offset(-tb.Rect.Left, -tb.Rect.Top);
            if (bounds != _placed || Native.GetWindow(Handle, Native.GW_HWNDPREV) != IntPtr.Zero)
                Native.SetWindowPos(Handle, Native.HWND_TOP, bounds.X, bounds.Y, bounds.Width, bounds.Height, Native.SWP_NOACTIVATE);
        }
        if (bounds != _placed || _dirty)
        {
            _placed = bounds;
            Render();
        }
        SetShown(true);
        BringAboveTaskbar();
    }

    /// <summary>Mostra ou esconde a janela (o motivo de esconder é registrado por quem chama).</summary>
    void SetShown(bool shown)
    {
        WatchForeground(shown && !Inside);
        if (Handle == IntPtr.Zero || Native.IsWindowVisible(Handle) == shown) return;
        Native.ShowWindow(Handle, shown ? Native.SW_SHOWNA : Native.SW_HIDE);
        if (!shown) return;
        Log.Info("widget", _everShown ? "exibido" : $"exibido {(DateTime.Now - AppStart).TotalSeconds:0.0} s após o início do app");
        _everShown = true;
        _hiddenReason = null;
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

    // ícones da barra (Iniciar, apps, clima…), lidos pela UIA num thread de fundo quando janelas abrem ou fecham
    // (os botões da barra mudam) e, sem aviso, de tempos em tempos (WidgetPlacement.IconsReadDue)
    volatile List<(int Start, int End)>? _taskbarIcons;
    string? _iconsDesc;
    DateTime _iconsReadAt;
    DateTime? _windowsChangedAt;
    bool _readingIcons;

    void RefreshTaskbarIcons(TaskbarInfo tb)
    {
        if (_readingIcons || !WidgetPlacement.IconsReadDue(DateTime.Now, _iconsReadAt, _windowsChangedAt)) return;
        _readingIcons = true;
        _iconsReadAt = DateTime.Now;
        var ui = SynchronizationContext.Current;
        var (hwnd, notify, left, cornerWidth) = (tb.Hwnd, tb.NotifyRect, tb.Rect.Left, P(200));
        Task.Run(() => TaskbarIcons.Read(hwnd, notify, left, cornerWidth)).ContinueWith(t =>
        {
            var desc = t.Result == null ? "não lidos" : string.Join(" ", t.Result.Select(i => $"{i.Start}–{i.End}"));
            bool changed = desc != _iconsDesc;
            if (changed) { _iconsDesc = desc; Log.Info("widget", "ícones da barra: " + desc); }
            _taskbarIcons = t.Result;
            _readingIcons = false;
            if (changed) ui?.Post(_ => FollowSoon(), null); // recoloca já com os ícones novos
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
                HoverEnded?.Invoke();
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
            case Native.WM_NCDESTROY:
                if (!_destroying) FollowSoon(); // destruída junto com a barra (o Explorer reiniciou): recria assim que ela voltar
                break;
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
        StopHooks();
        _follow.Dispose();
        _followSoon.Dispose();
        _iconsSoon.Dispose();
        Destroy();
    }
}
