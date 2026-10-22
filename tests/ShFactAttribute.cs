namespace WslSyncMonitor.Tests;

/// <summary>
/// Teste que executa os scripts no sh do Linux, como o app faz dentro do WSL. No Windows (CI, Git Bash) os caminhos
/// (C:/…) e as permissões de arquivo são outros, então o teste é pulado lá; ele roda no job de Linux e no pre-commit.
/// </summary>
public sealed class ShFactAttribute : FactAttribute
{
    public ShFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "executa o sh do Linux (no app, os scripts rodam dentro do WSL)";
    }
}
