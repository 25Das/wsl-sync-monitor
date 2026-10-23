using WslSyncMonitor;

namespace WslSyncMonitor.WindowsTests;

/// <summary>
/// O widget relê os ícones da barra quando o Windows avisa que uma janela com botão nela abriu ou fechou (o mesmo
/// aviso que a própria barra usa), e não a cada janela qualquer (dicas, menus) que aparece e some.
/// </summary>
[Collection("E2E")]
public sealed class ShellHookTests
{
    // o aviso vale para todas as janelas do sistema (no CI, outras também abrem e fecham): conta só as do teste

    [Fact]
    public void Avisa_quando_uma_janela_com_botao_na_barra_abre_e_fecha()
    {
        var seen = new List<IntPtr>();
        using var hook = new ShellHookWindow(seen.Add);
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
        // janela "de ferramenta" sem dona, como dicas, menus e miniaturas: não tem botão na barra
        var seen = new List<IntPtr>();
        using var hook = new ShellHookWindow(seen.Add);
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

    static void Pump(Func<bool> done, TimeSpan? max = null)
    {
        var end = DateTime.Now + (max ?? TimeSpan.FromSeconds(5));
        while (!done() && DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(20); }
    }
}
