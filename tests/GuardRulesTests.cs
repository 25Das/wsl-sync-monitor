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
