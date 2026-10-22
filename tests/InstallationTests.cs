using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

public class InstallationTests
{
    static readonly InstallPaths Paths = InstallPaths.For(
        localAppData: @"C:\Users\Daniel\AppData\Local",
        desktop: @"C:\Users\Daniel\Desktop",
        startMenuPrograms: @"C:\Users\Daniel\AppData\Roaming\Microsoft\Windows\Start Menu\Programs");

    [Fact]
    public void Instala_em_LocalAppData_Programs_onde_o_atualizador_pode_gravar()
    {
        Assert.Equal(@"C:\Users\Daniel\AppData\Local\Programs\WslSyncMonitor", Paths.Directory);
        Assert.Equal(@"C:\Users\Daniel\AppData\Local\Programs\WslSyncMonitor\WslSyncMonitor.exe", Paths.Exe);
    }

    [Fact]
    public void Atalhos_na_area_de_trabalho_e_no_menu_iniciar()
    {
        Assert.Equal(@"C:\Users\Daniel\Desktop\WSL Sync Monitor.lnk", Paths.DesktopShortcut);
        Assert.Equal(@"C:\Users\Daniel\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\WSL Sync Monitor.lnk", Paths.StartMenuShortcut);
    }

    [Theory]
    [InlineData(@"C:\Users\Daniel\AppData\Local\Programs\WslSyncMonitor\WslSyncMonitor.exe", true)]
    [InlineData(@"c:\users\daniel\appdata\local\programs\wslsyncmonitor\WSLSYNCMONITOR.EXE", true)] // Windows não diferencia maiúsculas
    [InlineData(@"C:\Users\Daniel\Downloads\WslSyncMonitor.exe", false)]
    public void Sabe_se_esta_rodando_da_pasta_de_instalacao(string current, bool installed)
    {
        Assert.Equal(installed, Paths.IsInstalledExe(current));
    }

    [Fact]
    public void Registro_de_desinstalacao_para_Aplicativos_instalados()
    {
        var values = Paths.UninstallEntry(new Version(1, 9, 0), sizeBytes: 400 * 1024);

        Assert.Equal("WSL Sync Monitor", values["DisplayName"]);
        Assert.Equal("1.9.0", values["DisplayVersion"]);
        Assert.Equal(Paths.Directory, values["InstallLocation"]);
        Assert.Equal($"\"{Paths.Exe}\"", values["DisplayIcon"]);
        Assert.Equal($"\"{Paths.Exe}\" --uninstall", values["UninstallString"]);
        Assert.Equal(400, values["EstimatedSize"]); // em KB
        Assert.Equal(1, values["NoModify"]);
        Assert.Equal(1, values["NoRepair"]);
    }
}
