using Microsoft.Win32;
using WslSyncMonitor;

namespace WslSyncMonitor.WindowsTests;

/// <summary>
/// A lista de distros do WSL (instalar, remover, trocar a padrão) fica no registro: o Windows avisa quando ela muda,
/// em vez de o app descobrir na próxima leitura. Testado numa chave temporária só do teste.
/// </summary>
public sealed class RegistryChangeWatcherTests : IDisposable
{
    readonly string _path = @"Software\WslSyncMonitorTests\" + Guid.NewGuid().ToString("N");
    readonly RegistryKey _key;

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
        Assert.True(changed.Wait(TimeSpan.FromSeconds(5)), "primeira mudança não avisou");

        _key.SetValue("DefaultDistribution", "{b}");
        Assert.True(changed.Wait(TimeSpan.FromSeconds(5)), "segunda mudança não avisou (não voltou a observar)");
    }

    [Fact]
    public void Avisa_quando_uma_distro_e_instalada_ou_removida()
    {
        using var changed = new SemaphoreSlim(0);
        using var w = RegistryChangeWatcher.Open(RegistryHive.CurrentUser, _path, () => changed.Release())!;

        using (var distro = _key.CreateSubKey("{0f0e}")) distro.SetValue("DistributionName", "Ubuntu");
        Assert.True(changed.Wait(TimeSpan.FromSeconds(5)), "instalar não avisou");
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
}
