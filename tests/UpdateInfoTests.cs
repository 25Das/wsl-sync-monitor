using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

public class UpdateInfoTests
{
    const string Release = """
        {
          "tag_name": "v1.7.0",
          "html_url": "https://github.com/25Das/wsl-sync-monitor/releases/tag/v1.7.0",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "notas.txt", "browser_download_url": "https://example/notas.txt", "size": 10 },
            { "name": "WslSyncMonitor.exe", "browser_download_url": "https://example/WslSyncMonitor.exe", "size": 286318 }
          ]
        }
        """;

    [Fact]
    public void Le_versao_pagina_e_o_exe_da_release()
    {
        var u = UpdateInfo.Parse(Release)!;

        Assert.Equal(new Version(1, 7, 0), u.Version);
        Assert.Equal("https://github.com/25Das/wsl-sync-monitor/releases/tag/v1.7.0", u.PageUrl);
        Assert.Equal("https://example/WslSyncMonitor.exe", u.DownloadUrl);
        Assert.Equal(286318, u.Size);
    }

    [Theory]
    [InlineData("\"draft\": false", "\"draft\": true")]
    [InlineData("\"prerelease\": false", "\"prerelease\": true")]
    [InlineData("\"name\": \"WslSyncMonitor.exe\"", "\"name\": \"outro.exe\"")]
    [InlineData("\"tag_name\": \"v1.7.0\"", "\"tag_name\": \"ultima\"")]
    public void Ignora_rascunho_pre_release_sem_exe_ou_tag_invalida(string from, string to)
    {
        Assert.Null(UpdateInfo.Parse(Release.Replace(from, to)));
    }

    [Fact]
    public void Json_invalido_nao_tem_atualizacao()
    {
        Assert.Null(UpdateInfo.Parse("não é json"));
    }

    [Theory]
    [InlineData("1.6.0", true)]
    [InlineData("1.6.9", true)]
    [InlineData("1.7.0", false)]
    [InlineData("1.8.0", false)]
    public void So_e_atualizacao_se_for_mais_nova(string current, bool expected)
    {
        var u = UpdateInfo.Parse(Release)!;

        Assert.Equal(expected, u.IsNewerThan(Version.Parse(current)));
    }

    [Fact]
    public void Compara_ignorando_o_quarto_numero_da_versao_do_assembly()
    {
        // o assembly tem 4 partes (1.7.0.0); a tag tem 3 (v1.7.0)
        Assert.False(UpdateInfo.Parse(Release)!.IsNewerThan(new Version(1, 7, 0, 0)));
    }
}

public class UpdateInfoRealResponseTests
{
    // resposta real de GET /repos/25Das/wsl-sync-monitor/releases/latest (release v1.6.0)
    static string RealResponse() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github-release-latest.json"));

    [Fact]
    public void Le_a_resposta_real_da_API_do_GitHub()
    {
        var u = UpdateInfo.Parse(RealResponse())!;

        Assert.Equal(new Version(1, 6, 0), u.Version);
        Assert.EndsWith("/releases/tag/v1.6.0", u.PageUrl);
        Assert.EndsWith("/v1.6.0/WslSyncMonitor.exe", u.DownloadUrl);
        Assert.True(u.Size > 100_000);
        Assert.True(u.IsNewerThan(new Version(1, 5, 6)));
        Assert.False(u.IsNewerThan(new Version(1, 6, 0, 0)));
    }
}
