using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>Observar o settings.json em vez de conferir a data dele a cada atualização.</summary>
public sealed class FileChangeWatcherTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wsm-fw-" + Guid.NewGuid().ToString("N")[..8]);
    string File1 => Path.Combine(_dir, "settings.json");

    public FileChangeWatcherTests() { Directory.CreateDirectory(_dir); File.WriteAllText(File1, "{}"); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Avisa_quando_o_arquivo_muda()
    {
        using var changed = new ManualResetEventSlim();
        using var w = new FileChangeWatcher(File1, TimeSpan.FromMilliseconds(100), changed.Set);
        File.WriteAllText(File1, """{ "RefreshSeconds": 10 }""");
        Assert.True(changed.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Gravacoes_seguidas_viram_um_aviso_so()
    {
        // editores gravam em vários passos: um aviso só, depois que o arquivo para de mudar
        int count = 0;
        using var w = new FileChangeWatcher(File1, TimeSpan.FromMilliseconds(300), () => Interlocked.Increment(ref count));
        for (int i = 0; i < 5; i++) { File.WriteAllText(File1, $$"""{ "n": {{i}} }"""); Thread.Sleep(30); }
        Thread.Sleep(1500);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Outros_arquivos_da_pasta_nao_contam()
    {
        int count = 0;
        using var w = new FileChangeWatcher(File1, TimeSpan.FromMilliseconds(100), () => Interlocked.Increment(ref count));
        File.WriteAllText(Path.Combine(_dir, "outro.txt"), "x");
        Thread.Sleep(800);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Arquivo_que_ainda_nao_existe_tambem_e_observado()
    {
        File.Delete(File1);
        using var changed = new ManualResetEventSlim();
        using var w = new FileChangeWatcher(File1, TimeSpan.FromMilliseconds(100), changed.Set);
        File.WriteAllText(File1, "{}");
        Assert.True(changed.Wait(TimeSpan.FromSeconds(5)));
    }
}
