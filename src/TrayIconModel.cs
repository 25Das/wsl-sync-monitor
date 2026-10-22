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

    /// <summary>
    /// Texto ao passar o mouse no ícone: distro na primeira linha, depois "chave: valor" e, por último, se a
    /// distro está sendo mantida ligada. O Windows corta em 127 caracteres: se faltar espaço, sai a linha da
    /// versão nova (que também aparece no menu e no painel).
    /// </summary>
    public static string Tooltip(Snapshot? s, string? keptAlive, Version? update)
    {
        var lines = new List<string> { s?.Target?.Name ?? "WSL Sync Monitor" };
        if (s == null) lines.Add("Carregando…");
        else if (!s.WslInstalled) lines.Add("WSL: não encontrado");
        else if (!s.WslRunning) lines.Add("WSL: parado");
        else if (s.St != StState.Running) lines.Add("Syncthing: " + PanelHeader.Lower(s.StStatusText));
        else
        {
            lines.Add($"Pastas: {s.FoldersOk} de {s.Folders.Count} atualizadas");
            if (s.Devices.Count > 0) lines.Add($"Dispositivos: {s.DevicesOnline} de {s.Devices.Count} conectados");
        }
        string? updateLine = update != null ? $"Nova versão: {update.ToString(3)}" : null;
        string? keptLine = keptAlive != null ? $"Mantida ligada {keptAlive}" : null;

        var text = Join(lines, updateLine, keptLine);
        if (text.Length > Max) text = Join(lines, null, keptLine);
        if (text.Length <= Max) return text;
        // ainda longo (nome de distro enorme): encurta a primeira linha
        int excess = text.Length - Max + 1;
        lines[0] = lines[0][..Math.Max(1, lines[0].Length - excess)] + "…";
        return Join(lines, null, keptLine);
    }

    const int Max = 127;

    static string Join(List<string> lines, string? update, string? kept) =>
        string.Join("\n", lines.Concat(new[] { update, kept }.OfType<string>()));
}
