using System.Runtime.InteropServices;
using WslSyncMonitor;

namespace WslSyncMonitor.WindowsTests;

/// <summary>
/// Desligamento do Windows simulado com as mesmas mensagens que o Windows envia. Os testes mexem no estado global
/// "sessão terminando", por isso não rodam em paralelo com outros que executam o wsl.exe.
/// </summary>
[Collection("Sessão")]
public sealed class ShutdownTests : IDisposable
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public void Dispose() => WslService.SessionEnding = false;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int max);

    [Fact]
    public void Janela_que_segura_o_desligamento_tem_o_nome_e_o_icone_do_app()
    {
        // é ela que o Windows lista em "Estes aplicativos estão impedindo o desligamento"
        var w = new ShutdownWatcher(() => false, () => Task.CompletedTask, () => 10);
        try
        {
            var title = new System.Text.StringBuilder(256);
            GetWindowText(w.Handle, title, title.Capacity);
            Assert.Equal("WSL Sync Monitor", title.ToString());
            Assert.NotEqual(IntPtr.Zero, SendMessage(w.Handle, 0x7F /* WM_GETICON */, (IntPtr)1, IntPtr.Zero));
        }
        finally { w.DestroyHandle(); }
    }

    static IntPtr Send(ShutdownWatcher w, int msg, long wParam, long lParam) =>
        SendMessage(w.Handle, msg, (IntPtr)wParam, (IntPtr)lParam);

    [Fact]
    public void Aviso_de_desligamento_roda_a_sequencia_e_marca_a_sessao_terminando()
    {
        bool ran = false;
        var w = new ShutdownWatcher(() => true, () => { ran = true; return Task.CompletedTask; }, () => 10);
        try
        {
            Assert.Equal(1, (long)Send(w, Native.WM_QUERYENDSESSION, 0, 0)); // pode desligar
            Assert.True(ran);
            Assert.True(WslService.SessionEnding);
        }
        finally { w.DestroyHandle(); }
    }

    [Fact]
    public void Desligamento_cancelado_volta_ao_normal_e_protege_de_novo_no_proximo()
    {
        int runs = 0;
        var w = new ShutdownWatcher(() => true, () => { runs++; return Task.CompletedTask; }, () => 10);
        try
        {
            Send(w, Native.WM_QUERYENDSESSION, 0, 0);
            Send(w, Native.WM_ENDSESSION, 0, 0); // wParam 0 = cancelado
            Assert.False(WslService.SessionEnding);

            Send(w, Native.WM_QUERYENDSESSION, 0, 0);
            Assert.Equal(2, runs);
        }
        finally { w.DestroyHandle(); }
    }

    [Fact]
    public void Instalador_pedindo_para_fechar_o_app_nao_e_fim_de_sessao()
    {
        var w = new ShutdownWatcher(() => false, () => Task.CompletedTask, () => 10);
        try
        {
            Send(w, Native.WM_QUERYENDSESSION, 0, 0x1); // ENDSESSION_CLOSEAPP
            Assert.False(WslService.SessionEnding);
        }
        finally { w.DestroyHandle(); }
    }

    [Fact]
    public async Task Com_a_sessao_terminando_nada_executa_o_wslexe()
    {
        // nem a sequência de desligamento: o processo falharia (0xc0000142) e a janela de erro travaria o desligamento
        WslService.SessionEnding = true;
        await Assert.ThrowsAsync<WslService.SessionEndingException>(() => WslService.RunAsync(["--version"]));
        await Assert.ThrowsAsync<WslService.SessionEndingException>(() => Task.Run(() => WslService.RunAsync(["--version"])));
    }

    [Fact]
    public async Task Pedido_a_distro_sem_processo_de_manutencao_responde_logo_que_nao()
    {
        var start = DateTime.Now;
        Assert.False(await WslService.RequestInDistroAsync("NaoExiste-Teste", "sync", "synced", TimeSpan.FromSeconds(10)));
        Assert.True(DateTime.Now - start < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Com_a_sessao_terminando_nao_liga_distro()
    {
        WslService.SessionEnding = true;
        WslService.StartDistro("NaoExiste-Teste");
        Assert.False(WslService.IsHolding("NaoExiste-Teste"));
    }
}

public class LogTests
{
    [Fact]
    public void Grava_no_arquivo_do_dia_sem_a_chave_da_API()
    {
        var key = "chave-secreta-" + Guid.NewGuid().ToString("N");
        var marker = Guid.NewGuid().ToString("N");
        Log.AddSecret(key);

        Log.Warn("teste", $"{marker} falhou com a chave {key}", new InvalidOperationException("detalhe " + key));

        var text = File.ReadAllText(Log.CurrentFile);
        Assert.Contains(marker, text);
        Assert.DoesNotContain(key, text);
        Assert.Contains("AVISO [teste]", text);
    }
}
