namespace WslSyncMonitor;

/// <summary>
/// O que o widget da barra mostra, nas duas orientações: pontos do WSL e do Syncthing e os contadores de pastas
/// (sem problemas / total) e dispositivos (conectados / total), cada um com a cor do estado. Os números são os mesmos
/// da dica do ícone e do painel; a cor diz o que está acontecendo (sincronizar não tira uma pasta da conta).
/// </summary>
public sealed record WidgetModel(Level Wsl, Level Syncthing, string Folders, Level FoldersLevel, string Devices, Level DevicesLevel)
{
    const string None = "–";

    /// <param name="busy">Operação em andamento (iniciar/encerrar WSL): o ponto do WSL fica azul.</param>
    public static WidgetModel From(Snapshot? s, bool busy)
    {
        var wsl = busy ? Level.Busy : s?.WslLevel ?? Level.Off;
        var st = s?.StLevel ?? Level.Off;
        if (s is not { St: StState.Running }) return new(wsl, st, None, Level.Off, None, Level.Off);
        return new(wsl, st,
            $"{s.FoldersHealthy}/{s.Folders.Count}", s.FoldersLevel,
            $"{s.DevicesOnline}/{s.Devices.Count}", s.DevicesLevel);
    }
}
