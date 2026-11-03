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

    // conexão reaproveitada que morreu (depois de suspender, por exemplo): o primeiro pedido falha, o seguinte funciona.
    // Sem tentar de novo, isso virava "Sem resposta da API" no log e no ícone por uma fração de segundo.

    [Fact]
    public async Task Falha_de_conexao_tenta_de_novo_uma_vez()
    {
        int calls = 0;
        var r = await SyncthingHttp.RetryOnceOnConnectionErrorAsync(() =>
            ++calls == 1 ? throw new HttpRequestException("An error occurred while sending the request.") : Task.FromResult(42));
        Assert.Equal(42, r);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Falha_de_novo_vale_a_segunda()
    {
        int calls = 0;
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => SyncthingHttp.RetryOnceOnConnectionErrorAsync<int>(() =>
            throw new HttpRequestException("falha " + ++calls)));
        Assert.Equal("falha 2", ex.Message);
    }

    [Fact]
    public async Task Resposta_de_erro_do_servidor_nao_tenta_de_novo()
    {
        // o Syncthing respondeu (chave recusada, erro interno): repetir não muda nada
        int calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => SyncthingHttp.RetryOnceOnConnectionErrorAsync<int>(() =>
        {
            calls++;
            throw new HttpRequestException("HTTP 403", null, System.Net.HttpStatusCode.Forbidden);
        }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Prazo_esgotado_nao_tenta_de_novo()
    {
        // um Syncthing travado não responde a nenhum dos dois: repetir só dobraria a espera
        int calls = 0;
        await Assert.ThrowsAsync<TaskCanceledException>(() => SyncthingHttp.RetryOnceOnConnectionErrorAsync<int>(() =>
        {
            calls++;
            throw new TaskCanceledException();
        }));
        Assert.Equal(1, calls);
    }
}
