using System.Text.Json;
using Microsoft.Win32;

namespace WslSyncMonitor;

/// <summary>Configurações persistidas em %APPDATA%\WslSyncMonitor\settings.json.</summary>
public sealed class Settings
{
    /// <summary>Distro do WSL onde o Syncthing roda. Vazio = distro padrão.</summary>
    public string Distro { get; set; } = "";

    /// <summary>URL da interface do Syncthing. Vazio = detectar pelo config.xml (fallback http://127.0.0.1:8384).</summary>
    public string SyncthingUrl { get; set; } = "";

    /// <summary>API key do Syncthing. Vazio = ler automaticamente do config.xml dentro do WSL.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Caminho do config.xml dentro do WSL. Vazio = locais padrão.</summary>
    public string SyncthingConfigPath { get; set; } = "";

    public int RefreshSeconds { get; set; } = 5;

    /// <summary>Oculta pastas que não estão compartilhadas com nenhum dispositivo (ex.: "Default Folder" sem uso).</summary>
    public bool HideUnsharedFolders { get; set; } = true;

    /// <summary>Oculta pastas pausadas no Syncthing.</summary>
    public bool HidePausedFolders { get; set; } = false;

    /// <summary>IDs de pastas ocultadas manualmente.</summary>
    public List<string> HiddenFolders { get; set; } = new();

    /// <summary>IDs de dispositivos ocultados do painel (menu do dispositivo → Mostrar no painel).</summary>
    public List<string> HiddenDevices { get; set; } = new();

    public bool IsDeviceHidden(DeviceInfo d) => HiddenDevices.Contains(d.Id);

    public bool IsFolderHidden(FolderInfo f) =>
        HiddenFolders.Contains(f.Id) || (HideUnsharedFolders && f.SharedWith == 0) || (HidePausedFolders && f.Paused);

    /// <summary>
    /// Mantém a distro monitorada ligada até o Syncthing terminar de sincronizar; só então a solta para o
    /// desligamento por ociosidade do WSL agir. Também vale ao sair do app e ao desligar o Windows.
    /// </summary>
    public bool ProtectSync { get; set; } = true;

    /// <summary>Tempo máximo segurando o WSL esperando a sincronização (depois solta mesmo assim).</summary>
    public int MaxHoldMinutes { get; set; } = 10;

    /// <summary>Tempo máximo esperando a sincronização ao desligar o Windows (depois encerra com segurança mesmo assim).</summary>
    public int ShutdownMaxWaitSeconds { get; set; } = 120;

    /// <summary>Após ligar, tempo para o Syncthing conectar e trocar índices antes de considerar "sincronizado".</summary>
    public int StartupGraceSeconds { get; set; } = 90;

    /// <summary>Por quanto tempo o estado "sincronizado" precisa se manter antes de soltar o WSL.</summary>
    public int StableSeconds { get; set; } = 10;

    /// <summary>Distro mantida ligada "até você desligar" (menu da distro → Manter ligada). Vazio = nenhuma.</summary>
    public string KeepAliveDistro { get; set; } = "";

    /// <summary>Liga o WSL a cada N minutos (desligado há N min) só para sincronizar. 0 = desativado.</summary>
    public int PeriodicSyncMinutes { get; set; } = 60;

    /// <summary>Não perguntar mais se deve se instalar ao rodar fora da pasta de instalação.</summary>
    public bool InstallPromptDismissed { get; set; }

    /// <summary>Mostra o mini‑widget sobre a barra de tarefas (além do ícone na bandeja).</summary>
    public bool ShowTaskbarWidget { get; set; } = true;

    /// <summary>Deslocamento extra (px) do widget em relação à área de notificação. Ajustável arrastando o widget.</summary>
    public int WidgetOffset { get; set; } = 0;

    /// <summary>Mostra o painel detalhado ao passar o mouse (além do clique).</summary>
    public bool HoverPopup { get; set; } = false;

    /// <summary>Versão do formato do arquivo (para migrar padrões alterados).</summary>
    public int SettingsVersion { get; set; }
    const int CurrentVersion = 2;

    public int HoverDelayMs { get; set; } = 450;

    // ----------------------------------------------------------------------

    /// <summary>
    /// Pasta de dados (configurações e logs). A variável de ambiente WSLSYNCMONITOR_HOME troca a pasta: os testes
    /// de ponta a ponta usam uma pasta temporária para não tocar nas configurações reais.
    /// </summary>
    public static string Dir => Environment.GetEnvironmentVariable("WSLSYNCMONITOR_HOME") is { Length: > 0 } home
        ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WslSyncMonitor");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // o arquivo é editado à mão: tolera comentários e vírgula sobrando
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Lê o arquivo (cria com os padrões se não existir). Se ele for inválido ou estiver sendo gravado,
    /// retorna null com o motivo em <paramref name="error"/> e <b>não</b> sobrescreve nada.
    /// </summary>
    public static Settings? TryLoad(out string? error)
    {
        error = null;
        if (!File.Exists(FilePath))
        {
            var s = new Settings { SettingsVersion = CurrentVersion };
            s.Save();
            return s;
        }
        try
        {
            var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOpts) ?? new Settings();
            if (loaded.SettingsVersion < 2) // v2: hover desligado por padrão
            {
                loaded.HoverPopup = false;
                loaded.SettingsVersion = CurrentVersion;
                loaded.Save();
            }
            return loaded;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn("configurações", "settings.json inválido: " + ex.Message);
            return null;
        }
    }

    /// <summary>Para a inicialização: se o arquivo for inválido, guarda uma cópia dele e usa os padrões.</summary>
    public static Settings LoadOrDefault(out string? error)
    {
        var s = TryLoad(out error);
        if (s != null) return s;
        try { File.Copy(FilePath, FilePath + ".invalido", overwrite: true); } catch { /* sem o arquivo, nada a guardar */ }
        return new Settings { SettingsVersion = CurrentVersion };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex) { Log.Warn("configurações", "salvar settings.json", ex); }
    }

    // ---- Iniciar com o Windows ----

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "WslSyncMonitor";

    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string;
        }
        set => SetStartWithWindows(value ? Environment.ProcessPath : null);
    }

    /// <summary>Qual .exe inicia com o Windows (sem aspas), ou null.</summary>
    public static string? StartWithWindowsTarget
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return (k?.GetValue(RunName) as string)?.Trim('"');
        }
    }

    /// <summary>Inicia com o Windows o .exe indicado (null = não iniciar).</summary>
    public static void SetStartWithWindows(string? exe)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (exe != null) k.SetValue(RunName, $"\"{exe}\"");
        else k.DeleteValue(RunName, false);
    }
}
