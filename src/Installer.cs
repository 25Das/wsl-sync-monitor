using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace WslSyncMonitor;

public sealed record InstallOptions(bool DesktopShortcut, bool StartMenuShortcut, bool StartWithWindows);

/// <summary>Início com o Windows: qual .exe inicia (null = nenhum). Injetável para teste.</summary>
public interface IStartup
{
    string? Get();
    void Set(string? exe);
}

/// <summary>Início com o Windows pelo registro (HKCU\...\Run).</summary>
public sealed class WindowsStartup : IStartup
{
    public string? Get() => Settings.StartWithWindowsTarget;
    public void Set(string? exe) => Settings.SetStartWithWindows(exe);
}

/// <summary>
/// Instalação por usuário, sem administrador: copia o .exe para <see cref="InstallPaths.Directory"/>, cria atalhos e
/// registra em "Aplicativos instalados" (com desinstalação). Caminhos e chave do registro são injetados para teste.
/// </summary>
public sealed class Installer(InstallPaths paths, string uninstallKey, IStartup startup)
{
    public InstallPaths Paths => paths;

    public static Installer ForCurrentUser() => new(InstallPaths.Current, InstallPaths.UninstallKey, new WindowsStartup());

    /// <summary>Versão registrada em "Aplicativos instalados" (null = não instalado).</summary>
    public Version? InstalledVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(uninstallKey);
        return Version.TryParse(key?.GetValue("DisplayVersion") as string, out var v) && File.Exists(paths.Exe) ? v : null;
    }

    /// <summary>Depois de uma atualização: corrige versão e tamanho em "Aplicativos instalados" (só se instalado).</summary>
    public void RefreshRegistration(Version version)
    {
        using var key = Registry.CurrentUser.OpenSubKey(uninstallKey, writable: true);
        if (key == null || !File.Exists(paths.Exe)) return;
        WriteEntry(key, version);
    }

    void WriteEntry(RegistryKey key, Version version)
    {
        foreach (var (name, value) in paths.UninstallEntry(version, new FileInfo(paths.Exe).Length))
            key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
    }

    bool StartupIsThisCopy => string.Equals(startup.Get(), paths.Exe, StringComparison.OrdinalIgnoreCase);

    public void Install(string sourceExe, InstallOptions options)
    {
        Directory.CreateDirectory(paths.Directory);
        if (!paths.IsInstalledExe(sourceExe)) File.Copy(sourceExe, paths.Exe, overwrite: true);

        // respeita a escolha também numa reinstalação: atalho desmarcado é removido
        SetShortcut(paths.DesktopShortcut, options.DesktopShortcut);
        SetShortcut(paths.StartMenuShortcut, options.StartMenuShortcut);
        // marcado: a cópia instalada passa a iniciar com o Windows (substitui uma cópia avulsa anterior);
        // desmarcado: só desliga se quem inicia é a cópia instalada
        if (options.StartWithWindows) startup.Set(paths.Exe);
        else if (StartupIsThisCopy) startup.Set(null);

        using var key = Registry.CurrentUser.CreateSubKey(uninstallKey);
        WriteEntry(key, Updater.Current);
    }

    void SetShortcut(string shortcut, bool wanted)
    {
        if (wanted) ShellLink.Create(shortcut, paths.Exe, "Monitor do WSL e do Syncthing");
        else if (File.Exists(shortcut)) File.Delete(shortcut);
    }

    /// <summary>Remove atalhos, início automático e o registro. O .exe (talvez em uso) é apagado depois.</summary>
    public void RemoveShortcutsAndRegistration()
    {
        foreach (var s in new[] { paths.DesktopShortcut, paths.StartMenuShortcut })
            if (File.Exists(s)) File.Delete(s);
        if (StartupIsThisCopy) startup.Set(null); // não mexe no início automático de outra cópia
        Registry.CurrentUser.DeleteSubKeyTree(uninstallKey, throwOnMissingSubKey: false);
    }

    /// <summary>
    /// Apaga a pasta de instalação depois que este processo terminar (o .exe em uso não pode ser apagado antes).
    /// Roda a partir da pasta temporária: uma pasta não pode ser apagada por quem está dentro dela.
    /// </summary>
    public void ScheduleDirectoryRemoval()
    {
        var dir = paths.Directory.Replace("'", "''");
        var script = $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1; " +
                     $"Remove-Item -LiteralPath '{dir}' -Recurse -Force -ErrorAction SilentlyContinue";
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        });
    }
}

/// <summary>Atalhos do Windows (.lnk) pela interface COM do próprio Windows (IShellLink).</summary>
public static class ShellLink
{
    public static void Create(string shortcut, string target, string description)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
            ((IPersistFile)link).Save(shortcut, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    public static string ReadTarget(string shortcut)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            ((IPersistFile)link).Load(shortcut, 0);
            var path = new StringBuilder(1024);
            link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            return path.ToString();
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, int flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
