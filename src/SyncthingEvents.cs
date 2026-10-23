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
    /// cancelamento ou até o Syncthing parar de responder ou reiniciar (exceção: quem chama decide quando tentar de novo).
    /// <paramref name="onConnected"/> é chamado quando o fluxo está pronto (daí em diante, nada se perde).
    /// </summary>
    public async Task RunAsync(string baseUrl, string apiKey, Action<IReadOnlyList<JsonElement>> onEvents, CancellationToken ct,
        Action? onConnected = null)
    {
        string? started = await StartTimeAsync(baseUrl, apiKey, ct).ConfigureAwait(false);
        long since = (await GetAsync(baseUrl, apiKey, "/rest/events?limit=1&timeout=0", ct).ConfigureAwait(false))
            .Select(e => (long)SyncthingApi.Num(e, "id")).DefaultIfEmpty(0).Max();
        onConnected?.Invoke();
        while (!ct.IsCancellationRequested)
        {
            var batch = await GetAsync(baseUrl, apiKey, $"/rest/events?since={since}&timeout={(int)LongPoll.TotalSeconds}", ct)
                .ConfigureAwait(false);
            if (batch.Count > 0)
            {
                since = batch.Max(e => (long)SyncthingApi.Num(e, "id"));
                onEvents(batch);
            }
            else if (await StartTimeAsync(baseUrl, apiKey, ct).ConfigureAwait(false) != started)
            {
                // reiniciou entre dois pedidos: os números dos eventos recomeçaram (e podem já ter passado do último visto)
                throw new InvalidOperationException("o Syncthing reiniciou");
            }
        }
    }

    /// <summary>
    /// Espera o Syncthing responder (logo depois de ligar a distro ele ainda está subindo), conferindo a cada
    /// <paramref name="every"/> até <paramref name="max"/>. Usa /rest/noauth/health: não precisa da chave e não executa
    /// nada na distro, então pode ser frequente. true = respondeu.
    /// </summary>
    public async Task<bool> WaitUntilUpAsync(string baseUrl, TimeSpan every, TimeSpan max, CancellationToken ct)
    {
        var end = DateTime.Now + max;
        while (true)
        {
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                attempt.CancelAfter(TimeSpan.FromSeconds(2)); // endereço que não responde nem recusa
                try
                {
                    using var resp = await http.GetAsync(baseUrl + "/rest/noauth/health", attempt.Token).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode) return true;
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested)) { }
            }
            if (DateTime.Now + every > end) return false;
            await Task.Delay(every, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Espera antes de tentar de novo após <paramref name="failures"/> falhas seguidas: 2 s, 4 s, 8 s… até 1 min.</summary>
    public static TimeSpan RetryDelay(int failures) => TimeSpan.FromSeconds(Math.Min(60, 2 << Math.Min(Math.Max(failures, 1) - 1, 5)));

    /// <summary>Quando o Syncthing iniciou: muda se ele reiniciar.</summary>
    async Task<string?> StartTimeAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetStringAsync(baseUrl, apiKey, "/rest/system/status", ct).ConfigureAwait(false));
        return SyncthingApi.Str(doc.RootElement, "startTime");
    }

    async Task<List<JsonElement>> GetAsync(string baseUrl, string apiKey, string path, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetStringAsync(baseUrl, apiKey, path, ct).ConfigureAwait(false));
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList() : [];
    }

    async Task<string> GetStringAsync(string baseUrl, string apiKey, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        if (!string.IsNullOrEmpty(apiKey)) req.Headers.Add("X-API-Key", apiKey);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
