using System.IO;

namespace auto_updater_wpf.Services;

public sealed class SpecificFileStore
{
    private const string SectionName = "[SpecificFiles]";
    private readonly string _path;

    public SpecificFileStore(string baseDirectory)
    {
        _path = System.IO.Path.Combine(baseDirectory, "SpecificFiles.ini");
    }

    public string Path => _path;

    public List<string> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        return File.ReadAllLines(_path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith(';') && line != SectionName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(line => line)
            .ToList();
    }

    public void Save(IEnumerable<string> fileNames)
    {
        var lines = new List<string>
        {
            "; One file name per line. Auto-saved when the app closes.",
            SectionName,
        };

        lines.AddRange(fileNames
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name));

        File.WriteAllLines(_path, lines);
    }
}
