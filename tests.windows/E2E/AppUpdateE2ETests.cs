using FlaUI.Core.AutomationElements;

namespace WslSyncMonitor.WindowsTests.E2E;

/// <summary>
/// Atualização do app de verdade, pelo menu: a versão anterior só fecha depois de a nova avisar que abriu (a
/// proteção da sincronização nunca fica sem app). Se a nova não abre, volta o executável anterior e continua.
/// </summary>
[Collection("E2E")]
public sealed class AppUpdateE2ETests
{
    /// <summary>Abre o app, pula a oferta de instalação e clica em "Atualizar para a versão 9.9.9" no menu.</summary>
    static void UpdateFromMenu(AppSession app, string shot)
    {
        var install = app.WaitWindow("Instalar o WSL Sync Monitor", TimeSpan.FromSeconds(30));
        install.FindFirstDescendant(cf => cf.ByName("Só executar")).AsButton().Invoke();
        var widget = app.Widget(TimeSpan.FromSeconds(30));
        Thread.Sleep(1500);
        app.LatestVersion = "9.9.9";
        widget.RightClick();
        AppSession.WaitFor(() => app.MenuItem("Preferências"), null, "Preferências").Click();
        AppSession.WaitFor(() => app.MenuItem("Verificar atualizações"), null, "Verificar atualizações").Click();
        var update = AppSession.WaitFor(() => app.MenuItem("Atualizar para a versão 9.9.9"), TimeSpan.FromSeconds(15), "Atualizar para 9.9.9");
        app.Screenshot(shot);
        update.Click();
    }

    /// <summary>A caixa de mensagem do app com o texto (o painel tem o mesmo título: procura pelo conteúdo).</summary>
    static Window MessageWith(AppSession app, string text, TimeSpan timeout) => AppSession.WaitFor(() =>
        app.Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(app.Process.Id))
            .FirstOrDefault(w => w.FindAllDescendants().Any(e => e.Name?.Contains(text, StringComparison.Ordinal) == true))?.AsWindow(),
        timeout, $"aviso \"{text}\"");

    static void Run(AppSession app, Action test)
    {
        try { test(); }
        catch (Exception ex)
        {
            app.Screenshot("falha-atualizacao");
            throw new Exception($"{ex.Message}\n{app.LogText()}", ex);
        }
    }

    [E2EFact]
    public void Nova_versao_assume_e_so_entao_a_anterior_fecha()
    {
        using var app = new AppSession("""{ "SettingsVersion": 2, "ShowTaskbarWidget": true }""", copy: true);
        app.Download = File.ReadAllBytes(AppSession.AppExe()); // a "versão nova" é o mesmo app
        Run(app, () =>
        {
            UpdateFromMenu(app, "atualizacao-1-menu");
            Assert.True(app.Process.WaitForExit(60_000), "a versão anterior não fechou");
            var next = AppSession.WaitFor(() => app.Running().FirstOrDefault(), TimeSpan.FromSeconds(30), "a versão nova rodando");
            app.Widget(TimeSpan.FromSeconds(30));
            app.Screenshot("atualizacao-2-nova-versao");
            var log = app.LogText();
            Assert.Contains("fechado para a nova versão assumir", log);
            Assert.Equal(2, log.Split("[app] iniciado").Length - 1);
            Assert.DoesNotContain("não abriu", log);
        });
    }

    [E2EFact]
    public void Executavel_invalido_volta_para_a_versao_anterior()
    {
        using var app = new AppSession("""{ "SettingsVersion": 2, "ShowTaskbarWidget": true }""", copy: true);
        var original = File.ReadAllBytes(app.Exe);
        app.Download = [(byte)'M', (byte)'Z', .. new byte[4096]]; // começa como um .exe, mas não é
        Run(app, () =>
        {
            UpdateFromMenu(app, "atualizacao-invalida-1-menu");
            var box = MessageWith(app, "Não foi possível atualizar", TimeSpan.FromSeconds(30));
            app.Screenshot("atualizacao-invalida-2-aviso");
            Assert.False(app.Process.HasExited, "o app fechou mesmo sem a versão nova");
            Assert.Equal(original, File.ReadAllBytes(app.Exe));
            box.FindFirstDescendant(cf => cf.ByName("Não")).AsButton().Invoke();
        });
    }

    [E2EFact]
    public void Versao_nova_que_nao_avisa_que_abriu_volta_para_a_anterior()
    {
        // um .exe de verdade que abre e fecha sem avisar (como uma versão que trava antes de iniciar)
        var silent = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        using var app = new AppSession("""{ "SettingsVersion": 2, "ShowTaskbarWidget": true }""", copy: true,
            env: new Dictionary<string, string> { ["WSLSYNCMONITOR_UPDATE_WAIT_SECONDS"] = "5" });
        var original = File.ReadAllBytes(app.Exe);
        app.Download = File.ReadAllBytes(silent);
        Run(app, () =>
        {
            UpdateFromMenu(app, "atualizacao-sem-aviso-1-menu");
            var box = MessageWith(app, "a versão nova não abriu", TimeSpan.FromSeconds(40));
            app.Screenshot("atualizacao-sem-aviso-2-aviso");
            Assert.False(app.Process.HasExited, "o app fechou mesmo sem a versão nova avisar");
            Assert.Equal(original, File.ReadAllBytes(app.Exe));
            Assert.True(File.Exists(app.Exe + ".failed"));
            box.FindFirstDescendant(cf => cf.ByName("Não")).AsButton().Invoke();
        });
    }
}
