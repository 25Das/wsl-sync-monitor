using System.Text.Json;

namespace WslSyncMonitor;

/// <summary>Leitura de respostas da API do Syncthing e de comandos da distro.</summary>
public static class SyncthingParsers
{
    /// <summary>GET /rest/system/error.</summary>
    public static List<(DateTimeOffset When, string Message)> Errors(string json)
    {
        var list = new List<(DateTimeOffset, string)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                foreach (var e in errors.EnumerateArray())
                    list.Add((e.GetProperty("when").GetDateTimeOffset(), e.GetProperty("message").GetString() ?? ""));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        return list;
    }

    /// <summary>Saída de "df -B1 --output=size,avail /": (total, livre) em bytes.</summary>
    public static (long Total, long Free)? Df(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && long.TryParse(parts[0], out var total) && long.TryParse(parts[1], out var free))
                return (total, free);
        }
        return null;
    }
}
