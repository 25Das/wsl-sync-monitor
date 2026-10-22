using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

public class SyncthingScriptsParseTests
{
    const string Xml = "<configuration><gui><apikey>KEY</apikey></gui></configuration>";

    [Fact]
    public void Separa_cada_arquivo_com_caminho_e_conteudo()
    {
        var output = $"@@FILE /a/config.xml\n{Xml}\n@@FILE /b/config.xml\n{Xml}\n";

        var files = SyncthingScripts.ParseConfigs(output);

        Assert.Equal(["/a/config.xml", "/b/config.xml"], files.Select(f => f.Path));
        Assert.All(files, f => Assert.Contains("<apikey>KEY</apikey>", f.Xml));
    }

    [Fact]
    public void Ignora_arquivo_que_nao_e_config_do_syncthing()
    {
        var output = $"@@FILE /a/config.xml\nnão é xml\n@@FILE /b/config.xml\n{Xml}\n";

        Assert.Equal(["/b/config.xml"], SyncthingScripts.ParseConfigs(output).Select(f => f.Path));
    }

    [Fact]
    public void Aceita_quebras_de_linha_do_windows_e_caminho_com_espacos()
    {
        var output = $"@@FILE /home/me/my dir/config.xml\r\n{Xml}\r\n";

        Assert.Equal(["/home/me/my dir/config.xml"], SyncthingScripts.ParseConfigs(output).Select(f => f.Path));
    }

    [Fact]
    public void Saida_vazia_nao_tem_arquivos()
    {
        Assert.Empty(SyncthingScripts.ParseConfigs(""));
    }
}
