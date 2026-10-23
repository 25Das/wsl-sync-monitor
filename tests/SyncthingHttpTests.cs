using System.Net.Http;
using System.Net.Security;
using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>A conexão com a API do Syncthing (a leitura completa e o fluxo de eventos usam a mesma).</summary>
public class SyncthingHttpTests
{
    [Fact]
    public void Nao_passa_pelo_proxy_do_sistema()
    {
        using var h = SyncthingHttp.CreateHandler();
        Assert.False(h.UseProxy); // o Syncthing é local (ou o IP do WSL): um proxy não o alcança
    }

    [Theory]
    [InlineData("https://127.0.0.1:8384", true)]
    [InlineData("https://localhost:8384", true)]
    [InlineData("https://172.24.10.2:8384", true)]   // IP do WSL
    [InlineData("https://192.168.0.10:8384", true)]
    [InlineData("https://example.com:8384", false)]
    [InlineData("https://8.8.8.8:8384", false)]
    public void Certificado_autoassinado_so_vale_para_enderecos_locais(string url, bool accepted)
    {
        using var h = SyncthingHttp.CreateHandler();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        Assert.Equal(accepted, h.ServerCertificateCustomValidationCallback!(req, null, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(h.ServerCertificateCustomValidationCallback(req, null, null, SslPolicyErrors.None));
    }
}
