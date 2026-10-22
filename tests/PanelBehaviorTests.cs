using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// Quando o painel fecha. Aberto pelo mouse (hover): fecha ao sair. Aberto por clique: fecha ao clicar fora,
/// ao perder o foco ou com Esc. Fixado (botão de pin): só fecha depois de liberado.
/// </summary>
public class PanelBehaviorTests
{
    static readonly PanelState Hover = new(Visible: true, Clicked: false, Locked: false);
    static readonly PanelState Clicked = new(Visible: true, Clicked: true, Locked: false);
    static readonly PanelState Locked = new(Visible: true, Clicked: true, Locked: true);
    static readonly PanelState Closed = new(Visible: false, Clicked: false, Locked: false);

    [Theory]
    [InlineData(PanelEvent.ClickOutside)]
    [InlineData(PanelEvent.Deactivated)]
    [InlineData(PanelEvent.Escape)]
    [InlineData(PanelEvent.SourceClicked)]
    public void Aberto_por_clique_fecha(PanelEvent e) =>
        Assert.Equal(PanelAction.Hide, PanelBehavior.Decide(Clicked, e));

    [Fact]
    public void Aberto_por_clique_nao_fecha_quando_o_mouse_sai() =>
        Assert.Equal(PanelAction.None, PanelBehavior.Decide(Clicked, PanelEvent.MouseLeft));

    [Theory]
    [InlineData(PanelEvent.ClickOutside)]
    [InlineData(PanelEvent.Deactivated)]
    [InlineData(PanelEvent.Escape)]
    [InlineData(PanelEvent.MouseLeft)]
    public void Fixado_nao_fecha(PanelEvent e) =>
        Assert.Equal(PanelAction.None, PanelBehavior.Decide(Locked, e));

    [Fact]
    public void Fixado_clicar_no_icone_traz_para_frente_em_vez_de_fechar() =>
        Assert.Equal(PanelAction.BringToFront, PanelBehavior.Decide(Locked, PanelEvent.SourceClicked));

    [Fact]
    public void Aberto_pelo_mouse_fecha_quando_o_mouse_sai() =>
        Assert.Equal(PanelAction.Hide, PanelBehavior.Decide(Hover, PanelEvent.MouseLeft));

    [Fact]
    public void Aberto_pelo_mouse_clicar_no_icone_mantem_aberto_como_clique() =>
        Assert.Equal(PanelAction.ShowClicked, PanelBehavior.Decide(Hover, PanelEvent.SourceClicked));

    [Fact]
    public void Fechado_clicar_no_icone_abre() =>
        Assert.Equal(PanelAction.ShowClicked, PanelBehavior.Decide(Closed, PanelEvent.SourceClicked));

    [Fact]
    public void Fechado_outros_eventos_nao_fazem_nada()
    {
        foreach (var e in new[] { PanelEvent.ClickOutside, PanelEvent.Deactivated, PanelEvent.Escape, PanelEvent.MouseLeft })
            Assert.Equal(PanelAction.None, PanelBehavior.Decide(Closed, e));
    }

    [Fact]
    public void Fixar_e_liberar_alternam_e_liberar_volta_ao_comportamento_de_clique()
    {
        var locked = PanelBehavior.ToggleLock(Clicked);
        Assert.True(locked.Locked);

        var released = PanelBehavior.ToggleLock(locked);
        Assert.False(released.Locked);
        Assert.Equal(PanelAction.Hide, PanelBehavior.Decide(released, PanelEvent.ClickOutside));
    }

    [Fact]
    public void Fixar_um_painel_aberto_pelo_mouse_o_mantem_aberto() =>
        Assert.Equal(PanelAction.None, PanelBehavior.Decide(PanelBehavior.ToggleLock(Hover), PanelEvent.MouseLeft));
}
