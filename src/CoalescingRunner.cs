namespace WslSyncMonitor;

/// <summary>
/// Executa um trabalho uma vez por vez (a leitura completa do estado). Pedido feito durante uma execução não se perde:
/// vira uma única execução logo depois (a que estava em andamento pode ter começado antes da mudança), e quem pediu
/// espera por ela. Continua no contexto de quem chamou (a interface).
/// </summary>
public sealed class CoalescingRunner(Func<Task> work)
{
    readonly object _lock = new();
    bool _running;
    TaskCompletionSource? _next;

    internal bool Running { get { lock (_lock) return _running; } }

    /// <summary>Executa agora, ou logo depois da execução em andamento (e espera por ela).</summary>
    public Task RunAsync()
    {
        lock (_lock)
        {
            if (_running) return (_next ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            _running = true;
        }
        return RunOnceAsync();
    }

    /// <summary>Executa agora se nada estiver em andamento (a execução em andamento serve; ex.: a leitura periódica).</summary>
    public Task RunIfIdleAsync()
    {
        lock (_lock)
        {
            if (_running) return Task.CompletedTask;
            _running = true;
        }
        return RunOnceAsync();
    }

    async Task RunOnceAsync()
    {
        try { await work(); }
        finally
        {
            TaskCompletionSource? next;
            lock (_lock)
            {
                next = _next;
                _next = null;
                _running = next != null; // continua "em execução" até a próxima começar
            }
            if (next != null) _ = FollowUpAsync(next);
        }
    }

    async Task FollowUpAsync(TaskCompletionSource waiter)
    {
        try
        {
            await RunOnceAsync();
            waiter.TrySetResult();
        }
        catch (Exception ex) { waiter.TrySetException(ex); }
    }
}
