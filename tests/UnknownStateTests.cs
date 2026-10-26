using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// Quando não dá para saber o estado real (leitura que falhou, estado que o app não conhece), o app mostra
/// "desconhecido" (cinza, ponto vazado, "?" no ícone) em vez de afirmar parado, erro ou atualizado.
/// </summary>
public class UnknownStateTests
{
    static readonly Version V = new(1, 0, 0);

    static Snapshot Running(params FolderInfo[] folders)
    {
        var s = new Snapshot { Target = new DistroInfo("Ubuntu", true, 2, true), St = StState.Running };
        s.Distros.Add(s.Target);
        s.Folders.AddRange(folders);
        s.AllFolders.AddRange(folders);
        return s;
    }

    static PanelHeader Header(Snapshot s) => PanelHeader.For(s, null, V, UpdateState.NotChecked);

    // ---- lista de distros que não pôde ser lida ----

    static Snapshot ListFailed(string? configured = null)
    {
        var s = new Snapshot();
        ReadFailures.DistroList(s, "wsl.exe não respondeu a tempo");
        s.ResolveTarget(configured);
        return s;
    }

    [Fact]
    public void Lista_de_distros_que_falhou_e_desconhecida_nao_parada_nem_erro()
    {
        var s = ListFailed();
        Assert.Equal((Level.Unknown, Level.Unknown, Level.Unknown), (s.WslLevel, s.StLevel, s.Overall));
        var icon = TrayIconModel.From(s, false, false);
        Assert.Equal((IconGlyph.Unknown, Level.Unknown), (icon.Glyph, icon.Level));
        Assert.Equal("WSL Sync Monitor\nWSL: estado desconhecido (wsl.exe não respondeu a tempo)", TrayIconModel.Tooltip(s, null, null));
        var h = Header(s);
        Assert.Equal(("Estado do WSL desconhecido: wsl.exe não respondeu a tempo", Level.Unknown), (h.Status, h.StatusLevel));
        var w = WidgetModel.From(s, false);
        Assert.Equal((Level.Unknown, Level.Unknown, "–"), (w.Wsl, w.Syncthing, w.Folders));
    }

    [Fact]
    public void Lista_que_falhou_nao_diz_que_a_distro_configurada_nao_existe()
    {
        var s = ListFailed("Debian");
        Assert.Equal("wsl.exe não respondeu a tempo", s.WslError);
        Assert.Equal(Level.Unknown, s.Overall);
    }

    [Fact]
    public void Lista_lida_define_a_distro_e_os_erros_conhecidos()
    {
        var s = new Snapshot();
        s.Distros.Add(new DistroInfo("Ubuntu", false, 2, true));
        s.ResolveTarget("Debian");
        Assert.Equal(("Distro \"Debian\" não encontrada", Level.Error), (s.WslError, s.Overall));
        Assert.Equal(Level.Off, s.StLevel); // sem distro, o Syncthing certamente não roda nela

        var none = new Snapshot();
        none.ResolveTarget(null);
        Assert.Equal(("Nenhuma distro instalada", Level.Error), (none.WslError, none.Overall));

        var ok = new Snapshot();
        ok.Distros.Add(new DistroInfo("Debian", false, 2, false));
        ok.Distros.Add(new DistroInfo("Ubuntu", false, 2, true));
        ok.ResolveTarget(null);
        Assert.Equal(("Ubuntu", Level.Off, StState.DistroStopped), (ok.Target?.Name, ok.Overall, ok.St));
        ok.ResolveTarget("debian");
        Assert.Equal("Debian", ok.Target?.Name);
    }

    // ---- Syncthing que não responde ----

    [Fact]
    public void API_sem_resposta_sem_saber_se_o_processo_roda_e_desconhecido()
    {
        var (st, text) = ReadFailures.ApiUnreachable(processRunning: null, "http://127.0.0.1:8384", "timeout");
        Assert.Equal(StState.Unknown, st);
        Assert.Equal("http://127.0.0.1:8384 não responde (timeout); não foi possível saber se o Syncthing está rodando.", text);
        var s = Running(); s.St = st;
        Assert.Equal((Level.Unknown, Level.Unknown), (s.StLevel, s.Overall));
        Assert.Equal(("Syncthing: estado desconhecido", Level.Unknown), (Header(s).Status, Header(s).StatusLevel));
        Assert.Equal(IconGlyph.Unknown, TrayIconModel.From(s, false, false).Glyph);
    }

    [Fact]
    public void API_sem_resposta_com_o_processo_conhecido_continua_parado_ou_erro()
    {
        Assert.Equal(StState.NotRunning, ReadFailures.ApiUnreachable(false, "u", "e").State);
        Assert.Equal(StState.Unreachable, ReadFailures.ApiUnreachable(true, "u", "e").State);
    }

    // ---- pasta e dispositivo cuja leitura falhou ----

    [Fact]
    public void Pasta_que_nao_pode_ser_lida_e_desconhecida_e_nao_conta_como_sem_problemas()
    {
        var f = new FolderInfo { State = "idle" };
        ReadFailures.Folder(f, "HTTP 500 em /rest/db/status");
        Assert.Equal(("Estado desconhecido (HTTP 500 em /rest/db/status)", Level.Unknown), f.Status);

        var s = Running(new FolderInfo { State = "idle" }, f);
        Assert.Equal(("1/2", Level.Unknown), (WidgetModel.From(s, false).Folders, s.FoldersLevel));
        Assert.Equal(("Estado desconhecido de 1 pasta", Level.Unknown), (Header(s).Status, Header(s).StatusLevel));
        Assert.Equal(IconGlyph.Unknown, TrayIconModel.From(s, false, false).Glyph);
    }

    [Fact]
    public void Estado_de_pasta_que_o_app_nao_conhece_e_desconhecido()
    {
        Assert.Equal(("estado-novo", Level.Unknown), new FolderInfo { State = "estado-novo" }.Status);
    }

    [Fact]
    public void Dispositivo_conectado_cujo_andamento_nao_pode_ser_lido_nao_aparece_como_atualizado()
    {
        var d = new DeviceInfo { Connected = true };
        ReadFailures.Device(d);
        Assert.Equal(("Conectado · andamento desconhecido", Level.Unknown), d.Status);
        d.Completion = 40; // os eventos trouxeram andamento: sabe-se que sincroniza
        Assert.Equal(Level.Busy, d.Status.Level);

        var s = Running(new FolderInfo { State = "idle" });
        var unknown = new DeviceInfo { Connected = true };
        ReadFailures.Device(unknown);
        s.Devices.Add(unknown); s.AllDevices.Add(unknown);
        Assert.Equal((Level.Unknown, Level.Unknown), (s.DevicesLevel, s.Overall));
        Assert.Equal("1/1", WidgetModel.From(s, false).Devices); // conectado, isso se sabe
        Assert.Equal("Estado desconhecido de 1 dispositivo", Header(s).Status);
    }

    [Fact]
    public void Desconhecido_perde_para_erro_ocupado_e_atencao_e_ganha_de_ok()
    {
        Assert.Equal(Level.Error, Levels.Worst([Level.Unknown, Level.Error]));
        Assert.Equal(Level.Busy, Levels.Worst([Level.Unknown, Level.Busy]));
        Assert.Equal(Level.Warn, Levels.Worst([Level.Unknown, Level.Warn]));
        Assert.Equal(Level.Unknown, Levels.Worst([Level.Unknown, Level.Ok, Level.Off]));
    }

    [Fact]
    public void Plural_no_cabecalho()
    {
        var a = new FolderInfo(); ReadFailures.Folder(a, "x");
        var b = new FolderInfo(); ReadFailures.Folder(b, "x");
        var s = Running(a, b);
        var d = new DeviceInfo { Connected = true }; ReadFailures.Device(d);
        s.Devices.Add(d); s.AllDevices.Add(d);
        Assert.Equal("Estado desconhecido de 2 pastas e 1 dispositivo", Header(s).Status);
    }
}
