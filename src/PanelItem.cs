namespace WslSyncMonitor;

public enum PanelItemKind { Distro, Syncthing, Folder, Device, IgnoredFolder, IgnoredDevice }

/// <summary>Uma linha clicável do painel (distro, Syncthing, pasta ou dispositivo) e o ID dela.</summary>
public readonly record struct PanelItem(PanelItemKind Kind, string Id);
