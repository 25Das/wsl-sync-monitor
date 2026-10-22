namespace WslSyncMonitor;

/// <summary>Onde o app se instala e cria atalhos, e o registro que o faz aparecer em "Aplicativos instalados".</summary>
public sealed record InstallPaths(string Directory, string Exe, string DesktopShortcut, string StartMenuShortcut)
{
    public const string AppName = "WSL Sync Monitor";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WslSyncMonitor";

    /// <summary>
    /// %LOCALAPPDATA%\Programs\WslSyncMonitor: por usuário, sem pedir administrador e com permissão de escrita
    /// (o atualizador troca o .exe ali).
    /// </summary>
    public static InstallPaths For(string localAppData, string desktop, string startMenuPrograms)
    {
        var dir = Join(localAppData, @"Programs\WslSyncMonitor");
        return new(dir, Join(dir, "WslSyncMonitor.exe"), Join(desktop, AppName + ".lnk"), Join(startMenuPrograms, AppName + ".lnk"));
    }

    // caminhos do Windows sempre com "\" (Path.Join usaria o separador do sistema em que roda, como nos testes no Linux)
    static string Join(string dir, string name) => dir.TrimEnd('\\', '/') + "\\" + name;

    public static InstallPaths Current => For(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs));

    /// <summary>O executável em uso é o instalado? (o Windows não diferencia maiúsculas em caminhos)</summary>
    public bool IsInstalledExe(string currentExe) => string.Equals(currentExe, Exe, StringComparison.OrdinalIgnoreCase);

    /// <summary>Valores da chave de desinstalação (HKCU\...\Uninstall\WslSyncMonitor).</summary>
    public Dictionary<string, object> UninstallEntry(Version version, long sizeBytes) => new()
    {
        ["DisplayName"] = AppName,
        ["DisplayVersion"] = version.ToString(3),
        ["Publisher"] = "25Das",
        ["InstallLocation"] = Directory,
        ["DisplayIcon"] = $"\"{Exe}\"",
        ["UninstallString"] = $"\"{Exe}\" --uninstall",
        ["URLInfoAbout"] = "https://github.com/25Das/wsl-sync-monitor",
        ["EstimatedSize"] = (int)(sizeBytes / 1024),
        ["NoModify"] = 1,
        ["NoRepair"] = 1,
    };
}
