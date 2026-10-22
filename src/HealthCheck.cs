namespace WslSyncMonitor;

public sealed record HealthIssue(Level Level, string Text);

/// <summary>
/// Verificação de pastas e dispositivos (ao iniciar e pelo menu): só o que merece atenção. Considera o que está
/// visível no painel (o que o usuário ocultou foi escolha dele). Desligado há pouco é normal.
/// </summary>
public static class HealthCheck
{
    /// <summary>
    /// O Syncthing está pronto para ser avaliado: respondendo e já com tempo de conectar aos dispositivos
    /// (ou com todos conectados), senão "nenhum dispositivo conectado" seria um falso aviso logo após ligar.
    /// </summary>
    public static bool Ready(Snapshot s, int graceSeconds) =>
        s.St == StState.Running &&
        (s.StUptime.TotalSeconds >= graceSeconds || s.AllDevices.Where(d => !d.Paused).All(d => d.Connected));

    public static IReadOnlyList<HealthIssue> Find(Snapshot s, DateTime now, int staleDays)
    {
        var issues = new List<HealthIssue>();
        foreach (var f in s.Folders)
        {
            if (f.Paused) { issues.Add(new(Level.Warn, $"pasta {f.Label} pausada")); continue; }
            var (text, level) = f.Status;
            if (level == Level.Error) issues.Add(new(Level.Error, $"pasta {f.Label}: {text}"));
            else if (level == Level.Warn) issues.Add(new(Level.Warn, $"pasta {f.Label} fora de sincronia"));
        }

        var active = s.Devices.Where(d => !d.Paused).ToList();
        foreach (var d in active.Where(d => !d.Connected))
        {
            if (d.LastSeen is not { Year: > 1970 } seen) issues.Add(new(Level.Warn, $"{d.Name} nunca conectou"));
            else if ((now - seen).TotalDays >= staleDays) issues.Add(new(Level.Warn, $"{d.Name} não conecta há {(int)(now - seen).TotalDays} dias"));
        }
        foreach (var d in s.Devices.Where(d => d.Paused)) issues.Add(new(Level.Warn, $"dispositivo {d.Name} pausado"));
        if (s.DevicesLevel == Level.Warn) issues.Add(new(Level.Warn, "nenhum dispositivo conectado"));
        return issues;
    }

    /// <summary>Texto da notificação: até três avisos e quantos mais.</summary>
    public static string Summary(IReadOnlyList<HealthIssue> issues) =>
        string.Join("\n", issues.Take(3).Select(i => i.Text)) + (issues.Count > 3 ? $"\ne mais {issues.Count - 3}" : "");
}
