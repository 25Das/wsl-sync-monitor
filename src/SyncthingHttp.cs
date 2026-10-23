using System.Net;
using System.Net.Security;

namespace WslSyncMonitor;

/// <summary>A conexão com a API do Syncthing: a leitura completa e o fluxo de eventos usam a mesma configuração.</summary>
public static class SyncthingHttp
{
    /// <summary>Cliente para a API, com o prazo de cada pedido.</summary>
    public static HttpClient Create(TimeSpan timeout) => new(CreateHandler()) { Timeout = timeout };

    /// <summary>Sem proxy (o Syncthing é local ou está no IP do WSL); aceita certificado autoassinado só nesses endereços.</summary>
    public static HttpClientHandler CreateHandler() => new()
    {
        // GUI com HTTPS usa certificado autoassinado; só aceitamos para endereços locais/WSL.
        ServerCertificateCustomValidationCallback = (req, _, _, errors) => errors == SslPolicyErrors.None || IsLocal(req.RequestUri),
        UseProxy = false,
    };

    static bool IsLocal(Uri? u) => u != null && (u.IsLoopback || (IPAddress.TryParse(u.Host, out var ip) && IsPrivate(ip)));

    static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168));
    }
}
