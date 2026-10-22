using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

public class PanelHeaderTests
{
    static readonly Version Current = new(1, 9, 0);
    static readonly DateTime At = new(2026, 10, 22, 14, 5, 9);

    static Snapshot Running(Level folder = Level.Ok)
    {
        var s = new Snapshot { Time = At, Target = new DistroInfo("Ubuntu", true, 2, true), St = StState.Running };
        s.Distros.Add(s.Target);
        s.Folders.Add(folder switch
        {
            Level.Busy => new FolderInfo { State = "syncing", GlobalBytes = 10, InSyncBytes = 5 },
            Level.Error => new FolderInfo { State = "error" },
            _ => new FolderInfo { State = "idle" },
        });
        return s;
    }

    static PanelHeader For(Snapshot? s, string? busy = null, UpdateState? u = null) =>
        PanelHeader.For(s, busy, Current, u ?? UpdateState.NotChecked);

    [Fact]
    public void Carregando()
    {
        var h = For(null);
        Assert.Equal("Carregando…", h.Status);
        Assert.Equal("", h.Checked);
    }

    [Fact]
    public void Status_resume_o_estado_e_a_hora_da_verificacao_fica_a_parte()
    {
        var h = For(Running());
        Assert.Equal(("Tudo sincronizado", Level.Ok), (h.Status, h.StatusLevel));
        Assert.Equal("Verificado às 14:05:09", h.Checked);
    }

    [Theory]
    [InlineData(Level.Busy, "Sincronizando…")]
    [InlineData(Level.Error, "Há erros em pastas ou dispositivos")]
    public void Status_segue_o_estado_geral(Level folder, string text) =>
        Assert.Equal((text, folder), (For(Running(folder)).Status, For(Running(folder)).StatusLevel));

    [Fact]
    public void WSL_parado_e_Syncthing_fora()
    {
        var off = new Snapshot { Target = new DistroInfo("Ubuntu", false, 2, true) };
        Assert.Equal(("WSL parado", Level.Off), (For(off).Status, For(off).StatusLevel));
        var st = Running(); st.St = StState.AuthError;
        Assert.Equal(("Syncthing: chave da API inválida", Level.Error), (For(st).Status, For(st).StatusLevel));
    }

    [Fact]
    public void Operacao_em_andamento_ocupa_o_status()
    {
        var h = For(Running(), busy: "Encerrando Ubuntu com segurança, aguardando a sincronização…");
        Assert.Equal(("Encerrando Ubuntu com segurança, aguardando a sincronização…", Level.Busy), (h.Status, h.StatusLevel));
        Assert.Equal(HeaderAction.None, h.VersionAction); // não troca o app no meio de uma operação
    }

    [Fact]
    public void Versao_sem_verificacao_clica_para_verificar()
    {
        var h = For(Running());
        Assert.Equal(("v1.9.0", HeaderAction.CheckUpdate, false), (h.Version, h.VersionAction, h.VersionHighlight));
    }

    [Fact]
    public void Versao_mais_recente_diz_quando_verificou()
    {
        var h = For(Running(), u: UpdateState.UpToDate(At));
        Assert.Equal(("v1.9.0", HeaderAction.CheckUpdate), (h.Version, h.VersionAction));
        Assert.Contains("mais recente", h.VersionTip);
        Assert.Contains("14:05", h.VersionTip);
    }

    [Fact]
    public void Verificando_nao_aceita_clique()
    {
        var h = For(Running(), u: UpdateState.Checking);
        Assert.Equal(("v1.9.0 · verificando…", HeaderAction.None), (h.Version, h.VersionAction));
    }

    [Fact]
    public void Falha_permite_tentar_de_novo()
    {
        var h = For(Running(), u: UpdateState.Failed("sem rede"));
        Assert.Equal(HeaderAction.CheckUpdate, h.VersionAction);
        Assert.Contains("sem rede", h.VersionTip);
        Assert.Contains("falhou", h.Version);
    }

    [Fact]
    public void Atualizacao_disponivel_vira_botao_de_destaque()
    {
        var h = For(Running(), u: UpdateState.Found(new Version(1, 9, 1), At));
        Assert.Equal(("Atualizar para 1.9.1", HeaderAction.InstallUpdate, true), (h.Version, h.VersionAction, h.VersionHighlight));
        Assert.Contains("1.9.0", h.VersionTip);
    }

    [Fact]
    public void Recarregar_explica_que_tambem_procura_versao_nova() =>
        Assert.Contains("versão nova do app", PanelHeader.RefreshTip);
}
