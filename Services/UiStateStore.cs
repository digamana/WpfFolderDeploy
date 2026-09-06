using System.IO;
using System.Text.Json;
using auto_updater_wpf.Models;

namespace auto_updater_wpf.Services;

public sealed class UiStateStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public UiStateStore(string baseDirectory)
    {
        _path = System.IO.Path.Combine(baseDirectory, "UiState.json");
    }

    public string Path => _path;

    public UiState Load()
    {
        if (!File.Exists(_path))
        {
            return new UiState();
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<UiState>(json, _options) ?? new UiState();
        }
        catch
        {
            return new UiState();
        }
    }

    public void Save(UiState state)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(state, _options));
    }
}
