using System.Diagnostics;
using WslSyncMonitor;

namespace WslSyncMonitor.WindowsTests;

/// <summary>
/// Aviso de que a nova versão está pronta (abriu e fez a primeira leitura): só então a anterior fecha. O aviso é
/// de uma versão anterior específica (pelo número do processo), então um app aberto à mão não o dispara.
/// </summary>
public class UpdateHandshakeTests
{
    static int FakePid() => Random.Shared.Next(1_000_000, 2_000_000);

    static Process LongRunning() => Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;

    [Fact]
    public async Task Nova_versao_avisa_que_esta_pronta()
    {
        int pid = FakePid();
        using var ready = Updater.CreateReadySignal(pid);
        using var next = LongRunning();
        try
        {
            var waiting = Updater.WaitReady(ready, next, TimeSpan.FromSeconds(10));
            Assert.True(Updater.SignalReady(pid));
            Assert.True(await waiting);
        }
        finally { next.Kill(); }
    }

    [Fact]
    public async Task Nova_versao_que_fecha_antes_de_avisar_e_percebida_na_hora()
    {
        using var ready = Updater.CreateReadySignal(FakePid());
        using var next = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        var sw = Stopwatch.StartNew();
        Assert.False(await Updater.WaitReady(ready, next, TimeSpan.FromSeconds(30)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"{sw.Elapsed}");
    }

    [Fact]
    public async Task Sem_aviso_no_prazo_nao_esta_pronta()
    {
        using var ready = Updater.CreateReadySignal(FakePid());
        using var next = LongRunning();
        try { Assert.False(await Updater.WaitReady(ready, next, TimeSpan.FromMilliseconds(300))); }
        finally { next.Kill(); }
    }

    [Fact]
    public void Aviso_para_outra_versao_anterior_nao_vale()
    {
        int pid = FakePid();
        using var ready = Updater.CreateReadySignal(pid);
        Assert.False(Updater.SignalReady(pid + 1));
        Assert.False(ready.WaitOne(0));
    }
}
