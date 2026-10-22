namespace WslSyncMonitor;

/// <summary>
/// Espaço livre real da distro. O disco dela é virtual (cresce até ~1 TB), então o que sobra de fato é o menor
/// entre o livre dentro da distro ("df") e o livre na unidade do Windows onde fica o arquivo .vhdx.
/// </summary>
public sealed record DiskSpace(long? Free, string Limit)
{
    public static DiskSpace Effective(long? distroFree, long? hostFree, string? hostDrive) => (distroFree, hostFree) switch
    {
        (null, null) => new(null, ""),
        (null, { } h) => new(h, $"na unidade {hostDrive}"),
        ({ } d, null) => new(d, "no disco da distro"),
        ({ } d, { } h) when h <= d => new(h, $"(limitado pela unidade {hostDrive})"),
        ({ } d, _) => new(d, "(limitado pelo disco da distro)"),
    };

    public string Describe() => Free is { } f ? $"{Fmt.Bytes(f)} livres {Limit}" : "—";
}
