namespace WslSyncMonitor;

/// <summary>
/// Uma instância por usuário (mutex) e o pedido para a instância em execução fechar (evento nomeado),
/// usado ao instalar e ao desinstalar.
/// </summary>
public static class AppInstance
{
    const string MutexName = @"Local\WslSyncMonitor.SingleInstance";
    const string ExitEventName = @"Local\WslSyncMonitor.Exit";

    public static Mutex CreateMutex() => new(false, MutexName);

    /// <summary>Tenta ser a instância única, esperando até <paramref name="wait"/> a anterior fechar.</summary>
    public static bool TryAcquire(Mutex mutex, TimeSpan wait)
    {
        try { return mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; } // a anterior fechou sem liberar: assume
    }

    /// <summary>Evento que a instância em execução escuta; ao ser sinalizado, ela fecha (com a saída segura).</summary>
    public static EventWaitHandle CreateExitEvent() => new(false, EventResetMode.AutoReset, ExitEventName);

    /// <summary>Pede à instância em execução para fechar e espera ela liberar o mutex. false = não fechou a tempo.</summary>
    public static bool RequestExitAndWait(Mutex mutex, TimeSpan wait)
    {
        if (TryAcquire(mutex, TimeSpan.Zero)) return true; // nenhuma em execução
        if (EventWaitHandle.TryOpenExisting(ExitEventName, out var ev))
            using (ev) ev.Set();
        return TryAcquire(mutex, wait);
    }
}
