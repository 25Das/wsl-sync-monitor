using WslSyncMonitor;

namespace WslSyncMonitor.WindowsTests;

/// <summary>
/// Aviso de que a nova versão abriu: a anterior só fecha depois dele (senão volta atrás e continua protegendo).
/// Os testes não rodam em paralelo entre si (o nome do evento é um só por sessão do Windows).
/// </summary>
[Collection("aviso de atualização")]
public class UpdateHandshakeTests
{
    [Fact]
    public async Task Nova_versao_avisa_e_a_anterior_ve()
    {
        using var started = Updater.CreateStartedSignal();
        var waiting = Updater.WaitStarted(started, TimeSpan.FromSeconds(5));
        Assert.True(Updater.SignalStarted());
        Assert.True(await waiting);
    }

    [Fact]
    public async Task Sem_aviso_no_prazo_a_anterior_sabe_que_a_nova_nao_abriu()
    {
        using var started = Updater.CreateStartedSignal();
        Assert.False(await Updater.WaitStarted(started, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void Sem_versao_anterior_esperando_o_aviso_nao_falha() => Assert.False(Updater.SignalStarted());
}
