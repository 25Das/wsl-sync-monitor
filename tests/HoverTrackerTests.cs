using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>Painel aberto pela passagem do mouse no widget: quando abrir, quando fechar e quando precisa conferir o mouse.</summary>
public class HoverTrackerTests
{
    static readonly DateTime T0 = new(2026, 10, 23, 12, 0, 0);
    static readonly PanelState Closed = new(false, false, false);
    static readonly PanelState ByHover = new(true, false, false);
    static readonly PanelState ByClick = new(true, true, false);

    static HoverInput On(PanelState p, bool widget = false, bool panel = false, bool menu = false, bool button = false, bool overMenu = false) =>
        new(p, widget, panel, menu, button, overMenu);

    [Fact]
    public void Abre_depois_do_atraso_com_o_mouse_parado_no_widget()
    {
        var h = new HoverTracker();
        h.WidgetEntered(T0, enabled: true);
        Assert.Equal(HoverAction.None, h.Tick(T0.AddMilliseconds(300), On(Closed, widget: true), delayMs: 400));
        Assert.Equal(HoverAction.ShowPopup, h.Tick(T0.AddMilliseconds(400), On(Closed, widget: true), delayMs: 400));
        Assert.Equal(HoverAction.None, h.Tick(T0.AddMilliseconds(500), On(ByHover, widget: true), delayMs: 400)); // não abre de novo
    }

    [Fact]
    public void Sair_do_widget_cancela_na_hora()
    {
        var h = new HoverTracker();
        h.WidgetEntered(T0, enabled: true);
        h.WidgetLeft();
        Assert.False(h.NeedsTicks(Closed)); // nada a conferir: o timer para
        Assert.Equal(HoverAction.None, h.Tick(T0.AddSeconds(1), On(Closed, widget: true), delayMs: 400));
    }

    [Fact]
    public void Entrar_e_sair_rapido_varias_vezes_conta_o_atraso_da_ultima_entrada()
    {
        var h = new HoverTracker();
        for (int i = 0; i < 5; i++) { h.WidgetEntered(T0.AddMilliseconds(i * 100), enabled: true); h.WidgetLeft(); }
        h.WidgetEntered(T0.AddMilliseconds(600), enabled: true);
        Assert.Equal(HoverAction.None, h.Tick(T0.AddMilliseconds(900), On(Closed, widget: true), delayMs: 400));
        Assert.Equal(HoverAction.ShowPopup, h.Tick(T0.AddMilliseconds(1000), On(Closed, widget: true), delayMs: 400));
    }

    [Fact]
    public void Desligado_nas_configuracoes_ou_com_menu_aberto_nao_abre()
    {
        var h = new HoverTracker();
        h.WidgetEntered(T0, enabled: false);
        Assert.False(h.NeedsTicks(Closed));

        h.WidgetEntered(T0, enabled: true);
        Assert.Equal(HoverAction.None, h.Tick(T0.AddSeconds(1), On(Closed, widget: true, menu: true), delayMs: 400));
        Assert.False(h.NeedsTicks(Closed));
    }

    [Fact]
    public void Fecha_quando_o_mouse_fica_fora_do_widget_e_do_painel_por_um_tempo()
    {
        var h = new HoverTracker();
        Assert.Equal(HoverAction.None, h.Tick(T0, On(ByHover), delayMs: 400));
        Assert.Equal(HoverAction.None, h.Tick(T0.AddMilliseconds(300), On(ByHover), delayMs: 400));
        Assert.Equal(HoverAction.MouseLeft, h.Tick(T0.AddMilliseconds(450), On(ByHover), delayMs: 400));
    }

    [Fact]
    public void Voltar_ao_painel_a_tempo_mantem_aberto()
    {
        var h = new HoverTracker();
        h.Tick(T0, On(ByHover), delayMs: 400);
        h.Tick(T0.AddMilliseconds(300), On(ByHover, panel: true), delayMs: 400);
        Assert.Equal(HoverAction.None, h.Tick(T0.AddMilliseconds(500), On(ByHover), delayMs: 400)); // contagem recomeçou
    }

    [Fact]
    public void Aberto_por_clique_fecha_com_clique_fora_mas_nao_no_menu()
    {
        var h = new HoverTracker();
        Assert.Equal(HoverAction.None, h.Tick(T0, On(ByClick), delayMs: 400)); // mouse fora, sem clique
        Assert.Equal(HoverAction.None, h.Tick(T0, On(ByClick, button: true, overMenu: true), delayMs: 400));
        Assert.Equal(HoverAction.None, h.Tick(T0, On(ByClick, button: true, panel: true), delayMs: 400));
        Assert.Equal(HoverAction.ClickOutside, h.Tick(T0, On(ByClick, button: true), delayMs: 400));
    }

    [Fact]
    public void So_confere_o_mouse_com_o_painel_aberto_ou_esperando_abrir()
    {
        var h = new HoverTracker();
        Assert.False(h.NeedsTicks(Closed));
        Assert.True(h.NeedsTicks(ByClick));
        h.WidgetEntered(T0, enabled: true);
        Assert.True(h.NeedsTicks(Closed));
    }
}
