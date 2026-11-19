namespace WslSyncMonitor;

/// <summary>
/// Andamento da sequência segura de desligamento. O Windows pode perguntar de novo (só roda uma vez por
/// desligamento) e o desligamento pode ser cancelado no meio: aí a sequência é interrompida (<see cref="Token"/>)
/// e o próximo desligamento roda a sequência outra vez.
/// </summary>
public sealed class ShutdownProgress : IDisposable
{
    /// <summary>
    /// Quanto o Windows espera um app que segura o desligamento antes de cancelá-lo sozinho (visto nos logs: 62 s,
    /// sempre). Passado isso, a pessoa tenta de novo e acaba em "Desligar mesmo assim", sem proteção nenhuma.
    /// </summary>
    public static readonly TimeSpan WindowsPatience = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A espera pela sincronização ao desligar: a configurada, até 40 s, para sobrar tempo de parar o Syncthing e
    /// gravar os dados em disco antes de o Windows desistir.
    /// </summary>
    public static TimeSpan WaitBudget(int settingSeconds) =>
        TimeSpan.FromSeconds(Math.Clamp(settingSeconds, 0, 40));

    bool _running, _done, _canceled;
    CancellationTokenSource _cts = new();

    /// <summary>Interrompido quando o desligamento é cancelado durante a sequência.</summary>
    public CancellationToken Token => _cts.Token;

    public bool ShouldRun(bool needed) => needed && !_running && !_done;

    public void Started()
    {
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _running = true;
        _canceled = false;
    }

    /// <summary>A sequência terminou. true = foi cancelada no meio (quem chama desfaz o que precisar).</summary>
    public bool Finished()
    {
        _running = false;
        _done = !_canceled;
        return _canceled;
    }

    /// <summary>O desligamento foi cancelado. true = havia uma sequência rodando, agora interrompida.</summary>
    public bool Cancel()
    {
        _done = false;
        if (!_running) return false;
        _canceled = true;
        _cts.Cancel();
        return true;
    }

    public void Dispose() => _cts.Dispose();
}
