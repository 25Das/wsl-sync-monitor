namespace WslSyncMonitor;

/// <summary>
/// Avisa quando uma janela com botão na barra de tarefas abre, fecha ou é trocada: é o mesmo aviso que a própria
/// barra usa para criar e tirar botões (RegisterShellHookWindow), então só chega para essas janelas (inclusive apps
/// UWP) e não para dicas, menus e miniaturas. Janela invisível de nível superior (o aviso não chega a janelas filhas);
/// o aviso vem no thread que a criou.
/// </summary>
public sealed class ShellHookWindow : NativeWindow, IDisposable
{
    const int HSHELL_WINDOWCREATED = 1, HSHELL_WINDOWDESTROYED = 2, HSHELL_WINDOWREPLACED = 13;
    static readonly int ShellHookMessage = Native.RegisterWindowMessage("SHELLHOOK");
    readonly Action<IntPtr> _changed;

    /// <param name="changed">recebe a janela que abriu, fechou ou foi trocada</param>
    public ShellHookWindow(Action<IntPtr> changed)
    {
        _changed = changed;
        CreateHandle(new CreateParams { Caption = "WSL Sync Monitor ShellHook", ExStyle = Native.WS_EX_TOOLWINDOW });
        if (!Native.RegisterShellHookWindow(Handle)) Log.Info("widget", "aviso de janelas da barra indisponível; os ícones são relidos a cada 30 s");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == ShellHookMessage && (int)m.WParam is HSHELL_WINDOWCREATED or HSHELL_WINDOWDESTROYED or HSHELL_WINDOWREPLACED)
            _changed(m.LParam);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        Native.DeregisterShellHookWindow(Handle);
        DestroyHandle();
    }
}
