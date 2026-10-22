using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

public class PanelFooterTests
{
    [Fact]
    public void WSL_desligado_so_oferece_iniciar_a_distro_em_destaque()
    {
        Assert.Equal([new FooterButton("Iniciar Ubuntu-24.04", FooterAction.StartDistro, true)],
            PanelFooter.For("Ubuntu-24.04", running: false, syncthing: false, busy: null));
    }

    [Fact]
    public void Ligado_com_Syncthing_abre_a_interface_e_encerra_a_distro()
    {
        Assert.Equal([
                new FooterButton("Abrir interface do Syncthing", FooterAction.OpenSyncthing, true),
                new FooterButton("Encerrar Ubuntu-24.04", FooterAction.StopDistro, false)],
            PanelFooter.For("Ubuntu-24.04", running: true, syncthing: true, busy: null));
    }

    [Fact]
    public void Ligado_sem_Syncthing_so_encerra()
    {
        Assert.Equal([new FooterButton("Encerrar Ubuntu-24.04", FooterAction.StopDistro, false)],
            PanelFooter.For("Ubuntu-24.04", running: true, syncthing: false, busy: null));
    }

    [Fact]
    public void Durante_uma_operacao_mostra_aguarde()
    {
        var b = PanelFooter.For("Ubuntu-24.04", running: true, syncthing: false, busy: "Encerrando…");
        Assert.Equal([new FooterButton("Aguarde…", FooterAction.None, false)], b);
    }

    [Fact]
    public void Sem_distro_nao_tem_botoes() => Assert.Empty(PanelFooter.For(null, false, false, null));
}
