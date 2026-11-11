using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// O settings.json é lido de novo quando muda (e pode ser editado à mão): gravá-lo direto deixava, por um instante,
/// um arquivo vazio ou pela metade, lido como "inválido", e um fechamento no meio perdia as configurações.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wsm-atomic-" + Guid.NewGuid().ToString("N"));
    string FilePath => Path.Combine(_dir, "settings.json");

    public AtomicFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Cria_o_arquivo()
    {
        AtomicFile.WriteAllText(FilePath, "{\"a\":1}");
        Assert.Equal("{\"a\":1}", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Substitui_o_conteudo_sem_deixar_sobras()
    {
        File.WriteAllText(FilePath, "antigo, bem mais comprido que o novo");
        AtomicFile.WriteAllText(FilePath, "novo");
        Assert.Equal("novo", File.ReadAllText(FilePath));
        Assert.Equal(["settings.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Leitores_nunca_veem_o_arquivo_pela_metade()
    {
        var big = new string('x', 1_000_000);
        var small = "{}";
        AtomicFile.WriteAllText(FilePath, small);
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            int bad = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var t = File.ReadAllText(FilePath);
                    if (t != big && t != small) bad++;
                }
                catch (IOException) { } // aberto no momento da troca: quem lê tenta de novo (como o app faz)
                catch (UnauthorizedAccessException) { }
            }
            return bad;
        });
        for (int i = 0; i < 30; i++) AtomicFile.WriteAllText(FilePath, i % 2 == 0 ? big : small);
        stop.Cancel();
        Assert.Equal(0, reader.Result);
    }
}
