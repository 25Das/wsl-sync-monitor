using System.Runtime.InteropServices;

namespace WslSyncMonitor;

/// <summary>
/// Avisos da barra de tarefas ao widget, pelos mecanismos que a própria barra usa, sem consultas:
/// <list type="bullet">
/// <item>janela com botão na barra abriu, fechou ou foi trocada (shell hook: só essas janelas, inclusive apps UWP;
/// dicas e menus não contam), e janela ocultada ou mostrada pela troca de área de trabalho virtual (cloak);</item>
/// <item>barra mudou de lugar, de tamanho ou de modo de ocultar, ou um app entrou/saiu de tela cheia (registrado como
/// "appbar", sem reservar espaço: só ABM_SETPOS reservaria);</item>
/// <item>Explorer reiniciou e a barra foi recriada (TaskbarCreated).</item>
/// </list>
/// Janela invisível de nível superior (os avisos não chegam a janelas filhas); os avisos vêm no thread que a criou.
/// </summary>
public sealed class TaskbarNotifications : NativeWindow, IDisposable
{
    const int HSHELL_WINDOWCREATED = 1, HSHELL_WINDOWDESTROYED = 2, HSHELL_WINDOWREPLACED = 13;
    const uint ABM_NEW = 0, ABM_REMOVE = 1;
    const int ABN_STATECHANGE = 0, ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
    const uint EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018, MSGFLT_ALLOW = 1;

    /// <summary>Mensagem com que a barra avisa este "appbar" (WM_APP + 0x57).</summary>
    public const int AppBarMessage = 0x8000 + 0x57;
    static readonly int ShellHookMessage = Native.RegisterWindowMessage("SHELLHOOK");
    static readonly int TaskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, int message, uint action, IntPtr changeInfo);

    readonly Native.WinEventProc _cloakProc;
    readonly IntPtr _cloakHook;

    /// <summary>Uma janela com botão na barra abriu, fechou, foi trocada, ocultada ou mostrada.</summary>
    public event Action<IntPtr>? ButtonsChanged;
    /// <summary>A barra mudou de lugar, de tamanho ou de modo de ocultar, ou um app entrou/saiu de tela cheia.</summary>
    public event Action? TaskbarChanged;
    /// <summary>O Explorer reiniciou: a barra é outra.</summary>
    public event Action? TaskbarCreated;

    public TaskbarNotifications()
    {
        CreateHandle(new CreateParams { Caption = "WSL Sync Monitor Taskbar", ExStyle = Native.WS_EX_TOOLWINDOW });
        // com o app como administrador, o Windows bloquearia avisos do Explorer (de nível menor) sem esta permissão
        foreach (var m in new[] { ShellHookMessage, TaskbarCreatedMessage, AppBarMessage })
            ChangeWindowMessageFilterEx(Handle, m, MSGFLT_ALLOW, IntPtr.Zero);
        if (!Native.RegisterShellHookWindow(Handle)) Log.Info("widget", "aviso de janelas da barra indisponível; os ícones são relidos a cada 30 s");
        RegisterAppBar();
        _cloakProc = OnCloak;
        _cloakHook = Native.SetWinEventHook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, _cloakProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    Native.APPBARDATA AppBar => new() { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = Handle, uCallbackMessage = (uint)AppBarMessage };

    /// <summary>Registrado como appbar: chegam os avisos de tela cheia, posição e modo de ocultar da barra.</summary>
    public bool AppBarRegistered { get; private set; }

    void RegisterAppBar()
    {
        var data = AppBar;
        AppBarRegistered = Native.SHAppBarMessage(ABM_NEW, ref data) != IntPtr.Zero;
        if (!AppBarRegistered) Log.Info("widget", "avisos de tela cheia indisponíveis; conferidos a cada troca de janela em primeiro plano");
    }

    void UnregisterAppBar()
    {
        var data = AppBar;
        Native.SHAppBarMessage(ABM_REMOVE, ref data);
    }

    void OnCloak(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // só janelas com botão na barra (não Iniciar, Pesquisa, dicas): a janela ocultada continua existindo, dá para ver o estilo
        if (idObject == Native.OBJID_WINDOW && idChild == Native.CHILDID_SELF && hwnd != IntPtr.Zero &&
            WidgetPlacement.HasTaskbarButton(topLevel: Native.GetAncestor(hwnd, Native.GA_ROOT) == hwnd,
                owned: Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero, exStyle: Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE)))
            ButtonsChanged?.Invoke(hwnd);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == ShellHookMessage)
        {
            if ((int)m.WParam is HSHELL_WINDOWCREATED or HSHELL_WINDOWDESTROYED or HSHELL_WINDOWREPLACED) ButtonsChanged?.Invoke(m.LParam);
        }
        else if (m.Msg == AppBarMessage)
        {
            if ((int)m.WParam is ABN_STATECHANGE or ABN_POSCHANGED or ABN_FULLSCREENAPP) TaskbarChanged?.Invoke();
        }
        else if (m.Msg == TaskbarCreatedMessage)
        {
            // a barra nova não conhece este appbar: registra de novo
            UnregisterAppBar();
            RegisterAppBar();
            TaskbarCreated?.Invoke();
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        if (_cloakHook != IntPtr.Zero) Native.UnhookWinEvent(_cloakHook);
        UnregisterAppBar();
        Native.DeregisterShellHookWindow(Handle);
        DestroyHandle();
    }
}
