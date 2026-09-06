using System.IO;

namespace auto_updater_wpf.Services;

public sealed class DownloadFileSettings
{
    public string RemoteIp { get; set; } = string.Empty;
    public string RemoteFilePath { get; set; } = string.Empty;
    public List<string> RemoteFilePaths { get; set; } = [];
    public string LocalFolderPath { get; set; } = string.Empty;
}

public sealed class DownloadFileSettingsStore
{
    private readonly string _path;

    public DownloadFileSettingsStore(string baseDirectory)
    {
        _path = System.IO.Path.Combine(baseDirectory, "DownloadFileSettings.ini");
    }

    public string Path => _path;

    public DownloadFileSettings Load()
    {
        var settings = new DownloadFileSettings();
        if (!File.Exists(_path))
        {
            return settings;
        }

        foreach (var line in File.ReadAllLines(_path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('['))
            {
                continue;
            }

            var index = trimmed.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = trimmed[..index].Trim();
            var value = trimmed[(index + 1)..].Trim();
            if (key.Equals("RemoteIp", StringComparison.OrdinalIgnoreCase))
            {
                settings.RemoteIp = value;
            }
            else if (key.Equals("RemoteFilePath", StringComparison.OrdinalIgnoreCase))
            {
                settings.RemoteFilePath = value;
            }
            else if (key.StartsWith("RemoteFilePath.", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(value) &&
                     !settings.RemoteFilePaths.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                settings.RemoteFilePaths.Add(value);
            }
            else if (key.Equals("LocalFolderPath", StringComparison.OrdinalIgnoreCase))
            {
                settings.LocalFolderPath = value;
            }
        }

        return settings;
    }

    public void Save(DownloadFileSettings settings)
    {
        var lines = new List<string>
        {
            "; Auto-saved settings for the Download Specific File tab.",
            "[DownloadSpecificFile]",
            $"RemoteIp={settings.RemoteIp}",
            $"RemoteFilePath={settings.RemoteFilePath}",
            $"LocalFolderPath={settings.LocalFolderPath}",
        };

        for (var index = 0; index < settings.RemoteFilePaths.Count; index++)
        {
            lines.Add($"RemoteFilePath.{index + 1}={settings.RemoteFilePaths[index]}");
        }

        File.WriteAllLines(_path, lines);
    }
}
