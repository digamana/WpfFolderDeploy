namespace auto_updater_wpf.Models;

public sealed class UiState
{
    public Dictionary<string, DeploymentJobUiState> Jobs { get; set; } = [];
}

public sealed class DeploymentJobUiState
{
    public bool OnlyLastWriteTimeDiff { get; set; }
    public bool OnlySpecificFiles { get; set; } = true;
    public bool IncludeAppSettings { get; set; }
    public Dictionary<string, bool> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
