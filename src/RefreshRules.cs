namespace WslSyncMonitor;

/// <summary>De quanto em quanto tempo o estado completo (WSL e Syncthing) é lido.</summary>
public static class RefreshRules
{
    /// <summary>
    /// O intervalo configurado, ou o triplo (no mínimo 15 s) quando nada depende dele: o fluxo de eventos traz as
    /// mudanças do Syncthing (e, se ele parar, o fluxo cai e a leitura é feita na hora), o painel está fechado (não
    /// mostra velocidades) e a proteção não está contando leituras seguidas sem sessões.
    /// </summary>
    public static TimeSpan Interval(int baseSeconds, bool eventsLive, bool panelOpen, bool guardCounting)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(2, baseSeconds));
        return eventsLive && !panelOpen && !guardCounting ? TimeSpan.FromSeconds(Math.Max(15, 3 * interval.TotalSeconds)) : interval;
    }
}
