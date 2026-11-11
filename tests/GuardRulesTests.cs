using WslSyncMonitor;
using static WslSyncMonitor.GuardActions;

namespace WslSyncMonitor.Tests;

/// <summary>O que a proteção faz a cada atualização, incluindo a opção "Manter ligada".</summary>
public class GuardRulesTests
{
    static GuardInput In(bool running = true, bool protect = true, bool busy = false, int sessions = 0, bool keep = false, int idle = 0) =>
        new(running, protect, busy, sessions, keep, idle);

    [Fact]
    public void Distro_parada_solta_tudo_e_volta_ao_inicio()
    {
        Assert.Equal(new GuardStep(GuardState.Off, CancelVerify | Release, 0), GuardRules.Step(GuardState.InUse, In(running: false)));
        Assert.Equal(new GuardStep(GuardState.Off, None, 0), GuardRules.Step(GuardState.Off, In(running: false)));
    }

    [Fact]
    public void Em_uso_segura_a_distro()
    {
        Assert.Equal(new GuardStep(GuardState.InUse, Hold, 0), GuardRules.Step(GuardState.Off, In(sessions: 2)));
    }

    [Fact]
    public void Sessao_que_volta_durante_a_verificacao_cancela_a_verificacao()
    {
        Assert.Equal(new GuardStep(GuardState.InUse, Hold | CancelVerify, 0), GuardRules.Step(GuardState.Verifying, In(sessions: 1)));
    }

    [Fact]
    public void Sem_sessoes_por_duas_atualizacoes_comeca_a_verificar()
    {
        var first = GuardRules.Step(GuardState.InUse, In(idle: 0));
        Assert.Equal(new GuardStep(GuardState.InUse, Hold, 1), first);

        Assert.Equal(new GuardStep(GuardState.Verifying, Hold | StartVerify, 0), GuardRules.Step(first.State, In(idle: first.IdleTicks)));
    }

    [Fact]
    public void Ligada_sem_ninguem_usando_verifica_e_depois_solta()
    {
        Assert.Equal(new GuardStep(GuardState.Verifying, Hold | StartVerify, 0), GuardRules.Step(GuardState.Off, In()));
        Assert.Equal(new GuardStep(GuardState.Verifying, None, 0), GuardRules.Step(GuardState.Verifying, In()));
        Assert.Equal(new GuardStep(GuardState.Released, None, 0), GuardRules.Step(GuardState.Released, In()));
    }

    [Fact]
    public void Manter_ligada_segura_mesmo_sem_ninguem_usando_e_sem_verificar()
    {
        Assert.Equal(new GuardStep(GuardState.KeptAlive, Hold, 0), GuardRules.Step(GuardState.Off, In(keep: true)));
        Assert.Equal(new GuardStep(GuardState.KeptAlive, Hold, 0), GuardRules.Step(GuardState.Released, In(keep: true)));
        Assert.Equal(new GuardStep(GuardState.KeptAlive, Hold | CancelVerify, 0), GuardRules.Step(GuardState.Verifying, In(keep: true)));
    }

    [Fact]
    public void Manter_ligada_vale_mesmo_com_a_protecao_desligada()
    {
        Assert.Equal(new GuardStep(GuardState.KeptAlive, Hold, 0), GuardRules.Step(GuardState.Off, In(protect: false, keep: true)));
    }

    [Fact]
    public void Quando_manter_ligada_acaba_verifica_a_sincronizacao_antes_de_soltar()
    {
        Assert.Equal(new GuardStep(GuardState.Verifying, Hold | StartVerify, 0), GuardRules.Step(GuardState.KeptAlive, In()));
    }

    [Fact]
    public void Quando_manter_ligada_acaba_com_a_protecao_desligada_solta_direto()
    {
        Assert.Equal(new GuardStep(GuardState.Off, CancelVerify | Release, 0), GuardRules.Step(GuardState.KeptAlive, In(protect: false)));
    }

    [Fact]
    public void Ocupado_com_outra_operacao_nao_muda_nada()
    {
        Assert.Equal(new GuardStep(GuardState.InUse, None, 1), GuardRules.Step(GuardState.InUse, In(busy: true, idle: 1)));
    }

    [Fact]
    public void Ocupado_nao_segura_a_distro_nem_com_manter_ligada()
    {
        // reiniciar a distro (ou atualizar o WSL) mantém o pedido: no meio da operação, segurar a distro de novo
        // criaria um processo que o encerramento em seguida mata (e a proteção acharia que a distro caiu por fora)
        Assert.Equal(new GuardStep(GuardState.KeptAlive, None, 0), GuardRules.Step(GuardState.KeptAlive, In(busy: true, keep: true)));
        Assert.Equal(new GuardStep(GuardState.Off, None, 0), GuardRules.Step(GuardState.Off, In(busy: true, keep: true)));
    }

    [Fact]
    public void Ocupado_com_a_protecao_desligada_nao_solta_no_meio()
    {
        // a operação (ex.: iniciar a distro) decide; a proteção age na leitura depois dela
        Assert.Equal(new GuardStep(GuardState.InUse, None, 0), GuardRules.Step(GuardState.InUse, In(busy: true, protect: false)));
    }

    [Fact]
    public void Pedido_de_manter_ligada_por_tempo_e_ate_desligar()
    {
        var now = new DateTime(2026, 10, 22, 15, 0, 0);
        var oneHour = KeepAliveRequest.For("Ubuntu", TimeSpan.FromHours(1), now);
        var forever = KeepAliveRequest.UntilStopped("Ubuntu");

        Assert.True(oneHour.IsActive(now.AddMinutes(59)));
        Assert.False(oneHour.IsActive(now.AddMinutes(60)));
        Assert.True(forever.IsActive(now.AddYears(1)));
        Assert.Equal("até 16:00", oneHour.Describe());
        Assert.Equal("até você desligar", forever.Describe());
        Assert.False(oneHour.Persist);
        Assert.True(forever.Persist);
    }
}

public class KeepAliveAtLaunchTests
{
    [Fact]
    public void Ate_voce_desligar_liga_a_distro_ao_iniciar_o_app() =>
        Assert.True(KeepAliveRequest.UntilStopped("Ubuntu").StartAtLaunch(new DistroInfo("Ubuntu", false, 2, true)));

    [Fact]
    public void Ja_ligada_nao_precisa() =>
        Assert.False(KeepAliveRequest.UntilStopped("Ubuntu").StartAtLaunch(new DistroInfo("Ubuntu", true, 2, true)));

    [Fact]
    public void Distro_que_nao_existe_mais_nao_liga_nada()
    {
        Assert.False(KeepAliveRequest.UntilStopped("Ubuntu").StartAtLaunch(null));
        Assert.False(KeepAliveRequest.UntilStopped("Ubuntu").StartAtLaunch(new DistroInfo("Debian", false, 2, true)));
    }

    [Fact]
    public void Pedido_por_tempo_nao_vale_depois_de_reiniciar_o_app() =>
        Assert.False(KeepAliveRequest.For("Ubuntu", TimeSpan.FromHours(1), DateTime.Now).StartAtLaunch(new DistroInfo("Ubuntu", false, 2, true)));

    // "Manter ligada até você desligar": encerrar a distro (ou o WSL) é o "desligar"; reiniciar não. Antes, reiniciar
    // pelo menu desativava o pedido sem avisar e a distro deixava de ser mantida ligada (e de ligar ao abrir o app)

    [Fact]
    public void Reiniciar_a_distro_mantem_o_pedido()
    {
        var k = KeepAliveRequest.UntilStopped("Ubuntu");
        Assert.Same(k, k.AfterStop("Ubuntu", restart: true));
    }

    [Fact]
    public void Encerrar_a_distro_desfaz_o_pedido()
    {
        Assert.Null(KeepAliveRequest.UntilStopped("Ubuntu").AfterStop("ubuntu", restart: false));
    }

    [Fact]
    public void Encerrar_outra_distro_nao_mexe_no_pedido()
    {
        var k = KeepAliveRequest.UntilStopped("Ubuntu");
        Assert.Same(k, k.AfterStop("Debian", restart: false));
    }

    [Fact]
    public void Encerrar_o_WSL_inteiro_desfaz_o_pedido()
    {
        Assert.Null(KeepAliveRequest.UntilStopped("Ubuntu").AfterStop(null, restart: false));
    }

    // a distro do Syncthing é solta pela proteção (verifica a sincronização antes); outra distro mantida ligada não tem
    // proteção nenhuma: quando o pedido acaba, ela precisa ser solta na hora (antes, ficava segurada até o app fechar)

    [Fact]
    public void Pedido_que_acaba_numa_distro_sem_o_Syncthing_solta_ela()
    {
        var debian = KeepAliveRequest.For("Debian", TimeSpan.FromHours(1), DateTime.Now);
        Assert.Equal("Debian", KeepAliveRequest.DistroToRelease(debian, next: null, monitored: "Ubuntu"));
    }

    [Fact]
    public void Pedido_trocado_para_outra_distro_solta_a_anterior()
    {
        var debian = KeepAliveRequest.UntilStopped("Debian");
        Assert.Equal("Debian", KeepAliveRequest.DistroToRelease(debian, KeepAliveRequest.UntilStopped("Ubuntu"), monitored: "Ubuntu"));
    }

    [Fact]
    public void Pedido_renovado_na_mesma_distro_nao_solta()
    {
        var debian = KeepAliveRequest.For("Debian", TimeSpan.FromHours(1), DateTime.Now);
        Assert.Null(KeepAliveRequest.DistroToRelease(debian, KeepAliveRequest.UntilStopped("debian"), monitored: "Ubuntu"));
    }

    [Fact]
    public void Pedido_que_acaba_na_distro_do_Syncthing_fica_com_a_protecao()
    {
        Assert.Null(KeepAliveRequest.DistroToRelease(KeepAliveRequest.UntilStopped("Ubuntu"), next: null, monitored: "ubuntu"));
        Assert.Null(KeepAliveRequest.DistroToRelease(previous: null, KeepAliveRequest.UntilStopped("Debian"), monitored: "Ubuntu"));
    }
}
