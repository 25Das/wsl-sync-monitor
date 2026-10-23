using System.Net;
using System.Net.Http;
using System.Text;
using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>O fluxo de eventos sem um Syncthing de verdade: reinício entre pedidos e o intervalo entre tentativas.</summary>
public class SyncthingEventsRulesTests
{
    /// <summary>Um Syncthing de mentira: sem eventos novos, e com o horário de início que <paramref name="startTime"/> der.</summary>
    sealed class FakeSyncthing(Func<int, string> startTime) : HttpMessageHandler
    {
        int _status;
        public volatile int Polls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            await Task.Delay(1, ct); // como a rede: nunca responde na mesma hora
            var path = req.RequestUri!.PathAndQuery;
            string body = path switch
            {
                "/rest/system/status" => $$"""{"myID":"X","startTime":"{{startTime(_status++)}}"}""",
                "/rest/events?limit=1&timeout=0" => """[{"id":500,"type":"Ping","data":{}}]""",
                _ when path.StartsWith("/rest/events?since=500", StringComparison.Ordinal) => Poll(),
                _ => throw new InvalidOperationException(path),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
        }

        string Poll() { Polls++; return "[]"; } // Syncthing respondeu "nada novo" ao fim do prazo
    }

    [Fact]
    public async Task Percebe_que_o_Syncthing_reiniciou_mesmo_sem_numeros_de_evento_menores()
    {
        // reiniciou entre dois pedidos e os números novos já passaram do último visto: só o horário de início denuncia
        using var http = new HttpClient(new FakeSyncthing(n => n == 0 ? "2026-10-23T10:00:00Z" : "2026-10-23T10:05:00Z"));
        var run = new SyncthingEvents(http).RunAsync("http://st", "", _ => { }, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("reiniciou", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sem_reinicio_continua_esperando()
    {
        var fake = new FakeSyncthing(_ => "2026-10-23T10:00:00Z");
        using var http = new HttpClient(fake);
        using var cts = new CancellationTokenSource();
        var run = new SyncthingEvents(http).RunAsync("http://st", "", _ => { }, cts.Token);
        while (fake.Polls < 3) await Task.Delay(10);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Avisa_quando_conectou()
    {
        var fake = new FakeSyncthing(_ => "2026-10-23T10:00:00Z");
        using var http = new HttpClient(fake);
        using var cts = new CancellationTokenSource();
        using var connected = new SemaphoreSlim(0);
        var run = new SyncthingEvents(http).RunAsync("http://st", "", _ => { }, cts.Token, onConnected: () => connected.Release());
        Assert.True(await connected.WaitAsync(TimeSpan.FromSeconds(5)));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(50, 60)]
    public void Tentativas_seguidas_esperam_cada_vez_mais_ate_um_minuto(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), SyncthingEvents.RetryDelay(failures));
}
