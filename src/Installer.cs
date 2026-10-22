using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace WslSyncMonitor;

public sealed record InstallOptions(bool DesktopShortcut, bool StartMenuShortcut, bool StartWithWindows);

/// <summary>
/// Instalação por usuário, sem administrador: copia o .exe para <see cref="InstallPaths.Directory"/>, cria atalhos e
/// registra em "Aplicativos instalados" (com desinstalação). Caminhos e chave do registro são injetados para teste.
/// </summary>
public sealed class Installer(InstallPaths paths, string uninstallKey, Action<string?> startWithWindows)
{
    public InstallPaths Paths => paths;

    public static Installer ForCurrentUser() =>
        new(InstallPaths.Current, InstallPaths.UninstallKey, exe => Settings.SetStartWithWindows(exe));

    public void Install(string sourceExe, InstallOptions options)
    {
        Directory.CreateDirectory(paths.Directory);
        if (!paths.IsInstalledExe(sourceExe)) File.Copy(sourceExe, paths.Exe, overwrite: true);

        // respeita a escolha também numa reinstalação: atalho desmarcado é removido
        SetShortcut(paths.DesktopShortcut, options.DesktopShortcut);
        SetShortcut(paths.StartMenuShortcut, options.StartMenuShortcut);
        startWithWindows(options.StartWithWindows ? paths.Exe : null);

        using var key = Registry.CurrentUser.CreateSubKey(uninstallKey);
        foreach (var (name, value) in paths.UninstallEntry(Updater.Current, new FileInfo(paths.Exe).Length))
            key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
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
        startWithWindows(null);
        Registry.CurrentUser.DeleteSubKeyTree(uninstallKey, throwOnMissingSubKey: false);
    }

    /// <summary>Apaga a pasta de instalação alguns segundos depois que este processo fechar (o .exe em uso não pode ser apagado já).</summary>
    public void ScheduleDirectoryRemoval() =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 > nul & rmdir /s /q \"{paths.Directory}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
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
