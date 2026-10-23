using System.Runtime.InteropServices;
using WslSyncMonitor;

namespace WslSyncMonitor.WindowsTests;

/// <summary>
/// A janela nativa do widget na barra de tarefas do runner. Mexe no cursor (arrasto): roda em série com os testes
/// de ponta a ponta.
/// </summary>
[Collection("E2E")]
public sealed class TaskbarWidgetTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205;
    const int GWL_EXSTYLE = -20, WS_EX_TOPMOST = 0x8;

    static IntPtr Taskbar => FindWindow("Shell_TrayWnd", null);

    static IntPtr WidgetWindow(TaskbarWidget w) => w.Handle;

    [Fact]
    public void Nasce_como_janela_filha_da_barra_e_visivel()
    {
        using var w = new TaskbarWidget(new Settings());
        var h = WidgetWindow(w);
        Assert.Equal(Taskbar, GetParent(h));
        Assert.True(IsWindowVisible(h));
        Assert.True(w.IsShown);
        Assert.False(w.ScreenBounds.IsEmpty);
    }

    [Fact]
    public void No_modo_antigo_e_uma_janela_sempre_visivel_por_cima_da_barra()
    {
        using var w = new TaskbarWidget(new Settings { WidgetInsideTaskbar = false });
        var h = WidgetWindow(w);
        Assert.Equal(h, GetAncestor(h, 2 /* GA_ROOT */)); // janela de nível superior
        Assert.NotEqual(0, GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST);
    }

    [Fact]
    public void Trocar_de_modo_recria_a_janela_no_modo_novo()
    {
        var settings = new Settings();
        using var w = new TaskbarWidget(settings);
        var inside = WidgetWindow(w);

        settings.WidgetInsideTaskbar = false;
        w.Follow();

        Assert.False(IsWindow(inside));
        Assert.NotEqual(Taskbar, GetParent(WidgetWindow(w)));
    }

    [Fact]
    public void Desligado_nas_preferencias_fica_oculto()
    {
        var settings = new Settings();
        using var w = new TaskbarWidget(settings);
        settings.ShowTaskbarWidget = false;
        w.Follow();
        Assert.False(w.IsShown);
    }

    [Fact]
    public void Passar_o_mouse_avisa_na_hora()
    {
        using var w = new TaskbarWidget(new Settings());
        int hovers = 0;
        w.HoverStarted += () => hovers++;
        SendMessage(WidgetWindow(w), WM_MOUSEMOVE, IntPtr.Zero, IntPtr.Zero);
        SendMessage(WidgetWindow(w), WM_MOUSEMOVE, IntPtr.Zero, IntPtr.Zero); // continua dentro: não repete
        Assert.Equal(1, hovers);
    }

    [Fact]
    public void Clique_e_clique_direito()
    {
        using var w = new TaskbarWidget(new Settings());
        int clicks = 0, right = 0;
        w.Clicked += () => clicks++;
        w.RightClicked += () => right++;
        var h = WidgetWindow(w);
        var c = w.PopupAnchor;
        SetCursorPos(c.X, c.Y);

        SendMessage(h, WM_LBUTTONDOWN, IntPtr.Zero, IntPtr.Zero);
        SendMessage(h, WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);
        SendMessage(h, WM_RBUTTONUP, IntPtr.Zero, IntPtr.Zero);

        Assert.Equal((1, 1), (clicks, right));
    }

    [Fact]
    public void Arrastar_muda_o_deslocamento_e_nao_conta_como_clique()
    {
        var settings = new Settings();
        using var w = new TaskbarWidget(settings);
        int clicks = 0, moved = 0;
        w.Clicked += () => clicks++;
        w.OffsetChanged += () => moved++;
        var h = WidgetWindow(w);
        var c = w.PopupAnchor;

        SetCursorPos(c.X, c.Y);
        SendMessage(h, WM_LBUTTONDOWN, IntPtr.Zero, IntPtr.Zero);
        SetCursorPos(c.X - 40, c.Y); // para a esquerda: longe da bandeja
        SendMessage(h, WM_MOUSEMOVE, IntPtr.Zero, IntPtr.Zero);
        SendMessage(h, WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);

        Assert.Equal((0, 1), (clicks, moved));
        Assert.Equal(40, settings.WidgetOffset);
    }

    [Fact]
    public void Descartar_destroi_a_janela()
    {
        var w = new TaskbarWidget(new Settings());
        var h = WidgetWindow(w);
        w.Dispose();
        Assert.False(IsWindow(h));
        Assert.False(w.IsShown);
    }

    [Fact]
    public void O_host_roda_o_widget_num_thread_proprio()
    {
        // o widget dentro da barra liga a fila de entrada do thread dele à do Explorer: não pode ser o da interface
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var host = new WidgetHost(new Settings());
        IntPtr h = IntPtr.Zero;
        for (int i = 0; i < 50 && h == IntPtr.Zero; i++)
        {
            h = FindWindowEx(Taskbar, IntPtr.Zero, null, TaskbarWidget.Title);
            if (h == IntPtr.Zero) Thread.Sleep(100);
        }
        Assert.NotEqual(IntPtr.Zero, h);
        Assert.NotEqual(GetCurrentThreadId(), GetWindowThreadProcessId(h, out _));
        Assert.True(host.Visible);
    }
}
