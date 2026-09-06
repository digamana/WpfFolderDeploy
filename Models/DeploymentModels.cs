using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace auto_updater_wpf.Models;

public sealed class EnvSetting
{
    [JsonPropertyName("EnvSetting")]
    public List<EnvironmentConfig> EnvironmentConfigs { get; set; } = [];

    public static EnvSetting Load(string path)
    {
        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        return JsonSerializer.Deserialize<EnvSetting>(json, options) ?? new EnvSetting();
    }
}

public sealed class EnvironmentConfig
{
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("projName")]
    public string ProjectName { get; set; } = string.Empty;

    public string Memo { get; set; } = string.Empty;

    [JsonPropertyName("osswebClint_local")]
    public string LocalPath { get; set; } = string.Empty;

    [JsonPropertyName("osswebClint_remote")]
    public string RemotePath { get; set; } = string.Empty;

    [JsonPropertyName("osswebClint_backup_remote")]
    public string BackupRemotePath { get; set; } = string.Empty;

    public List<Server> Server { get; set; } = [];

    [JsonIgnore]
    public string DisplayName => $"{Type} - {ProjectName}";
}

public sealed class Server
{
    [JsonPropertyName("ip")]
    public string Ip { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

public sealed class DeploymentFile : IEquatable<DeploymentFile>
{
    public string Ip { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public DateTime? LastWriteTime { get; set; }

    public static List<DeploymentFile> GetFiles(string localPath)
    {
        if (!Directory.Exists(localPath))
        {
            return [];
        }

        var root = Path.GetFullPath(localPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
            .Select(file => new DeploymentFile
            {
                FileName = Path.GetFileName(file),
                FullPath = file,
                LastWriteTime = File.GetLastWriteTime(file),
                RelativePath = "/" + Path.GetRelativePath(root, file).Replace("\\", "/"),
            })
            .ToList();
    }

    public bool Equals(DeploymentFile? other)
    {
        return other is not null
            && string.Equals(FileName, other.FileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(RelativePath, other.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => Equals(obj as DeploymentFile);

    public override int GetHashCode()
    {
        return HashCode.Combine(
            FileName.ToUpperInvariant(),
            RelativePath.ToUpperInvariant());
    }
}

public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Scope { get; init; } = string.Empty;
    public string Level { get; init; } = "INFO";
    public string Message { get; init; } = string.Empty;
}

public sealed class PublishFileRecord
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Ip { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string RemotePath { get; init; } = string.Empty;
}
