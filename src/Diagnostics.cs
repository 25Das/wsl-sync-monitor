using System.Text;

namespace WslSyncMonitor;

/// <summary>
/// Informações técnicas para diagnóstico (menu → Informações técnicas). Nunca guarda a chave da API,
/// só de onde ela veio.
/// </summary>
public sealed record Diagnostics
{
    public string? AppVersion { get; init; }
    public string? WslVersion { get; init; }
    public string? Distro { get; init; }
    public string? DefaultDistro { get; init; }
    public string? GuiUrl { get; init; }
    public string? ApiKeySource { get; init; }
    public IReadOnlyList<string> ConfigFiles { get; init; } = [];
    public string? ConfigFileUsed { get; init; }
    public int? LastScriptExitCode { get; init; }
    public string? LastScriptError { get; init; }
    public string? LastApiError { get; init; }
    public string? Guard { get; init; }
    public int? ExternalSessions { get; init; }
    public string? SettingsPath { get; init; }
    public long? VmMemory { get; init; }
    public long? VhdSize { get; init; }
    public DiskSpace? Disk { get; init; }
    public string? LogTail { get; init; }
    public DateTime? Updated { get; init; }

    const int MaxErrorLength = 1000;

    public string ToText()
    {
        static string V(object? v) => v switch
        {
            null => "—",
            string s when string.IsNullOrWhiteSpace(s) => "—",
            _ => v.ToString()!,
        };
        static string Short(string? s) => s is { Length: > MaxErrorLength } ? s[..MaxErrorLength] + "…" : V(s);

        var sb = new StringBuilder();
        void Line(string label, object? value) => sb.Append(label).Append(": ").AppendLine(V(value));

        sb.AppendLine("WSL Sync Monitor - informações técnicas");
        Line("Versão do app", AppVersion);
        Line("Atualizado em", Updated?.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        Line("Configurações", SettingsPath);
        sb.AppendLine();
        sb.AppendLine("[WSL]");
        Line("Versão do WSL", WslVersion);
        Line("Distro monitorada", Distro);
        Line("Distro padrão", DefaultDistro);
        Line("Proteção", Guard);
        Line("Sessões de outros programas", ExternalSessions);
        Line("Memória do WSL", VmMemory is { } m ? Fmt.Bytes(m) : null);
        Line("Arquivo de disco da distro", VhdSize is { } v ? Fmt.Bytes(v) : null);
        Line("Espaço livre", Disk?.Free != null ? Disk.Describe() : null);
        sb.AppendLine();
        sb.AppendLine("[Syncthing]");
        Line("URL da interface", GuiUrl);
        Line("Origem da chave da API", ApiKeySource);
        Line("config.xml encontrados", ConfigFiles.Count == 0 ? null : string.Join(", ", ConfigFiles));
        Line("config.xml usado", ConfigFileUsed);
        Line("Último script na distro (código de saída)", LastScriptExitCode);
        sb.Append("Erro do último script: ").AppendLine(Short(LastScriptError));
        sb.Append("Último erro da API: ").AppendLine(Short(LastApiError));
        if (!string.IsNullOrWhiteSpace(LogTail))
        {
            sb.AppendLine();
            sb.AppendLine("[Log (final do arquivo mais recente)]");
            sb.AppendLine(LogTail);
        }
        return sb.ToString();
    }
}
