using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>Verificação de pastas e dispositivos ao iniciar (como a busca de atualização: só avisa se houver algo).</summary>
public class HealthCheckTests
{
    static readonly DateTime Now = new(2026, 10, 22, 9, 0, 0);

    static Snapshot Snap(TimeSpan? uptime = null)
    {
        var s = new Snapshot { Target = new DistroInfo("Ubuntu", true, 2, true), St = StState.Running, StUptime = uptime ?? TimeSpan.FromMinutes(5) };
        s.Distros.Add(s.Target);
        return s;
    }

    static void AddFolder(Snapshot s, FolderInfo f) { s.Folders.Add(f); s.AllFolders.Add(f); }
    static void AddDevice(Snapshot s, DeviceInfo d) { s.Devices.Add(d); s.AllDevices.Add(d); }

    [Fact]
    public void Tudo_certo_nao_tem_avisos()
    {
        var s = Snap();
        AddFolder(s, new FolderInfo { Label = "Claude", State = "idle", SharedWith = 2 });
        AddDevice(s, new DeviceInfo { Name = "vm", Connected = true });
        AddDevice(s, new DeviceInfo { Name = "casa", LastSeen = Now.AddHours(-10) }); // desligado há pouco: normal
        Assert.Empty(HealthCheck.Find(s, Now, staleDays: 7));
    }

    [Fact]
    public void Problemas_de_pastas()
    {
        var s = Snap();
        AddDevice(s, new DeviceInfo { Name = "vm", Connected = true });
        AddFolder(s, new FolderInfo { Label = "Claude", State = "idle", Errors = 2 });
        AddFolder(s, new FolderInfo { Label = "Projetos", State = "idle", NeedBytes = 5 });
        AddFolder(s, new FolderInfo { Label = "Fotos", Paused = true });
        AddFolder(s, new FolderInfo { Label = "Sync", State = "syncing" }); // sincronizando: normal

        var issues = HealthCheck.Find(s, Now, 7);

        Assert.Equal(["pasta Claude: 2 erro(s)", "pasta Projetos fora de sincronia", "pasta Fotos pausada"], issues.Select(i => i.Text));
        Assert.Equal(Level.Error, issues[0].Level);
    }

    [Fact]
    public void Dispositivos_pausados_ou_sem_conectar_ha_dias()
    {
        var s = Snap();
        AddDevice(s, new DeviceInfo { Name = "vm", Connected = true });
        AddDevice(s, new DeviceInfo { Name = "notebook", LastSeen = Now.AddDays(-9) });
        AddDevice(s, new DeviceInfo { Name = "novo" }); // nunca visto
        AddDevice(s, new DeviceInfo { Name = "tablet", Paused = true });

        Assert.Equal(["notebook não conecta há 9 dias", "novo nunca conectou", "dispositivo tablet pausado"],
            HealthCheck.Find(s, Now, 7).Select(i => i.Text));
    }

    [Fact]
    public void Nenhum_dispositivo_conectado()
    {
        var s = Snap();
        AddDevice(s, new DeviceInfo { Name = "casa", LastSeen = Now.AddHours(-1) });
        Assert.Equal(["nenhum dispositivo conectado"], HealthCheck.Find(s, Now, 7).Select(i => i.Text));
    }

    [Fact]
    public void Itens_ocultos_do_painel_nao_entram()
    {
        var s = Snap();
        AddDevice(s, new DeviceInfo { Name = "vm", Connected = true });
        s.AllFolders.Add(new FolderInfo { Label = "Default", Paused = true }); // oculta: escolha do usuário
        Assert.Empty(HealthCheck.Find(s, Now, 7));
    }

    [Theory]
    [InlineData(StState.Running, 120, false, true)]  // já teve tempo de conectar
    [InlineData(StState.Running, 20, false, false)]  // acabou de iniciar: espera
    [InlineData(StState.Running, 20, true, true)]    // todos conectados: não precisa esperar
    [InlineData(StState.NotRunning, 999, true, false)]
    public void So_roda_com_o_syncthing_pronto(StState st, int uptimeSeconds, bool allConnected, bool ready)
    {
        var s = Snap(TimeSpan.FromSeconds(uptimeSeconds));
        s.St = st;
        AddDevice(s, new DeviceInfo { Name = "vm", Connected = true });
        AddDevice(s, new DeviceInfo { Name = "casa", Connected = allConnected });
        Assert.Equal(ready, HealthCheck.Ready(s, graceSeconds: 90));
    }

    [Fact]
    public void Resumo_para_a_notificacao_mostra_ate_tres()
    {
        var issues = Enumerable.Range(1, 5).Select(i => new HealthIssue(Level.Warn, $"aviso {i}")).ToList();
        Assert.Equal("aviso 1\naviso 2\naviso 3\ne mais 2", HealthCheck.Summary(issues));
    }

    [Fact]
    public void Estado_desconhecido_tambem_avisa()
    {
        var s = Snap();
        var f = new FolderInfo { Label = "Fotos" }; ReadFailures.Folder(f, "HTTP 500");
        AddFolder(s, f);
        var d = new DeviceInfo { Name = "vm", Connected = true }; ReadFailures.Device(d);
        AddDevice(s, d);
        Assert.Equal(new[] { "pasta Fotos: estado desconhecido (HTTP 500)", "vm: andamento desconhecido" },
            HealthCheck.Find(s, Now, 7).Select(i => i.Text));
    }
}
