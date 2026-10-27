using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// Troca do executável na atualização e a volta para a versão anterior quando a nova não consegue iniciar: a
/// proteção da sincronização não pode ficar sem app nenhum rodando.
/// </summary>
public sealed class UpdateFilesTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("wsm-update-").FullName;
    string Exe => Path.Combine(_dir, "WslSyncMonitor.exe");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Troca_guarda_a_versao_anterior_ao_lado()
    {
        File.WriteAllText(Exe, "v1");
        Updater.SwapIn(Exe, "MZv2"u8.ToArray());
        Assert.Equal("MZv2", File.ReadAllText(Exe));
        Assert.Equal("v1", File.ReadAllText(Exe + ".old"));
        Assert.False(File.Exists(Exe + ".new"));
    }

    [Fact]
    public void Volta_para_a_versao_anterior_e_guarda_a_que_falhou()
    {
        File.WriteAllText(Exe, "v1");
        Updater.SwapIn(Exe, "MZv2"u8.ToArray());

        Assert.True(Updater.Rollback(Exe));
        Assert.Equal("v1", File.ReadAllText(Exe));
        Assert.Equal("MZv2", File.ReadAllText(Exe + ".failed"));
        Assert.False(File.Exists(Exe + ".old"));
    }

    [Fact]
    public void Sem_versao_anterior_nao_ha_para_onde_voltar()
    {
        File.WriteAllText(Exe, "v2");
        Assert.False(Updater.Rollback(Exe));
        Assert.Equal("v2", File.ReadAllText(Exe));
    }

    [Fact]
    public void Limpeza_so_depois_de_a_nova_versao_iniciar_apaga_as_sobras()
    {
        File.WriteAllText(Exe, "v2");
        foreach (var ext in new[] { ".old", ".new", ".failed" }) File.WriteAllText(Exe + ext, "x");
        Updater.CleanupOld(Exe);
        Assert.Equal(["WslSyncMonitor.exe"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }
}
