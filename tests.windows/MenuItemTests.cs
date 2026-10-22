using WslSyncMonitor;

namespace WslSyncMonitor.WindowsTests;

public class MenuItemTests
{
    // Na 1.8.0, Item(texto, Func<Task>) chamava a si mesmo (estouro de pilha) ao montar qualquer menu com ação assíncrona.
    [Fact]
    public void Item_com_acao_assincrona_executa_a_acao_uma_vez()
    {
        int calls = 0;
        using var item = TrayApp.Item("x", () => { calls++; return Task.CompletedTask; });

        item.PerformClick();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Item_com_acao_comum_executa_a_acao_uma_vez()
    {
        int calls = 0;
        using var item = TrayApp.Item("x", () => calls++);

        item.PerformClick();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Item_desativado_marcado_e_em_negrito()
    {
        using var item = TrayApp.Item("x", () => { }, enabled: false, check: true, bold: true);

        Assert.False(item.Enabled);
        Assert.True(item.Checked);
        Assert.True(item.Font.Bold);
    }
}
