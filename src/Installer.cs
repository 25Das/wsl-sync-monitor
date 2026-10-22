namespace WslSyncMonitor;

public sealed record InstallOptions(bool DesktopShortcut, bool StartMenuShortcut, bool StartWithWindows);

public sealed class Installer
{
    public Installer(InstallPaths paths, string uninstallKey, Action<string?> startWithWindows) { }
    public void Install(string sourceExe, InstallOptions options) => throw new NotImplementedException();
    public void RemoveShortcutsAndRegistration() => throw new NotImplementedException();
}

public static class ShellLink
{
    public static void Create(string shortcut, string target, string description) => throw new NotImplementedException();
    public static string ReadTarget(string shortcut) => throw new NotImplementedException();
}
