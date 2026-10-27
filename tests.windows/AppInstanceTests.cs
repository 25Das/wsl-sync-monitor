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

    [Fact]
    public void Na_atualizacao_a_versao_anterior_libera_o_mutex_e_o_retoma_se_a_nova_falhar()
    {
        var name = Unique();
        using var mutex = new Mutex(false, name);
        var owner = new AppInstance.Owner(mutex);
        Assert.True(owner.TryAcquire(TimeSpan.Zero));

        owner.Release(); // a nova versão pode assumir
        using (var other = new Mutex(false, name))
        {
            Assert.True(other.WaitOne(0)); // ... e assume
            other.ReleaseMutex();
        }
        Assert.True(owner.Reacquire(TimeSpan.FromSeconds(1))); // a nova falhou: a anterior volta
        owner.Release();
        owner.Release(); // liberar de novo não falha (o fim do Main sempre libera)
    }

    [Fact]
    public void Retomar_o_mutex_da_nova_versao_que_morreu_segurando_funciona()
    {
        var name = Unique();
        using var mutex = new Mutex(false, name);
        var owner = new AppInstance.Owner(mutex);
        var t = new Thread(() => { var m = new Mutex(false, name); m.WaitOne(); }); // sai sem liberar
        t.Start(); t.Join();
        Assert.True(owner.Reacquire(TimeSpan.FromSeconds(1)));
        owner.Release();
    }
}
