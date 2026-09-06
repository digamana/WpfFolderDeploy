using System.IO;

namespace auto_updater_wpf.Services;

public sealed class DiffCompareSettingsItem
{
    public string Ip { get; set; } = string.Empty;
    public string RemoteFilePath { get; set; } = string.Empty;
    public string LocalFilePath { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

public sealed class DiffCompareSettingsStore
{
    private readonly string _path;

    public DiffCompareSettingsStore(string baseDirectory)
    {
        _path = System.IO.Path.Combine(baseDirectory, "DiffCompareSettings.ini");
    }

    public IReadOnlyList<DiffCompareSettingsItem> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var items = new List<DiffCompareSettingsItem>();
        DiffCompareSettingsItem? current = null;
        foreach (var line in File.ReadAllLines(_path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (current is not null)
                {
                    items.Add(current);
                }

                current = trimmed.Equals("[DiffCompare]", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : new DiffCompareSettingsItem();
                continue;
            }

            if (current is null)
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
            if (key.Equals("Ip", StringComparison.OrdinalIgnoreCase))
            {
                current.Ip = value;
            }
            else if (key.Equals("RemoteFilePath", StringComparison.OrdinalIgnoreCase))
            {
                current.RemoteFilePath = value;
            }
            else if (key.Equals("LocalFilePath", StringComparison.OrdinalIgnoreCase))
            {
                current.LocalFilePath = value;
            }
            else if (key.Equals("Note", StringComparison.OrdinalIgnoreCase))
            {
                current.Note = value;
            }
        }

        if (current is not null)
        {
            items.Add(current);
        }

        return items;
    }

    public void Save(IEnumerable<DiffCompareSettingsItem> items)
    {
        var lines = new List<string>
        {
            "; Auto-saved settings for the Diff Compare tab.",
            "[DiffCompare]",
        };

        var index = 1;
        foreach (var item in items)
        {
            lines.Add(string.Empty);
            lines.Add($"[Item{index}]");
            lines.Add($"Ip={item.Ip}");
            lines.Add($"RemoteFilePath={item.RemoteFilePath}");
            lines.Add($"LocalFilePath={item.LocalFilePath}");
            lines.Add($"Note={item.Note}");
            index++;
        }

        File.WriteAllLines(_path, lines);
    }
}
