namespace WslSyncMonitor;

public enum UpdateStatus { NotChecked, Checking, UpToDate, Available, Failed }

/// <summary>Estado da busca por versão nova do app (mostrado no cabeçalho do painel).</summary>
public sealed record UpdateState(UpdateStatus Status, Version? Version = null, DateTime? CheckedAt = null, string? Error = null)
{
    public static readonly UpdateState NotChecked = new(UpdateStatus.NotChecked);
    public static readonly UpdateState Checking = new(UpdateStatus.Checking);
    public static UpdateState UpToDate(DateTime at) => new(UpdateStatus.UpToDate, CheckedAt: at);
    public static UpdateState Found(Version v, DateTime at) => new(UpdateStatus.Available, v, at);
    public static UpdateState Failed(string error) => new(UpdateStatus.Failed, Error: error);
}

public enum HeaderAction { None, CheckUpdate, InstallUpdate }

/// <summary>
/// Cabeçalho do painel em duas linhas:
/// 1) título · versão do app (ou "Atualizar para X") · ficha técnica · pin;
/// 2) status (ou operação em andamento) · "Verificado às" · recarregar.
/// </summary>
public sealed record PanelHeader(
    string Status, Level StatusLevel, string Checked,
    string Version, string VersionTip, HeaderAction VersionAction, bool VersionHighlight)
{
    public const string Title = "WSL · Syncthing";
    public const string RefreshTip = "Verificar agora o WSL, o Syncthing e se há versão nova do app (F5)";

    public static PanelHeader For(Snapshot? s, string? busy, Version current, UpdateState update)
    {
        var (status, level) = busy != null ? (busy, Level.Busy) : StatusOf(s);
        string checkedAt = s == null ? "" : $"Verificado às {s.Time:HH:mm:ss}";
        string v = "v" + current.ToString(3);

        var (text, tip, action, highlight) = update.Status switch
        {
            UpdateStatus.Available => ($"Atualizar para {update.Version!.ToString(3)}",
                $"Baixar e instalar a versão {update.Version.ToString(3)} (atual: {current.ToString(3)}). O app reinicia sozinho.",
                HeaderAction.InstallUpdate, true),
            UpdateStatus.Checking => ($"{v} · verificando…", "Procurando versão nova do app…", HeaderAction.None, false),
            UpdateStatus.UpToDate => (v, $"Versão mais recente (verificado às {update.CheckedAt:HH:mm}). Clique para verificar de novo.",
                HeaderAction.CheckUpdate, false),
            UpdateStatus.Failed => ($"{v} · verificação falhou", $"Não foi possível verificar atualizações: {update.Error}. Clique para tentar de novo.",
                HeaderAction.CheckUpdate, false),
            _ => (v, "Clique para verificar se há versão nova do app.", HeaderAction.CheckUpdate, false),
        };
        if (busy != null) action = HeaderAction.None; // não troca o app no meio de uma operação
        return new(status, level, checkedAt, text, tip, action, highlight);
    }

    static (string, Level) StatusOf(Snapshot? s)
    {
        if (s == null) return ("Carregando…", Level.Off);
        if (!s.WslInstalled) return ("WSL não encontrado", Level.Error);
        if (s.Target == null && s.WslError != null) return (s.WslError, Level.Error); // sem distro, distro inexistente…
        if (!s.WslRunning) return ("WSL parado", Level.Off);
        if (s.St != StState.Running) return ("Syncthing: " + Lower(s.StStatusText), Level.Error);
        return s.Overall switch
        {
            Level.Error => ("Há erros em pastas ou dispositivos", Level.Error),
            Level.Busy => ("Sincronizando…", Level.Busy),
            Level.Warn when s.DevicesLevel == Level.Warn && !s.Folders.Any(f => f.Status.Level == Level.Warn) => ("Nenhum dispositivo conectado", Level.Warn),
            Level.Warn => ($"{s.FoldersOk} de {s.Folders.Count} pastas atualizadas", Level.Warn),
            _ => ("Tudo sincronizado", Level.Ok),
        };
    }

    internal static string Lower(string t) => t.Length == 0 ? t : char.ToLowerInvariant(t[0]) + t[1..];
}

/// <summary>
/// Quando avisar de versão nova: uma vez por versão e só se o resultado chegar com o painel e os menus fechados.
/// Quem buscou pelo painel ou pelo menu vê o resultado (e atualiza) ali mesmo; se fechou antes de a resposta
/// chegar, ou a busca foi a automática, a notificação é o único jeito de saber.
/// </summary>
public static class UpdateNotice
{
    public static bool ShouldNotify(Version? found, Version? alreadyNotified, bool uiOpen) =>
        !uiOpen && found != null && found != alreadyNotified;

    public static string MenuLabel(UpdateState u) => u.Status switch
    {
        UpdateStatus.Checking => "Verificando atualizações…",
        UpdateStatus.UpToDate => $"Verificar atualizações (em dia às {u.CheckedAt:HH:mm})",
        UpdateStatus.Failed => "Verificar atualizações (a última falhou)",
        UpdateStatus.Available => $"Atualizar para a versão {u.Version!.ToString(3)}",
        _ => "Verificar atualizações",
    };
}
