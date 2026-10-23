using System.Runtime.InteropServices;
using WslSyncMonitor;

namespace WslSyncMonitor.WindowsTests;

/// <summary>
/// O que a barra de tarefas avisa ao widget, pelos mecanismos que ela mesma usa: janelas com botão nela (shell hook),
/// tela cheia e mudanças da barra (appbar), Explorer reiniciado (TaskbarCreated) e janelas ocultadas por área de
/// trabalho virtual (cloak). O aviso vale para o sistema todo; os testes contam só as próprias janelas.
/// </summary>
[Collection("E2E")]
public sealed class TaskbarNotificationsTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static void Pump(Func<bool> done, TimeSpan? max = null)
    {
        var end = DateTime.Now + (max ?? TimeSpan.FromSeconds(5));
        while (!done() && DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(20); }
    }

    [Fact]
    public void Avisa_quando_uma_janela_com_botao_na_barra_abre_e_fecha()
    {
        var seen = new List<IntPtr>();
        using var n = new TaskbarNotifications();
        n.ButtonsChanged += seen.Add;
        using var app = new Form { ShowInTaskbar = true, Text = "janela de teste", StartPosition = FormStartPosition.Manual, Location = new(-2000, -2000) };
        app.Show();
        var h = app.Handle;
        Pump(() => seen.Contains(h));
        Assert.True(seen.Contains(h), "abrir não avisou");
        seen.Clear();
        app.Close();
        Pump(() => seen.Contains(h));
        Assert.True(seen.Contains(h), "fechar não avisou");
    }

    [Fact]
    public void Dica_ou_menu_nao_avisa()
    {
        var seen = new List<IntPtr>();
        using var n = new TaskbarNotifications();
        n.ButtonsChanged += seen.Add;
        var tip = new NativeWindow();
        tip.CreateHandle(new CreateParams
        {
            Style = unchecked((int)0x80000000) | 0x10000000, // WS_POPUP | WS_VISIBLE
            ExStyle = Native.WS_EX_TOOLWINDOW,
            X = -2000, Y = -2000, Width = 50, Height = 20,
        });
        var h = tip.Handle;
        Pump(() => false, TimeSpan.FromMilliseconds(300));
        tip.DestroyHandle();
        Pump(() => false, TimeSpan.FromMilliseconds(700));
        Assert.DoesNotContain(h, seen);
    }

    [Fact]
    public void Janela_ocultada_por_area_de_trabalho_virtual_avisa()
    {
        // trocar de área de trabalho oculta ("cloak") as janelas da outra sem fechá-las: os botões da barra mudam
        var seen = new List<IntPtr>();
        using var n = new TaskbarNotifications();
        n.ButtonsChanged += seen.Add;
        using var app = new Form { ShowInTaskbar = true, StartPosition = FormStartPosition.Manual, Location = new(-2000, -2000) };
        app.Show();
        Pump(() => false, TimeSpan.FromMilliseconds(300));
        seen.Clear();
        int cloak = 1;
        Assert.Equal(0, DwmSetWindowAttribute(app.Handle, 13 /* DWMWA_CLOAK */, ref cloak, sizeof(int)));
        Pump(() => seen.Contains(app.Handle));
        Assert.Contains(app.Handle, seen);
    }

    [Fact]
    public void Explorer_reiniciado_avisa()
    {
        int created = 0;
        using var n = new TaskbarNotifications();
        n.TaskbarCreated += () => created++;
        SendMessage(n.Handle, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero); // o que o Explorer manda a todos
        Assert.Equal(1, created);
    }

    [Theory]
    [InlineData(0)] // ABN_STATECHANGE: barra passou a se ocultar (ou deixou)
    [InlineData(1)] // ABN_POSCHANGED: barra mudou de lugar ou tamanho
    [InlineData(2)] // ABN_FULLSCREENAPP: um app entrou ou saiu de tela cheia
    public void Mudancas_da_barra_avisam(int notification)
    {
        int changes = 0;
        using var n = new TaskbarNotifications();
        n.TaskbarChanged += () => changes++;
        SendMessage(n.Handle, TaskbarNotifications.AppBarMessage, notification, IntPtr.Zero);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Registrar_para_os_avisos_da_barra_nao_ocupa_espaco_na_tela()
    {
        // só ABM_SETPOS reserva espaço: a área de trabalho das outras janelas continua igual
        var before = Screen.PrimaryScreen!.WorkingArea;
        using (var n = new TaskbarNotifications())
        {
            Pump(() => false, TimeSpan.FromMilliseconds(500));
            Assert.Equal(before, Screen.PrimaryScreen!.WorkingArea);
        }
    }
}
