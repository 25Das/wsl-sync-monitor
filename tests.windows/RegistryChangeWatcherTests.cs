using Microsoft.Win32;
using WslSyncMonitor;

namespace WslSyncMonitor.WindowsTests;

/// <summary>
/// A lista de distros do WSL (instalar, remover, trocar a padrão) fica no registro: o Windows avisa quando ela muda,
/// em vez de o app descobrir na próxima leitura. Testado numa chave temporária só do teste.
/// <para>
/// O aviso chega por um thread do pool: com os testes pesados rodando ao mesmo tempo no CI, ele pode atrasar. Por isso
/// a classe roda em série com eles, e o prazo (só limita a falha; o Windows garante o aviso) é largo.
/// </para>
/// </summary>
[Collection("E2E")]
public sealed class RegistryChangeWatcherTests : IDisposable
{
    readonly string _path = @"Software\WslSyncMonitorTests\" + Guid.NewGuid().ToString("N");
    readonly RegistryKey _key;
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    public RegistryChangeWatcherTests() => _key = Registry.CurrentUser.CreateSubKey(_path);

    public void Dispose()
    {
        _key.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
    }

    [Fact]
    public void Avisa_quando_um_valor_muda_e_continua_avisando()
    {
        using var changed = new SemaphoreSlim(0);
        using var w = RegistryChangeWatcher.Open(RegistryHive.CurrentUser, _path, () => changed.Release())!;

        _key.SetValue("DefaultDistribution", "{a}");
        Assert.True(changed.Wait(Limit), "primeira mudança não avisou");

        _key.SetValue("DefaultDistribution", "{b}");
        Assert.True(changed.Wait(Limit), "segunda mudança não avisou (não voltou a observar)");
    }

    [Fact]
    public void Avisa_quando_uma_distro_e_instalada_ou_removida()
    {
        using var changed = new SemaphoreSlim(0);
        using var w = RegistryChangeWatcher.Open(RegistryHive.CurrentUser, _path, () => changed.Release())!;

        using (var distro = _key.CreateSubKey("{0f0e}")) distro.SetValue("DistributionName", "Ubuntu");
        Assert.True(changed.Wait(Limit), "instalar não avisou");
    }

    [Fact]
    public void Depois_de_descartado_nao_avisa_mais()
    {
        int changes = 0;
        var w = RegistryChangeWatcher.Open(RegistryHive.CurrentUser, _path, () => Interlocked.Increment(ref changes))!;
        w.Dispose();
        _key.SetValue("x", 1);
        Thread.Sleep(1000);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Chave_que_nao_existe_nao_e_observada() =>
        Assert.Null(RegistryChangeWatcher.Open(RegistryHive.CurrentUser, _path + @"\nao-existe", () => { }));

    [Fact]
    public void Sem_subchaves_avisa_so_quando_uma_chave_e_criada_ou_removida_aqui()
    {
        // WSL instalado depois do app: observa a chave de cima até a do WSL aparecer
        using var changed = new SemaphoreSlim(0);
        using var w = RegistryChangeWatcher.Open(RegistryHive.CurrentUser, _path, () => changed.Release(), subtree: false)!;
        using (var lxss = _key.CreateSubKey("Lxss")) { }
        Assert.True(changed.Wait(Limit), "criar a chave não avisou");
    }

    [Fact]
    public void Criar_subchaves_avisa_sempre()
    {
        // repetido para o CI mostrar se o aviso de chave criada falha às vezes (e com que diagnóstico)
        for (int i = 0; i < 30; i++)
        {
            foreach (var subtree in new[] { true, false })
            {
                using var changed = new SemaphoreSlim(0);
                using var w = RegistryChangeWatcher.Open(RegistryHive.CurrentUser, _path, () => changed.Release(), subtree)!;
                var name = $"k{i}{subtree}";
                using (_key.CreateSubKey(name)) { }
                bool ok = changed.Wait(TimeSpan.FromSeconds(5));
                Assert.True(ok, $"volta {i}, subtree={subtree}: sem aviso (sinais={w.Signals}, erro={w.LastError?.Message ?? "nenhum"})");
                _key.DeleteSubKey(name);
            }
        }
    }
}
