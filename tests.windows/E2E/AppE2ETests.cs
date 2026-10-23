using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace WslSyncMonitor.WindowsTests.E2E;

/// <summary>
/// O app de verdade, usado como uma pessoa usaria (mouse e teclado), do primeiro início até sair. Cada passo
/// deixa uma captura da tela em ci-evidencias (e2e-*.png). No runner do CI não há WSL: o app mostra "WSL não
/// encontrado", o que não atrapalha a interface.
/// </summary>
[Collection("E2E")]
public sealed class AppE2ETests
{
    [E2EFact]
    public void Primeiro_inicio_menu_busca_de_atualizacao_painel_e_sair()
    {
        using var app = new AppSession("""{ "SettingsVersion": 2, "ShowTaskbarWidget": true }""");
        try
        {
            // 1. fora da pasta de instalação, oferece instalar; "Só executar" segue sem instalar nada
            var install = app.WaitWindow("Instalar o WSL Sync Monitor", TimeSpan.FromSeconds(30));
            app.Screenshot("1-oferta-de-instalacao");
            install.FindFirstDescendant(cf => cf.ByName("Só executar")).AsButton().Invoke();

            // 2. o widget aparece sobre a barra de tarefas
            var widget = app.Widget(TimeSpan.FromSeconds(30));
            Thread.Sleep(1500); // primeira atualização e busca de atualização (servidor falso: em dia)
            app.Screenshot("2-widget");

            // 3. botão direito no widget abre o menu do app (não o da barra, que é a janela pai do widget)
            widget.RightClick();
            AppSession.WaitFor(app.OpenMenu, null, "menu principal");
            app.Screenshot("3-menu");

            // 4. Preferências → Verificar atualizações: o menu continua aberto e o item vira o botão de atualizar
            AppSession.WaitFor(() => app.MenuItem("Preferências"), null, "Preferências").Click();
            var check = AppSession.WaitFor(() => app.MenuItem("Verificar atualizações"), null, "Verificar atualizações");
            app.LatestVersion = "9.9.9";
            check.Click();
            AppSession.WaitFor(() => app.MenuItem("Atualizar para a versão 9.9.9"), TimeSpan.FromSeconds(15), "item Atualizar para a versão 9.9.9");
            Assert.NotNull(app.OpenMenu()); // ainda aberto: quem buscou pelo menu atualiza pelo menu
            app.Screenshot("4-menu-com-atualizacao");
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Thread.Sleep(500);

            // 5. clique no widget abre o painel (com o botão "Atualizar para 9.9.9" no cabeçalho)
            widget.Click();
            app.WaitWindow("WSL Sync Monitor");
            Thread.Sleep(800);
            app.Screenshot("5-painel");
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Thread.Sleep(500);

            // 6. Sair pelo menu fecha o app (sem WSL não há o que esperar)
            widget.RightClick();
            AppSession.WaitFor(() => app.MenuItem("Sair"), null, "Sair").Click();
            Assert.True(app.Process.WaitForExit(20_000), "o app não fechou");

            var log = app.LogText();
            Assert.Contains("[app] iniciado", log);
            Assert.Contains("[app] saindo", log);
        }
        catch (Exception ex)
        {
            app.Screenshot("falha");
            string items;
            try { items = app.VisibleMenuItems(); } catch (Exception e) { items = e.Message; }
            throw new Exception($"{ex.Message}\nitens de menu visíveis: {items}\n{app.LogText()}", ex);
        }
    }
}
