using System.Text.Json;

namespace WslSyncMonitor;

/// <summary>Release do GitHub com o WslSyncMonitor.exe (resposta de /releases/latest).</summary>
public sealed record UpdateInfo(Version Version, string PageUrl, string DownloadUrl, long Size)
{
    public const string AssetName = "WslSyncMonitor.exe";

    /// <summary>null se for rascunho, pré-release, sem o .exe, com tag fora do formato vX.Y.Z ou JSON inválido.</summary>
    public static UpdateInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.GetProperty("draft").GetBoolean() || r.GetProperty("prerelease").GetBoolean()) return null;
            if (!Version.TryParse(r.GetProperty("tag_name").GetString()?.TrimStart('v', 'V'), out var version)) return null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                if (a.GetProperty("name").GetString() != AssetName) continue;
                return new UpdateInfo(version, r.GetProperty("html_url").GetString() ?? "",
                    a.GetProperty("browser_download_url").GetString() ?? "", a.GetProperty("size").GetInt64());
            }
            return null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public bool IsNewerThan(Version current) => Normalize(Version) > Normalize(current);

    // 1.7 == 1.7.0 == 1.7.0.0
    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
