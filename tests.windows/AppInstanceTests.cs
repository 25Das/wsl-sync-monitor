using WslSyncMonitor;

namespace WslSyncMonitor.WindowsTests;

/// <summary>Pedido para a instância em execução fechar (instalação e desinstalação).</summary>
public class AppInstanceTests
{
    static string Unique() => "WslSyncMonitorTests-" + Guid.NewGuid().ToString("N");

    /// <summary>Segura o mutex numa thread separada, como outra instância do app faria.</summary>
    static (Thread Thread, ManualResetEventSlim Release) HoldMutex(string mutexName)
    {
        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            using var m = new Mutex(false, mutexName);
            m.WaitOne();
            held.Set();
            release.Wait();
            m.ReleaseMutex();
        });
        t.Start();
        held.Wait();
        return (t, release);
    }

    [Fact]
    public void Nenhuma_instancia_em_execucao()
    {
        var names = new AppInstance.Names(Unique(), Unique());
        using var mutex = AppInstance.CreateMutex(names);

        Assert.Equal(ExitRequest.NoneRunning, AppInstance.RequestExitAndWait(mutex, names, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Versao_antiga_que_nao_escuta_o_pedido_e_detectada_na_hora()
    {
        var names = new AppInstance.Names(Unique(), Unique());
        var (thread, release) = HoldMutex(names.Mutex);
        using var mutex = AppInstance.CreateMutex(names);
        var started = DateTime.Now;

        var result = AppInstance.RequestExitAndWait(mutex, names, TimeSpan.FromMinutes(3));

        Assert.Equal(ExitRequest.NotListening, result);
        Assert.True(DateTime.Now - started < TimeSpan.FromSeconds(5), "não deve esperar o tempo todo");
        release.Set(); thread.Join();
    }

    [Fact]
    public void Instancia_que_escuta_fecha_quando_pedido()
    {
        var names = new AppInstance.Names(Unique(), Unique());
        var held = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            using var m = new Mutex(false, names.Mutex);
            using var exit = AppInstance.CreateExitEvent(names);
            m.WaitOne();
            held.Set();
            exit.WaitOne(TimeSpan.FromSeconds(10)); // a "instância" fecha quando pedem
            m.ReleaseMutex();
        });
        t.Start();
        held.Wait();
        using var mutex = AppInstance.CreateMutex(names);

        Assert.Equal(ExitRequest.Exited, AppInstance.RequestExitAndWait(mutex, names, TimeSpan.FromSeconds(10)));
        mutex.ReleaseMutex();
        t.Join();
    }
}
