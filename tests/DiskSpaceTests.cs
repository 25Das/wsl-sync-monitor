using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// O disco da distro é virtual (cresce até ~1 TB): o espaço que realmente sobra é o menor entre o livre dentro da
/// distro e o livre na unidade do Windows onde fica o arquivo .vhdx.
/// </summary>
public class DiskSpaceTests
{
    const long GiB = 1L << 30;

    [Fact]
    public void Limitado_pela_unidade_do_Windows()
    {
        var d = DiskSpace.Effective(distroFree: 947 * GiB, hostFree: 36 * GiB, hostDrive: "C:");

        Assert.Equal(36 * GiB, d.Free);
        Assert.Equal("36 GiB livres (limitado pela unidade C:)", d.Describe());
    }

    [Fact]
    public void Limitado_pelo_disco_da_distro()
    {
        var d = DiskSpace.Effective(distroFree: 20 * GiB, hostFree: 500 * GiB, hostDrive: "E:");

        Assert.Equal(20 * GiB, d.Free);
        Assert.Equal("20 GiB livres (limitado pelo disco da distro)", d.Describe());
    }

    [Fact]
    public void Sem_leitura_da_distro_usa_a_unidade_do_Windows()
    {
        Assert.Equal("36 GiB livres na unidade C:", DiskSpace.Effective(null, 36 * GiB, "C:").Describe());
    }

    [Fact]
    public void Sem_nenhuma_leitura() => Assert.Null(DiskSpace.Effective(null, null, null).Free);
}
