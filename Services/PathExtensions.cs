using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace auto_updater_wpf.Services;

public static class PathExtensions
{
    public static string ToFtpPath(this string path)
    {
        var normalizedPath = path.Trim().Trim('"').Replace("\\", "/");
        if (normalizedPath.Length >= 3 &&
            char.IsLetter(normalizedPath[0]) &&
            normalizedPath[1] == ':' &&
            normalizedPath[2] == '/')
        {
            return normalizedPath[2..];
        }

        return normalizedPath;
    }

    public static string GetUntilBackupPath(this string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        const string keyword = "Backup";
        var index = path.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return string.Empty;
        }

        var slashAfter = path.IndexOf('/', index);
        if (slashAfter < 0)
        {
            slashAfter = path.Length - 1;
        }

        return path[..(slashAfter + 1)];
    }

    public static ISftpFile? SafeGet(this SftpClient sftpClient, string remoteFilePath)
    {
        return sftpClient.Exists(remoteFilePath) ? sftpClient.Get(remoteFilePath) : null;
    }

    public static DateTime? SafeGetLastWriteTime(this SftpClient sftpClient, string remoteFilePath)
    {
        return sftpClient.SafeGet(remoteFilePath)?.LastWriteTime;
    }
}
