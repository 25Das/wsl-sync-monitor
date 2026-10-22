using Microsoft.Win32;
using WslSyncMonitor;

namespace WslSyncMonitor.WindowsTests;

/// <summary>Instala numa pasta temporária (atalhos de verdade, registro numa chave de teste) e desinstala.</summary>
public sealed class InstallerTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("wslsync-install").FullName;
    readonly string _testKey = @"Software\WslSyncMonitorTests\Uninstall-" + Guid.NewGuid().ToString("N");
    readonly InstallPaths _paths;
    readonly string _source;

    public InstallerTests()
    {
        _paths = InstallPaths.For(Path.Combine(_root, "Local"), Path.Combine(_root, "Desktop"), Path.Combine(_root, "Programs"));
        Directory.CreateDirectory(Path.Combine(_root, "Desktop"));
        Directory.CreateDirectory(Path.Combine(_root, "Programs"));
        _source = Path.Combine(_root, "Downloads", "WslSyncMonitor.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_source)!);
        File.WriteAllBytes(_source, [(byte)'M', (byte)'Z', 1, 2, 3]);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\WslSyncMonitorTests", throwOnMissingSubKey: false);
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Início com o Windows de mentira (no teste não mexe no registro de verdade).</summary>
    sealed class FakeStartup(string? current = null) : IStartup
    {
        public string? Target { get; private set; } = current;
        public string? Get() => Target;
        public void Set(string? exe) => Target = exe;
    }

    Installer NewInstaller(IStartup? startup = null) => new(_paths, _testKey, startup ?? new FakeStartup());

    [Fact]
    public void Instala_copiando_o_exe_criando_atalhos_e_o_registro()
    {
        NewInstaller().Install(_source, new InstallOptions(DesktopShortcut: true, StartMenuShortcut: true, StartWithWindows: false));

        Assert.Equal(File.ReadAllBytes(_source), File.ReadAllBytes(_paths.Exe));
        Assert.Equal(_paths.Exe, ShellLink.ReadTarget(_paths.DesktopShortcut), ignoreCase: true);
        Assert.Equal(_paths.Exe, ShellLink.ReadTarget(_paths.StartMenuShortcut), ignoreCase: true);
        using var key = Registry.CurrentUser.OpenSubKey(_testKey)!;
        Assert.Equal("WSL Sync Monitor", key.GetValue("DisplayName"));
        Assert.Equal($"\"{_paths.Exe}\" --uninstall", key.GetValue("UninstallString"));
    }

    [Fact]
    public void So_cria_os_atalhos_escolhidos()
    {
        NewInstaller().Install(_source, new InstallOptions(DesktopShortcut: false, StartMenuShortcut: true, StartWithWindows: false));

        Assert.False(File.Exists(_paths.DesktopShortcut));
        Assert.True(File.Exists(_paths.StartMenuShortcut));
    }

    [Fact]
    public void Iniciar_com_o_Windows_aponta_para_o_exe_instalado()
    {
        var startup = new FakeStartup(current: @"C:\Users\Daniel\Downloads\WslSyncMonitor.exe"); // cópia avulsa anterior
        NewInstaller(startup).Install(_source, new InstallOptions(false, false, StartWithWindows: true));

        Assert.Equal(_paths.Exe, startup.Target);
    }

    [Fact]
    public void Desinstalar_remove_atalhos_e_registro_e_mantem_o_exe_para_ser_apagado_depois()
    {
        var installer = NewInstaller();
        installer.Install(_source, new InstallOptions(true, true, false));

        installer.RemoveShortcutsAndRegistration();

        Assert.False(File.Exists(_paths.DesktopShortcut));
        Assert.False(File.Exists(_paths.StartMenuShortcut));
        Assert.Null(Registry.CurrentUser.OpenSubKey(_testKey));
    }

    [Fact]
    public void Reinstalar_por_cima_substitui_o_exe()
    {
        var installer = NewInstaller();
        installer.Install(_source, new InstallOptions(false, false, false));
        File.WriteAllBytes(_source, [(byte)'M', (byte)'Z', 9, 9]);

        installer.Install(_source, new InstallOptions(false, false, false));

        Assert.Equal(File.ReadAllBytes(_source), File.ReadAllBytes(_paths.Exe));
    }

    [Fact]
    public void Desinstalar_mantem_o_inicio_automatico_de_outra_copia()
    {
        var other = @"C:\Users\Daniel\Downloads\WslSyncMonitor.exe";
        var startup = new FakeStartup(current: other);
        var installer = NewInstaller(startup);
        installer.Install(_source, new InstallOptions(false, false, StartWithWindows: false));

        installer.RemoveShortcutsAndRegistration();

        Assert.Equal(other, startup.Target);
    }

    [Fact]
    public void Desinstalar_remove_o_inicio_automatico_da_copia_instalada()
    {
        var startup = new FakeStartup();
        var installer = NewInstaller(startup);
        installer.Install(_source, new InstallOptions(false, false, StartWithWindows: true));

        installer.RemoveShortcutsAndRegistration();

        Assert.Null(startup.Target);
    }

    [Fact]
    public void Versao_instalada_lida_do_registro_e_atualizada_depois_de_uma_atualizacao()
    {
        var installer = NewInstaller();
        Assert.Null(installer.InstalledVersion());
        installer.Install(_source, new InstallOptions(false, false, false));

        installer.RefreshRegistration(new Version(9, 9, 9));

        Assert.Equal(new Version(9, 9, 9), installer.InstalledVersion());
    }

    [Fact]
    public void Atualizar_o_registro_sem_estar_instalado_nao_cria_nada()
    {
        NewInstaller().RefreshRegistration(new Version(9, 9, 9));

        Assert.Null(Registry.CurrentUser.OpenSubKey(_testKey));
    }
}
