using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

public class WslPathsTests
{
    [Theory]
    [InlineData("/home/daniel/www", @"\\wsl.localhost\Ubuntu\home\daniel\www")]
    [InlineData("~/.claude", @"\\wsl.localhost\Ubuntu\home\daniel\.claude")]
    [InlineData("~", @"\\wsl.localhost\Ubuntu\home\daniel")]
    [InlineData("Sync", @"\\wsl.localhost\Ubuntu\home\daniel\Sync")] // relativo: o Syncthing resolve a partir do home
    [InlineData("/", @"\\wsl.localhost\Ubuntu\")]
    public void Caminho_do_Linux_vira_caminho_do_Explorer(string linux, string windows)
    {
        Assert.Equal(windows, WslPaths.ToUnc("Ubuntu", linux, "/home/daniel"));
    }

    [Theory]
    [InlineData("~/.claude", "\"$HOME\"'/.claude'")]
    [InlineData("~", "\"$HOME\"")]
    [InlineData("/x/it's", "'/x/it'\\''s'")]
    public void Caminho_para_o_sh_expande_o_home_e_protege_aspas(string path, string quoted)
    {
        Assert.Equal(quoted, WslPaths.ShQuote(path));
    }

    [Theory]
    [InlineData("~/.claude", "/home/daniel/.claude")]
    [InlineData("~", "/home/daniel")]
    [InlineData("Sync", "/home/daniel/Sync")]
    [InlineData("/srv/data", "/srv/data")]
    public void Caminho_absoluto_no_Linux(string path, string absolute)
    {
        Assert.Equal(absolute, WslPaths.ToAbsolute(path, "/home/daniel"));
    }
}
