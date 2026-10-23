using System.Text.Json;

namespace WslSyncMonitor;

/// <summary>
/// Fluxo de eventos do Syncthing (GET /rest/events, "long polling"): cada pedido fica aberto até haver eventos
/// novos (ou até o prazo), então eles chegam no instante em que acontecem, sem consultas repetidas.
/// <para>
/// Pede todos os tipos de evento: com o filtro <c>events=</c>, o Syncthing só guarda eventos a partir do primeiro
/// pedido filtrado, e eventos se perdem no começo (visto nos testes com o Syncthing real). Quem consome ignora os
/// tipos que não interessam.
/// </para>
/// </summary>
public sealed class SyncthingEvents(HttpClient http)
{
    /// <summary>Quanto cada pedido fica aberto esperando eventos (o HttpClient precisa de prazo maior).</summary>
    public static readonly TimeSpan LongPoll = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Entrega lotes de eventos a <paramref name="onEvents"/> a partir de agora (sem repetir os antigos), até o
    /// cancelamento ou até o Syncthing parar de responder (exceção: quem chama decide quando tentar de novo).
    /// </summary>
    public async Task RunAsync(string baseUrl, string apiKey, Action<IReadOnlyList<JsonElement>> onEvents, CancellationToken ct)
    {
        long since = await LatestIdAsync(baseUrl, apiKey, ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            var batch = await GetAsync(baseUrl, apiKey, $"/rest/events?since={since}&timeout={(int)LongPoll.TotalSeconds}", ct)
                .ConfigureAwait(false);
            if (batch.Count > 0)
            {
                since = batch.Max(e => (long)SyncthingApi.Num(e, "id"));
                onEvents(batch);
            }
            else if (await LatestIdAsync(baseUrl, apiKey, ct).ConfigureAwait(false) < since)
            {
                // o Syncthing reiniciou entre dois pedidos: os números dos eventos recomeçaram
                throw new InvalidOperationException("o Syncthing reiniciou");
            }
        }
    }

    /// <summary>Número do evento mais recente (o próximo pedido começa dele).</summary>
    async Task<long> LatestIdAsync(string baseUrl, string apiKey, CancellationToken ct) =>
        (await GetAsync(baseUrl, apiKey, "/rest/events?limit=1&timeout=0", ct).ConfigureAwait(false))
            .Select(e => (long)SyncthingApi.Num(e, "id")).DefaultIfEmpty(0).Max();

    async Task<List<JsonElement>> GetAsync(string baseUrl, string apiKey, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        if (!string.IsNullOrEmpty(apiKey)) req.Headers.Add("X-API-Key", apiKey);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList() : [];
    }
}
