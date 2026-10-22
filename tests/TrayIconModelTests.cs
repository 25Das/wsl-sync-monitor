using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

public class TrayIconModelTests
{
    static Snapshot Running(params Level[] items)
    {
        var s = new Snapshot { Target = new DistroInfo("Ubuntu", true, 2, true), St = StState.Running };
        s.Distros.Add(s.Target);
        foreach (var l in items)
            s.Folders.Add(l switch
            {
                Level.Busy => new FolderInfo { State = "syncing", GlobalBytes = 10, InSyncBytes = 5 },
                Level.Warn => new FolderInfo { State = "idle", NeedBytes = 5 },
                Level.Error => new FolderInfo { State = "error" },
                _ => new FolderInfo { State = "idle" },
            });
        return s;
    }

    [Fact]
    public void Carregando() => Assert.Equal(IconGlyph.Loading, TrayIconModel.From(null, false, false).Glyph);

    [Fact]
    public void WSL_desligado_mostra_o_simbolo_de_energia_em_cinza()
    {
        var s = new Snapshot { Target = new DistroInfo("Ubuntu", false, 2, true) };
        var m = TrayIconModel.From(s, false, false);
        Assert.Equal((IconGlyph.Off, Level.Off), (m.Glyph, m.Level));
    }

    [Theory]
    [InlineData(Level.Ok, IconGlyph.Ok)]
    [InlineData(Level.Busy, IconGlyph.Sync)]
    [InlineData(Level.Warn, IconGlyph.Warn)]
    [InlineData(Level.Error, IconGlyph.Error)]
    public void Simbolo_segue_o_estado_geral(Level folder, IconGlyph glyph)
    {
        var m = TrayIconModel.From(Running(Level.Ok, folder), false, false);
        Assert.Equal((glyph, folder), (m.Glyph, m.Level));
    }

    [Fact]
    public void Syncthing_parado_e_erro()
    {
        var s = Running(); s.St = StState.NotRunning;
        Assert.Equal(IconGlyph.Error, TrayIconModel.From(s, false, false).Glyph);
    }

    [Fact]
    public void Marcas_de_mantida_ligada_e_atualizacao_pendente()
    {
        var m = TrayIconModel.From(Running(Level.Ok), keptAlive: true, updatePending: true);
        Assert.True(m.KeptAlive);
        Assert.True(m.UpdatePending);
    }

    [Fact]
    public void Dica_explica_o_estado_e_cabe_no_limite_do_windows()
    {
        var s = Running(Level.Ok);
        s.Devices.Add(new DeviceInfo { Connected = true });
        s.Devices.Add(new DeviceInfo { Connected = false });

        var tip = TrayIconModel.Tooltip(s, keptAlive: "até 16:00", update: new Version(1, 9, 0));

        Assert.Contains("1 de 2 dispositivos conectados", tip);
        Assert.Contains("Mantida ligada até 16:00", tip);
        Assert.Contains("versão 1.9.0", tip);
        Assert.True(tip.Length <= 127, $"{tip.Length} caracteres");
    }
}
