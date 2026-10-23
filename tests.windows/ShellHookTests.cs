using WslSyncMonitor;

namespace WslSyncMonitor.WindowsTests;

/// <summary>
/// O widget relê os ícones da barra quando o Windows avisa que uma janela com botão nela abriu ou fechou (o mesmo
/// aviso que a própria barra usa), e não a cada janela qualquer (dicas, menus) que aparece e some.
/// </summary>
[Collection("E2E")]
public sealed class ShellHookTests
{
    [Fact]
    public void Avisa_quando_uma_janela_com_botao_na_barra_abre_e_fecha()
    {
        int changes = 0;
        using var hook = new ShellHookWindow(() => changes++);
        using (var app = new Form { ShowInTaskbar = true, Text = "janela de teste", StartPosition = FormStartPosition.Manual, Location = new(-2000, -2000) })
        {
            app.Show();
            Pump(() => changes > 0);
            Assert.True(changes > 0, "abrir não avisou");
            int opened = changes;
            app.Close();
            Pump(() => changes > opened);
            Assert.True(changes > opened, "fechar não avisou");
        }
    }

    [Fact]
    public void Janela_sem_botao_na_barra_nao_avisa()
    {
        int changes = 0;
        using var hook = new ShellHookWindow(() => changes++);
        using (var tip = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new(-2000, -2000) })
        {
            tip.Show();
            tip.Close();
        }
        Pump(() => false, TimeSpan.FromMilliseconds(700));
        Assert.Equal(0, changes);
    }

    static void Pump(Func<bool> done, TimeSpan? max = null)
    {
        var end = DateTime.Now + (max ?? TimeSpan.FromSeconds(5));
        while (!done() && DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(20); }
    }
}
