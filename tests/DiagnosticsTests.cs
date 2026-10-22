using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

public class DiagnosticsTests
{
    static Diagnostics Sample() => new()
    {
        AppVersion = "1.5.5",
        Distro = "Ubuntu-24.04",
        GuiUrl = "http://127.0.0.1:8384",
        ApiKeySource = "config.xml",
        ConfigFiles = ["/home/daniel/.local/state/syncthing/config.xml"],
        ConfigFileUsed = "/home/daniel/.local/state/syncthing/config.xml",
        LastScriptExitCode = 2,
        LastScriptError = "sh: 3: Syntax error: \"fi\" unexpected",
        LastApiError = "HTTP 403 em /rest/system/status",
        Guard = "Em uso (3 sessões)",
    };

    [Fact]
    public void Relatorio_mostra_as_informacoes_para_diagnostico()
    {
        var text = Sample().ToText();

        Assert.Contains("1.5.5", text);
        Assert.Contains("Ubuntu-24.04", text);
        Assert.Contains("http://127.0.0.1:8384", text);
        Assert.Contains("/home/daniel/.local/state/syncthing/config.xml", text);
        Assert.Contains("sh: 3: Syntax error", text);
        Assert.Contains("HTTP 403", text);
        Assert.Contains("Em uso (3 sessões)", text);
    }

    [Fact]
    public void Relatorio_mostra_memoria_e_disco_em_unidades_legiveis()
    {
        var disk = DiskSpace.Effective(distroFree: 1_016_858_365_952, hostFree: 38_654_705_664, hostDrive: "C:");
        var text = (Sample() with { VmMemory = 2_254_857_830, VhdSize = 9_985_798_144, Disk = disk }).ToText();

        Assert.Contains("Memória do WSL: 2,1 GiB", text);
        Assert.Contains("Arquivo de disco da distro: 9,3 GiB", text);
        Assert.Contains("Espaço livre: 36 GiB livres (limitado pela unidade C:)", text);
        Assert.DoesNotContain("947 GiB", text); // o disco virtual da distro não é o espaço que sobra de verdade
    }

    [Fact]
    public void Relatorio_sem_dados_nao_falha_e_indica_ausencia()
    {
        var text = new Diagnostics().ToText();

        Assert.Contains("—", text);
    }

    [Fact]
    public void Erro_do_script_longo_e_resumido()
    {
        var d = Sample() with { LastScriptError = new string('x', 5000) };

        Assert.True(d.ToText().Length < 3000);
    }
}
