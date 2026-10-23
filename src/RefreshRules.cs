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

    /// <summary>
    /// Vale sondar o Syncthing a cada segundo (ele está para responder): a distro acabou de ligar, ou ele acabou de
    /// parar (reiniciando), ou o app acabou de abrir, e ele ainda não responde. Quem continua sem Syncthing não é sondado
    /// de novo (talvez nem use o Syncthing nesta distro).
    /// </summary>
    public static bool ShouldProbe(Snapshot? before, Snapshot after)
    {
        if (after.Target is not { Running: true } || after.St is not (StState.NotRunning or StState.Unreachable)) return false;
        return before == null || before.Target is not { Running: true } || before.St == StState.Running;
    }
}
