using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// O que o widget mostra: o contador de pastas e o de dispositivos contam o que está sem problema (pasta) ou
/// conectado (dispositivo), e a cor diz o que está acontecendo. Sincronizar não tira a pasta da conta: "1/1" verde
/// quando parada e "1/1" azul enquanto sincroniza, nunca "0/1" azul.
/// </summary>
public class WidgetModelTests
{
    static Snapshot Running(params FolderInfo[] folders)
    {
        var s = new Snapshot { Target = new DistroInfo("Ubuntu", true, 2, true), St = StState.Running };
        s.Distros.Add(s.Target);
        s.Folders.AddRange(folders);
        s.AllFolders.AddRange(folders);
        return s;
    }

    public static TheoryData<string, FolderInfo, string, Level> Pastas => new()
    {
        { "atualizada", new FolderInfo { State = "idle" }, "1/1", Level.Ok },
        { "sincronizando", new FolderInfo { State = "syncing", GlobalBytes = 10, InSyncBytes = 5 }, "1/1", Level.Busy },
        { "arquivo em andamento", new FolderInfo { State = "syncing", CurrentItem = "a.txt" }, "1/1", Level.Busy },
        { "sincronizada agora", new FolderInfo { State = "idle", RecentFiles = 2 }, "1/1", Level.Busy },
        { "verificando", new FolderInfo { State = "scanning" }, "1/1", Level.Busy },
        { "fora de sincronia", new FolderInfo { State = "idle", NeedBytes = 5 }, "0/1", Level.Warn },
        { "com erro", new FolderInfo { State = "error" }, "0/1", Level.Error },
        { "pausada", new FolderInfo { State = "idle", Paused = true }, "0/1", Level.Off },
    };

    [Theory]
    [MemberData(nameof(Pastas))]
    public void Contador_de_pastas_e_cor_concordam(string caso, FolderInfo folder, string text, Level level)
    {
        var m = WidgetModel.From(Running(folder), busy: false);
        Assert.True((text, level) == (m.Folders, m.FoldersLevel), $"{caso}: {m.Folders} {m.FoldersLevel}");
    }

    [Fact]
    public void Contador_de_pastas_e_o_mesmo_na_dica_e_no_painel()
    {
        var s = Running(new FolderInfo { State = "idle" }, new FolderInfo { State = "syncing" }, new FolderInfo { State = "error" });
        Assert.Equal(2, s.FoldersHealthy);
        Assert.Equal("2/3", WidgetModel.From(s, false).Folders);
        Assert.Contains("Pastas: 2 de 3 sem problemas", TrayIconModel.Tooltip(s, null, null));
    }

    [Fact]
    public void Dispositivos_contam_os_conectados_e_a_cor_segue_o_estado_deles()
    {
        var s = Running(new FolderInfo { State = "idle" });
        s.Devices.Add(new DeviceInfo { Connected = true, Completion = 50 });
        s.Devices.Add(new DeviceInfo { Connected = false });
        s.AllDevices.AddRange(s.Devices);
        var m = WidgetModel.From(s, false);
        Assert.Equal(("1/2", Level.Busy), (m.Devices, m.DevicesLevel));
    }

    [Fact]
    public void Sem_Syncthing_os_contadores_ficam_neutros()
    {
        var s = Running(new FolderInfo { State = "idle" });
        s.St = StState.Unreachable;
        var m = WidgetModel.From(s, false);
        Assert.Equal(("–", Level.Off, "–", Level.Off), (m.Folders, m.FoldersLevel, m.Devices, m.DevicesLevel));
        Assert.Equal(Level.Error, m.Syncthing);
    }

    [Fact]
    public void Operacao_em_andamento_deixa_o_ponto_do_WSL_azul()
    {
        Assert.Equal(Level.Busy, WidgetModel.From(Running(), busy: true).Wsl);
        Assert.Equal(Level.Busy, WidgetModel.From(null, busy: true).Wsl);
        Assert.Equal(Level.Unknown, WidgetModel.From(null, busy: false).Wsl);
    }

    [Fact]
    public void Distro_configurada_que_nao_existe_e_erro_em_todo_lugar()
    {
        var s = new Snapshot { WslError = "Distro \"Debian\" não encontrada" };
        s.Distros.Add(new DistroInfo("Ubuntu", true, 2, true));

        Assert.Equal(Level.Error, WidgetModel.From(s, false).Wsl);
        Assert.Equal(Level.Error, s.Overall);
        var icon = TrayIconModel.From(s, false, false);
        Assert.Equal((IconGlyph.Error, Level.Error), (icon.Glyph, icon.Level));
        Assert.Equal("WSL Sync Monitor\nWSL: distro \"Debian\" não encontrada", TrayIconModel.Tooltip(s, null, null));
        var h = PanelHeader.For(s, null, new Version(1, 0, 0), UpdateState.NotChecked);
        Assert.Equal(("Distro \"Debian\" não encontrada", Level.Error), (h.Status, h.StatusLevel));
    }

    [Fact]
    public void Nenhuma_distro_instalada_tambem()
    {
        var s = new Snapshot { WslError = "Nenhuma distro instalada" };
        Assert.Equal(Level.Error, WidgetModel.From(s, false).Wsl);
        Assert.Equal(IconGlyph.Error, TrayIconModel.From(s, false, false).Glyph);
    }

    [Fact]
    public void Pasta_pausada_aparece_no_status_em_vez_de_tudo_sincronizado()
    {
        var s = Running(new FolderInfo { State = "idle" }, new FolderInfo { State = "idle", Paused = true });
        var h = PanelHeader.For(s, null, new Version(1, 0, 0), UpdateState.NotChecked);
        Assert.Equal(("Sincronizado · 1 pasta pausada", Level.Ok), (h.Status, h.StatusLevel));
        s.Folders.Add(new FolderInfo { State = "idle", Paused = true });
        Assert.Equal("Sincronizado · 2 pastas pausadas", PanelHeader.For(s, null, new Version(1, 0, 0), UpdateState.NotChecked).Status);
    }

    [Fact]
    public void Pasta_pausada_nao_e_contada_como_problema()
    {
        var s = Running(new FolderInfo { State = "idle", NeedBytes = 5 }, new FolderInfo { State = "idle", Paused = true });
        var h = PanelHeader.For(s, null, new Version(1, 0, 0), UpdateState.NotChecked);
        Assert.Equal(("1 pasta precisa de atenção", Level.Warn), (h.Status, h.StatusLevel));
        Assert.Contains("Pastas: 0 de 2 sem problemas · 1 pausada", TrayIconModel.Tooltip(s, null, null));
        s.Folders.Add(new FolderInfo { State = "error" });
        s.Folders[1].Paused = false; s.Folders[1].NeedBytes = 1;
        Assert.Equal("Há erros em pastas ou dispositivos", PanelHeader.For(s, null, new Version(1, 0, 0), UpdateState.NotChecked).Status);
        s.Folders.RemoveAt(2);
        Assert.Equal("2 pastas precisam de atenção", PanelHeader.For(s, null, new Version(1, 0, 0), UpdateState.NotChecked).Status);
    }

    [Fact]
    public void Erro_longo_do_WSL_nao_estoura_a_dica()
    {
        // o Windows recusa dica com mais de 127 caracteres (o app quebraria ao atualizar o ícone)
        var s = new Snapshot { WslError = $"Distro \"{new string('x', 150)}\" não encontrada" };
        var tip = TrayIconModel.Tooltip(s, "até 16:00", new Version(1, 9, 0));
        Assert.True(tip.Length <= 127, $"{tip.Length} caracteres");
        Assert.StartsWith("WSL Sync Monitor\nWSL: distro \"xxx", tip);
        Assert.EndsWith("…\nMantida ligada até 16:00", tip);
    }

    [Fact]
    public void Erro_do_WSL_em_varias_linhas_mostra_so_a_primeira_na_dica()
    {
        var s = new Snapshot { WslError = "Falha ao executar o wsl.exe\r\nDetalhes técnicos" };
        Assert.Equal("WSL Sync Monitor\nWSL: falha ao executar o wsl.exe", TrayIconModel.Tooltip(s, null, null));
    }

    [Fact]
    public void WSL_nao_instalado_usa_a_mesma_regra()
    {
        var s = new Snapshot { WslInstalled = false, WslError = "wsl.exe não encontrado" };
        Assert.Equal("WSL Sync Monitor\nWSL: wsl.exe não encontrado", TrayIconModel.Tooltip(s, null, null));
        Assert.Equal(Level.Error, WidgetModel.From(s, false).Wsl);
    }
}
