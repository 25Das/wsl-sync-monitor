namespace WslSyncMonitor;

public enum IconGlyph { Loading, Off, Sync, Ok, Warn, Error }

/// <summary>O que o ícone da bandeja mostra: símbolo, cor e marcas.</summary>
public sealed record TrayIconModel(IconGlyph Glyph, Level Level, bool KeptAlive, bool UpdatePending)
{
    public static TrayIconModel From(Snapshot? s, bool keptAlive, bool updatePending)
    {
        if (s == null) return new(IconGlyph.Loading, Level.Off, keptAlive, updatePending);
        if (!s.WslInstalled) return new(IconGlyph.Error, Level.Error, keptAlive, updatePending);
        if (!s.WslRunning) return new(IconGlyph.Off, Level.Off, keptAlive, updatePending);
        var level = s.Overall;
        var glyph = level switch
        {
            Level.Error => IconGlyph.Error,
            Level.Busy => IconGlyph.Sync,
            Level.Warn => IconGlyph.Warn,
            Level.Ok => IconGlyph.Ok,
            _ => IconGlyph.Off,
        };
        return new(glyph, level, keptAlive, updatePending);
    }

    /// <summary>Texto ao passar o mouse no ícone (o Windows corta em 127 caracteres).</summary>
    public static string Tooltip(Snapshot? s, string? keptAlive, Version? update)
    {
        var lines = new List<string> { s?.ShortSummary() ?? "WSL Sync Monitor" };
        if (s is { St: StState.Running, Devices.Count: > 0 })
            lines[0] = $"{s.Target?.Name} · {s.FoldersOk} de {s.Folders.Count} pastas atualizadas";
        if (s is { St: StState.Running, Devices.Count: > 0 })
            lines.Add($"{s.DevicesOnline} de {s.Devices.Count} dispositivos conectados");
        if (keptAlive != null) lines.Add($"Mantida ligada {keptAlive}");
        if (update != null) lines.Add($"Nova versão {update.ToString(3)} disponível");
        var text = string.Join("\n", lines);
        return text.Length <= 127 ? text : text[..126] + "…";
    }
}
