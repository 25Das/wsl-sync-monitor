using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>Widget, ícone, dica e painel contam a mesma história.</summary>
public class VisualConsistencyTests
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

    [Fact]
    public void Operacao_em_andamento_tambem_aparece_no_icone()
    {
        // o widget já pinta o WSL de azul e o painel mostra a operação; o ícone mostrava "tudo certo"
        var m = TrayIconModel.From(Running(new FolderInfo { State = "idle" }), false, false, busy: true);
        Assert.Equal((IconGlyph.Sync, Level.Busy), (m.Glyph, m.Level));
        Assert.Equal(IconGlyph.Ok, TrayIconModel.From(Running(new FolderInfo { State = "idle" }), false, false).Glyph);
    }

    [Fact]
    public void Erro_nao_e_escondido_pela_operacao_em_andamento()
    {
        var s = Running(new FolderInfo { State = "error" });
        Assert.Equal(IconGlyph.Error, TrayIconModel.From(s, false, false, busy: true).Glyph);
    }

    [Fact]
    public void Sem_dados_ainda_os_pontos_sao_desconhecidos_e_nao_desligados()
    {
        var w = WidgetModel.From(null, busy: false);
        Assert.Equal((Level.Unknown, Level.Unknown), (w.Wsl, w.Syncthing));
    }

    [Fact]
    public void Sincronizado_agora_nao_e_anunciado_como_sincronizando()
    {
        var recent = Running(new FolderInfo { State = "idle", RecentFiles = 2 });
        var h = PanelHeader.For(recent, null, V, UpdateState.NotChecked);
        Assert.Equal(("Sincronizado agora", Level.Busy), (h.Status, h.StatusLevel));

        var device = Running(new FolderInfo { State = "idle" });
        device.Devices.Add(new DeviceInfo { Connected = true, RecentActivity = true });
        device.AllDevices.AddRange(device.Devices);
        Assert.Equal("Sincronizado agora", PanelHeader.For(device, null, V, UpdateState.NotChecked).Status);

        var syncing = Running(new FolderInfo { State = "idle", RecentFiles = 2 }, new FolderInfo { State = "syncing" });
        Assert.Equal("Sincronizando…", PanelHeader.For(syncing, null, V, UpdateState.NotChecked).Status);
    }

    [Fact]
    public void Dica_diz_nenhum_em_vez_de_zero_de_zero()
    {
        var tip = TrayIconModel.Tooltip(Running(), null, null);
        Assert.Equal("Ubuntu\nPastas: nenhuma\nDispositivos: nenhum", tip);
    }
}
