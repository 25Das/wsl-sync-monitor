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
}
