using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// Um dispositivo desligado é normal numa rede com vários (ex.: trabalho, casa e uma VM sempre ligada): não deixa o
/// estado geral amarelo enquanto houver outro conectado. Só "nenhum conectado" é atenção (nada sincroniza).
/// </summary>
public class DeviceLevelTests
{
    static Snapshot Triangle(params bool[] connected)
    {
        var s = new Snapshot { Target = new DistroInfo("Ubuntu", true, 2, true), St = StState.Running };
        s.Distros.Add(s.Target);
        s.Folders.Add(new FolderInfo { State = "idle" });
        foreach (var c in connected) s.Devices.Add(new DeviceInfo { Connected = c });
        return s;
    }

    [Fact]
    public void Desconectado_e_cinza_no_proprio_dispositivo() =>
        Assert.Equal(Level.Off, new DeviceInfo { Connected = false }.Status.Level);

    [Fact]
    public void Um_desligado_com_outro_conectado_nao_e_atencao()
    {
        var s = Triangle(true, false);
        Assert.Equal(Level.Ok, s.DevicesLevel);
        Assert.Equal(Level.Ok, s.Overall);
        Assert.Equal(IconGlyph.Ok, TrayIconModel.From(s, false, false).Glyph);
        var h = PanelHeader.For(s, null, new Version(1, 0, 0), UpdateState.NotChecked);
        Assert.Equal(("Tudo sincronizado", Level.Ok), (h.Status, h.StatusLevel));
    }

    [Fact]
    public void Nenhum_conectado_e_atencao_com_o_motivo_certo()
    {
        var s = Triangle(false, false);
        Assert.Equal(Level.Warn, s.DevicesLevel);
        Assert.Equal(Level.Warn, s.Overall);
        var h = PanelHeader.For(s, null, new Version(1, 0, 0), UpdateState.NotChecked);
        Assert.Equal(("Nenhum dispositivo conectado", Level.Warn), (h.Status, h.StatusLevel));
    }

    [Fact]
    public void Pasta_pendente_mostra_a_contagem_de_pastas()
    {
        var s = Triangle(true);
        s.Folders.Add(new FolderInfo { State = "idle", NeedBytes = 5 });
        var h = PanelHeader.For(s, null, new Version(1, 0, 0), UpdateState.NotChecked);
        Assert.Equal(("1 de 2 pastas atualizadas", Level.Warn), (h.Status, h.StatusLevel));
    }

    [Fact]
    public void Dispositivo_enviando_e_sincronizando()
    {
        var s = Triangle(true, false);
        s.Devices[0].Completion = 50;
        Assert.Equal(Level.Busy, s.DevicesLevel);
        Assert.Equal(Level.Busy, s.Overall);
    }

    [Fact]
    public void Sem_dispositivos_ou_todos_pausados_nao_e_atencao()
    {
        Assert.Equal(Level.Off, Triangle().DevicesLevel);
        var s = Triangle(false); s.Devices[0].Paused = true;
        Assert.Equal(Level.Off, s.DevicesLevel);
        Assert.Equal(Level.Ok, s.Overall);
    }
}
