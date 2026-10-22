namespace WslSyncMonitor;

public enum InstallKind { Fresh, Upgrade, Reinstall, InstalledIsNewer }

/// <summary>O que fazer ao abrir uma cópia fora da pasta de instalação, conforme a versão já instalada.</summary>
public static class InstallDecision
{
    public static InstallKind Decide(Version current, Version? installed)
    {
        if (installed == null) return InstallKind.Fresh;
        int cmp = Normalize(current).CompareTo(Normalize(installed));
        return cmp > 0 ? InstallKind.Upgrade : cmp == 0 ? InstallKind.Reinstall : InstallKind.InstalledIsNewer;
    }

    public static string Title(InstallKind kind, Version current, Version? installed) => kind switch
    {
        InstallKind.Upgrade => $"Atualizar a instalação ({installed!.ToString(3)} → {current.ToString(3)})",
        InstallKind.Reinstall => $"Reinstalar o WSL Sync Monitor {current.ToString(3)}",
        _ => "Instalar o WSL Sync Monitor",
    };

    // 1.9 == 1.9.0 == 1.9.0.0
    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
