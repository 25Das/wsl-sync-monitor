using WslSyncMonitor;

namespace WslSyncMonitor.WindowsTests;

/// <summary>
/// O processo que mantém a distro ligada avisa quando termina (o Windows sinaliza o handle do processo), e não na
/// próxima leitura: se a distro for encerrada por fora, a proteção sabe na hora.
/// </summary>
public sealed class DetachedProcessTests
{
    static Native.DetachedProcess Cmd(string args) => Native.DetachedProcess.Start($"cmd.exe /c {args}");

    [Fact]
    public void Avisa_quando_o_processo_termina()
    {
        using var p = Cmd("ping -n 2 127.0.0.1 >nul");
        using var exited = new ManualResetEventSlim();
        p.WatchExit(exited.Set);
        Assert.True(exited.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(p.HasExited);
    }

    [Fact]
    public void Processo_que_ja_terminou_avisa_logo()
    {
        using var p = Cmd("exit 0");
        Thread.Sleep(500);
        using var exited = new ManualResetEventSlim();
        p.WatchExit(exited.Set);
        Assert.True(exited.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Soltar_a_distro_nao_avisa_como_fim_inesperado()
    {
        // o app encerra o processo de propósito ao soltar a distro: isso não é "terminou por fora"
        using var p = Cmd("ping -n 30 127.0.0.1 >nul");
        using var exited = new ManualResetEventSlim();
        p.WatchExit(exited.Set);
        p.StopWatchingExit();
        p.Kill();
        Assert.False(exited.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Descartar_enquanto_espera_nao_avisa()
    {
        // o handle do processo só pode ser fechado depois de a espera do Windows ter sido desfeita: senão o número do
        // handle pode ir para outro objeto e a espera avisaria um "fim" que não aconteceu
        int spurious = 0;
        for (int i = 0; i < 20; i++)
        {
            var p = Cmd("ping -n 3 127.0.0.1 >nul");
            p.WatchExit(() => Interlocked.Increment(ref spurious));
            p.Dispose(); // o processo continua rodando; o app só deixou de acompanhá-lo
        }
        Thread.Sleep(500);
        Assert.Equal(0, spurious);
    }

    [Fact]
    public void Sabe_ha_quanto_tempo_o_processo_existe()
    {
        using var p = Cmd("ping -n 30 127.0.0.1 >nul");
        Assert.InRange(DateTime.Now - p.StartedAt, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        p.Kill();
    }
}
