namespace WslSyncMonitor;

/// <summary>Pergunta se o app deve se instalar (atalhos e início com o Windows).</summary>
public sealed class InstallForm : Form
{
    readonly CheckBox _desktop = new() { Text = "Atalho na área de trabalho", Checked = true, AutoSize = true };
    readonly CheckBox _startMenu = new() { Text = "Atalho no menu Iniciar", Checked = true, AutoSize = true };
    readonly CheckBox _autostart = new() { Text = "Iniciar com o Windows", Checked = true, AutoSize = true };
    readonly CheckBox _dontAsk = new() { Text = "Não perguntar de novo", AutoSize = true };

    public InstallOptions Options => new(_desktop.Checked, _startMenu.Checked, _autostart.Checked);
    public bool DontAskAgain => _dontAsk.Checked;

    public InstallForm(string installDir, bool offerDontAsk)
    {
        Text = "Instalar o WSL Sync Monitor";
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(16), WrapContents = false };
        layout.Controls.Add(new Label
        {
            Text = $"Instalar em:\n{installDir}\n\nA instalação é só para o seu usuário (não pede administrador), aparece em\n" +
                   "\"Aplicativos instalados\" para desinstalar e recebe as atualizações automaticamente.",
            AutoSize = true, Margin = new Padding(0, 0, 0, 12),
        });
        layout.Controls.AddRange([_desktop, _startMenu, _autostart]);
        if (offerDontAsk) layout.Controls.Add(_dontAsk);

        var install = new Button { Text = "Instalar", DialogResult = DialogResult.OK, AutoSize = true };
        var runOnly = new Button { Text = "Só executar", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange([runOnly, install]);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = install;
        CancelButton = runOnly;
    }
}

/// <summary>Ícone do app (o mesmo do .exe) para as janelas.</summary>
public static class AppIcon
{
    static Icon? _icon;
    public static Icon? Get()
    {
        try { return _icon ??= Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { return null; }
    }
}
