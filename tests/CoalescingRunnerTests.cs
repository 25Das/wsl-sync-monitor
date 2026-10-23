using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// Leitura completa do estado: uma por vez. Pedidos durante uma leitura não se perdem (antes, voltavam na hora sem
/// ler nada): viram uma única leitura logo depois, e quem pediu espera por ela.
/// </summary>
public class CoalescingRunnerTests
{
    sealed class Work
    {
        public int Runs;
        public TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Run() { Runs++; await Gate.Task; }
        public void Release() { var g = Gate; Gate = new(TaskCreationOptions.RunContinuationsAsynchronously); g.SetResult(); }
    }

    [Fact]
    public async Task Sem_leitura_em_andamento_le_na_hora()
    {
        var w = new Work();
        var r = new CoalescingRunner(w.Run);
        var t = r.RunAsync();
        Assert.Equal(1, w.Runs);
        w.Release();
        await t.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Pedido_durante_uma_leitura_espera_a_proxima_leitura()
    {
        var w = new Work();
        var r = new CoalescingRunner(w.Run);
        var first = r.RunAsync();
        var second = r.RunAsync();   // a leitura em andamento pode ter começado antes da mudança

        w.Release();                 // termina a primeira
        await first.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(second.IsCompleted);
        await WaitUntil(() => w.Runs == 2);

        w.Release();                 // termina a segunda
        await second.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Varios_pedidos_durante_uma_leitura_viram_uma_so()
    {
        var w = new Work();
        var r = new CoalescingRunner(w.Run);
        var first = r.RunAsync();
        var a = r.RunAsync();
        var b = r.RunAsync();
        w.Release();
        await WaitUntil(() => w.Runs == 2);
        w.Release();
        await Task.WhenAll(first, a, b).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, w.Runs);
    }

    [Fact]
    public async Task Pedido_opcional_nao_enfileira_se_ja_esta_lendo()
    {
        // a leitura periódica: se já está lendo, essa leitura serve
        var w = new Work();
        var r = new CoalescingRunner(w.Run);
        var first = r.RunAsync();
        Assert.True(r.Running);
        var skipped = r.RunIfIdleAsync();
        Assert.True(skipped.IsCompleted);
        w.Release();
        await first;
        Assert.Equal(1, w.Runs);
        Assert.False(r.Running);
    }

    [Fact]
    public async Task Falha_na_leitura_nao_trava_as_proximas()
    {
        int runs = 0;
        var r = new CoalescingRunner(() => ++runs == 1 ? Task.FromException(new InvalidOperationException("x")) : Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(r.RunAsync);
        await r.RunAsync();
        Assert.Equal(2, runs);
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(5);
        Assert.True(condition());
    }
}
