using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using auto_updater_wpf.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace auto_updater_wpf.Services;

public sealed record DiskSpaceInfo(double? FreeSpaceGb, string? ErrorMessage = null);
public sealed record RemoteDirectoryFile(string RelativePath, string FullPath, DateTime LastWriteTime, string Sha256);

public sealed class DeploymentService
{
    private readonly string _appOfflinePath;
    private readonly string _backupScriptPath;
    private readonly string _diskSpaceScriptPath;
    private readonly string _remoteDirectoryListScriptPath;
    private readonly Action<LogEntry> _log;

    public DeploymentService(
        string appOfflinePath,
        string backupScriptPath,
        string diskSpaceScriptPath,
        string remoteDirectoryListScriptPath,
        Action<LogEntry> log)
    {
        _appOfflinePath = appOfflinePath;
        _backupScriptPath = backupScriptPath;
        _diskSpaceScriptPath = diskSpaceScriptPath;
        _remoteDirectoryListScriptPath = remoteDirectoryListScriptPath;
        _log = log;
    }

    /// <summary>
    /// 依照指定檔案路徑內的全檔案列表，取得遠端缺少的檔案或遠端檔案最後修改時間不同的檔案列表
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<DeploymentFile>>> FindUpdateFilesAsync(
        EnvironmentConfig config,
        IReadOnlyCollection<Server> selectedServers,
        bool onlyLastWriteTimeDiff,
        bool includeAppSettings,
        IProgress<string> status,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var localFiles = DeploymentFile.GetFiles(config.LocalPath);
            if (!includeAppSettings)
            {
                localFiles = localFiles
                    .Where(file => !string.Equals(file.FileName, "appsettings.json", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var serverFilesMap = new ConcurrentDictionary<string, IReadOnlyList<DeploymentFile>>(StringComparer.OrdinalIgnoreCase);

            // 如果不比較差異 (全檔覆蓋)，就直接把完整清單派給所有選中的伺服器
            if (!onlyLastWriteTimeDiff)
            {
                status.Report($"使用完整清單：{localFiles.Count} 個檔案");
                foreach (var server in selectedServers)
                {
                    serverFilesMap[server.Ip] = localFiles;
                }
                return (IReadOnlyDictionary<string, IReadOnlyList<DeploymentFile>>)serverFilesMap;
            }

            Parallel.ForEach(selectedServers, server =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                status.Report($"檢查 {server.Ip}");
                var localServerBag = new List<DeploymentFile>();

                try
                {
                    var connectionInfo = CreateConnectionInfo(server.Ip, server.Username, server.Password);
                    using var sshClient = GetConnectedSshClient(connectionInfo);
                    using var sftp = GetConnectedSftpClient(connectionInfo);

                    var remoteRoot = config.RemotePath.ToFtpPath().TrimEnd('/');

                    // 1.【效能優化核心】建立記憶體快取字典 (Key: 相對路徑, Value: 最後修改時間)
                    var remoteFileCache = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

                    status.Report($"{server.Ip} 正在一次性讀取遠端檔案清單...");

                    // 修改遞迴方法：傳入 currentFullPath 給 SFTP 讀取，傳入 currentRelativePath 供字典 Key 使用
                    void BuildRemoteFileCache(string currentFullPath, string currentRelativePath)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            if (!sftp.Exists(currentFullPath)) return;
                            var files = sftp.ListDirectory(currentFullPath);
                            foreach (var item in files)
                            {
                                if (item.Name == "." || item.Name == "..") continue;

                                // 組合相對路徑，確保全部都是 "/" 開頭 (例如: /OSSAP2_AutoUpdateFolder/DataAuthentication.dll)
                                string itemRelativePath = $"{currentRelativePath}/{item.Name}";

                                if (item.IsDirectory)
                                {
                                    BuildRemoteFileCache(item.FullName, itemRelativePath);
                                }
                                else if (item.IsRegularFile)
                                {
                                    // 記錄「相對路徑」與時間，徹底擺脫 /C:/ 的干擾
                                    remoteFileCache[itemRelativePath] = item.LastWriteTime;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log(new LogEntry { Scope = $"{config.DisplayName} / {server.Ip}", Level = "WARN", Message = $"讀取目錄異常：{ex.Message}" });
                        }
                    }

                    // 初始呼叫：相對路徑給空字串 ""
                    BuildRemoteFileCache(remoteRoot, "");
                    status.Report($"{server.Ip} 進行記憶體差異比對中...");

                    foreach (var file in localFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // 🔥 將本機的 RelativePath 統一轉換為 / 開頭的相對路徑格式
                        var lookupKey = file.RelativePath.Replace('\\', '/');
                        if (!lookupKey.StartsWith("/")) lookupKey = "/" + lookupKey;

                        // 🔥 使用「相對路徑」向字典尋找檔案，完美配對！
                        var remoteExists = remoteFileCache.TryGetValue(lookupKey, out var remoteLastWriteTime);

                        var localUtc = File.GetLastWriteTimeUtc(file.FullPath);
                        var localTrimmed = new DateTime(localUtc.Year, localUtc.Month, localUtc.Day, localUtc.Hour, localUtc.Minute, localUtc.Second, DateTimeKind.Utc);

                        var isMissing = !remoteExists;
                        var isDateDiff = false;

                        if (remoteExists)
                        {
                            var remoteUtc = remoteLastWriteTime.ToUniversalTime();
                            var remoteTrimmed = new DateTime(remoteUtc.Year, remoteUtc.Month, remoteUtc.Day, remoteUtc.Hour, remoteUtc.Minute, remoteUtc.Second, DateTimeKind.Utc);
                            isDateDiff = localTrimmed != remoteTrimmed;
                        }

                        if (isMissing || isDateDiff)
                        {
                            localServerBag.Add(file);
                        }
                    }

                    // 將這台伺服器專屬的差異清單存入字典
                    serverFilesMap[server.Ip] = localServerBag;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    status.Report($"{server.Ip} 檢查失敗：{ex.Message}");
                    _log(new LogEntry { Scope = $"{config.DisplayName} / {server.Ip}", Level = "ERROR", Message = $"檢查失敗：{ex.Message}" });
                    // 如果這台失敗，存入空清單以避免發佈時拋錯
                    serverFilesMap[server.Ip] = new List<DeploymentFile>();
                }
            });

            var totalFilesAcrossAllServers = serverFilesMap.Values.Sum(v => v.Count);
            status.Report($"差異比對完成，所有伺服器總計需更新：{totalFilesAcrossAllServers} 個項目");

            return (IReadOnlyDictionary<string, IReadOnlyList<DeploymentFile>>)serverFilesMap;
        }, cancellationToken);
    }

    /// <summary>
    /// 依照指定檔案路徑內的特殊檔案列表，取得遠端缺少的檔案或遠端檔案最後修改時間不同的檔案列表
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<DeploymentFile>>> FindQuickUpdateFilesAsync(
        EnvironmentConfig config,
        IReadOnlyCollection<Server> selectedServers,
        bool includeAppSettings,
        IReadOnlyCollection<string> specificFileNames,
        IProgress<string> status,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var filePaths = DeploymentFile.GetFiles(config.LocalPath);
            if (!includeAppSettings)
            {
                filePaths = filePaths
                    .Where(file => !string.Equals(file.FileName, "appsettings.json", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var specificSet = specificFileNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var serverFilesMap = new ConcurrentDictionary<string, IReadOnlyList<DeploymentFile>>(StringComparer.OrdinalIgnoreCase);

            Parallel.ForEach(selectedServers, server =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                status.Report($"QuickUpdate 檢查 {server.Ip}");
                var localServerMissingFiles = new List<DeploymentFile>();

                try
                {
                    var connectionInfo = CreateConnectionInfo(server.Ip, server.Username, server.Password);
                    using var sshClient = GetConnectedSshClient(connectionInfo);
                    using var sftp = GetConnectedSftpClient(connectionInfo);

                    var remoteRoot = config.RemotePath.ToFtpPath().TrimEnd('/');

                    // 1. 建立記憶體快取字典 (Key: 相對路徑, Value: 最後修改時間)
                    var remoteFileCache = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

                    status.Report($"{server.Ip} 正在一次性讀取遠端檔案清單...");

                    void BuildRemoteFileCache(string currentFullPath, string currentRelativePath)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            if (!sftp.Exists(currentFullPath)) return;
                            var files = sftp.ListDirectory(currentFullPath);
                            foreach (var item in files)
                            {
                                if (item.Name == "." || item.Name == "..") continue;

                                // 組合出乾淨的相對路徑 (例如: /wwwroot/template/index.html)
                                string itemRelativePath = $"{currentRelativePath}/{item.Name}";

                                if (item.IsDirectory)
                                {
                                    BuildRemoteFileCache(item.FullName, itemRelativePath);
                                }
                                else if (item.IsRegularFile)
                                {
                                    remoteFileCache[itemRelativePath] = item.LastWriteTime;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log(new LogEntry { Scope = $"{config.DisplayName} / {server.Ip}", Level = "WARN", Message = $"讀取目錄異常：{ex.Message}" });
                        }
                    }

                    // 執行遞迴抓取 (初始相對路徑傳入空字串)
                    BuildRemoteFileCache(remoteRoot, "");
                    status.Report($"{server.Ip} 進行記憶體差異比對中...");

                    foreach (var filePath in filePaths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // 統一轉換為 / 開頭的相對路徑格式
                        var lookupKey = filePath.RelativePath.Replace('\\', '/');
                        if (!lookupKey.StartsWith("/")) lookupKey = "/" + lookupKey;

                        // 使用「相對路徑」向字典尋找檔案
                        var remoteExists = remoteFileCache.TryGetValue(lookupKey, out var remoteLastWriteTime);

                        // 條件 1：如果遠端不存在，就直接加入缺漏清單
                        if (!remoteExists)
                        {
                            localServerMissingFiles.Add(filePath);
                            continue; // 已經加入就不必往下比對時間
                        }

                        // 條件 2：針對 template 資料夾，進行精準「秒數」比對
                        if (lookupKey.Contains("/wwwroot/template", StringComparison.OrdinalIgnoreCase))
                        {
                            var localUtc = File.GetLastWriteTimeUtc(filePath.FullPath);
                            var localTrimmed = new DateTime(localUtc.Year, localUtc.Month, localUtc.Day, localUtc.Hour, localUtc.Minute, localUtc.Second, DateTimeKind.Utc);

                            var remoteUtc = remoteLastWriteTime.ToUniversalTime();
                            var remoteTrimmed = new DateTime(remoteUtc.Year, remoteUtc.Month, remoteUtc.Day, remoteUtc.Hour, remoteUtc.Minute, remoteUtc.Second, DateTimeKind.Utc);

                            if (localTrimmed != remoteTrimmed)
                            {
                                localServerMissingFiles.Add(filePath);
                            }
                        }
                    }

                    // 結算這台伺服器的專屬檔案 (指定必更的 specific files + 缺漏與修改過的 missing files)
                    var serverFinalFiles = filePaths.Where(file => specificSet.Contains(file.FileName)).ToList();
                    serverFinalFiles.AddRange(localServerMissingFiles);
                    serverFilesMap[server.Ip] = serverFinalFiles.Distinct().ToList();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    status.Report($"{server.Ip} QuickUpdate 檢查失敗：{ex.Message}");
                    _log(new LogEntry { Scope = $"{config.DisplayName} / {server.Ip}", Level = "ERROR", Message = $"QuickUpdate 檢查失敗：{ex.Message}" });
                    serverFilesMap[server.Ip] = new List<DeploymentFile>();
                }
            });

            var totalFilesAcrossAllServers = serverFilesMap.Values.Sum(v => v.Count);
            status.Report($"QuickUpdate 檢查完成，共需更新 {totalFilesAcrossAllServers} 個項目");
            return (IReadOnlyDictionary<string, IReadOnlyList<DeploymentFile>>)serverFilesMap;
        }, cancellationToken);
    }

    /// <summary>
    /// 發布檔案到遠端伺服器 (20260626NEW 升級為精準發佈模式)
    /// </summary>
    public async Task PublishAsync(
        EnvironmentConfig config,
        IReadOnlyCollection<Server> selectedServers,
        IReadOnlyDictionary<string, IReadOnlyList<DeploymentFile>> serverFilesMap, // 參數型別升級為 Dictionary
        IProgress<int> progress,
        IProgress<string> status,
        IProgress<PublishFileRecord>? publishFileProgress,
        CancellationToken cancellationToken)
    {
        var totalFilesToDeploy = serverFilesMap.Values.Sum(v => v.Count);
        if (totalFilesToDeploy == 0)
        {
            status.Report("沒有檔案需要發佈");
            return;
        }

        await Task.Run(() =>
        {
            Parallel.ForEach(selectedServers, server =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scope = $"{config.DisplayName} / {server.Ip}";

                // 如果這台伺服器不在字典內，或專屬清單為空，代表它完全沒有差異，直接跳過不連線
                if (!serverFilesMap.TryGetValue(server.Ip, out var filesForThisServer) || filesForThisServer.Count == 0)
                {
                    _log(new LogEntry { Scope = scope, Message = "這台伺服器已是最新狀態，無需更新。" });
                    return;
                }

                var stopwatch = Stopwatch.StartNew();

                try
                {
                    var connectionInfo = CreateConnectionInfo(server.Ip, server.Username, server.Password);
                    using var sshClient = GetConnectedSshClient(connectionInfo);
                    using var sftp = GetConnectedSftpClient(connectionInfo);

                    var remoteRoot = config.RemotePath.ToFtpPath().TrimEnd('/') + "/";
                    var appOfflineRemotePath = remoteRoot + "app_offline.htm";

                    try
                    {
                        status.Report($"{server.Ip} 建立 app_offline.htm");
                        UploadAppOffline(sftp, appOfflineRemotePath);

                        // 這裡只跑屬於「這台伺服器」的專屬清單
                        foreach (var file in filesForThisServer)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var remoteFilePath = remoteRoot.TrimEnd('/') + file.RelativePath;
                            UploadFileWithRetry(sftp, server.Ip, remoteFilePath, file, scope, cancellationToken);

                            progress.Report(1); // 回報整體進度加 1

                            publishFileProgress?.Report(new PublishFileRecord
                            {
                                Ip = server.Ip,
                                FileName = file.FileName,
                                RelativePath = file.RelativePath,
                                RemotePath = remoteFilePath,
                            });
                        }
                    }
                    finally
                    {
                        if (sftp.IsConnected && sftp.Exists(appOfflineRemotePath))
                        {
                            sftp.DeleteFile(appOfflineRemotePath);
                        }
                    }

                    stopwatch.Stop();
                    _log(new LogEntry
                    {
                        Scope = scope,
                        Message = $"發布完成 (共 {filesForThisServer.Count} 個檔案)，耗時 {stopwatch.Elapsed.TotalSeconds:N1} 秒",
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    status.Report($"{server.Ip} 發布失敗：{ex.Message}");
                    _log(new LogEntry { Scope = scope, Level = "ERROR", Message = $"發布失敗：{ex.Message}" });
                }
            });
        }, cancellationToken);
    }

    /// <summary>
    /// 備份遠端伺服器的指定資料夾到備份路徑
    /// </summary>
    /// <param name="config"></param>
    /// <param name="selectedServers"></param>
    /// <param name="progress"></param>
    /// <param name="status"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task BackupAsync(
        EnvironmentConfig config,
        IReadOnlyCollection<Server> selectedServers,
        IProgress<int> progress,
        IProgress<string> status,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            Parallel.ForEach(selectedServers, server =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scope = $"{config.DisplayName} / {server.Ip}";
                status.Report($"{server.Ip} 備份中");

                try
                {
                    var connectionInfo = CreateConnectionInfo(server.Ip, server.Username, server.Password);
                    using var sshClient = GetConnectedSshClient(connectionInfo);
                    using var sftp = GetConnectedSftpClient(connectionInfo);

                    var scriptPath = Path.Combine(config.BackupRemotePath.GetUntilBackupPath(), "BackupScript.ps1");
                    var scriptFtpPath = scriptPath.ToFtpPath();

                    try
                    {
                        if (sftp.Exists(scriptFtpPath))
                        {
                            sftp.DeleteFile(scriptFtpPath);
                        }

                        using (var stream = new FileStream(_backupScriptPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            sftp.UploadFile(stream, scriptFtpPath);
                        }

                        var commandText =
                            $"powershell -ExecutionPolicy Bypass -Command \"[console]::InputEncoding = [console]::OutputEncoding = [Text.UTF8Encoding]::UTF8; & '{scriptPath}' -SourcePath '{config.RemotePath}' -DestinationRoot '{config.BackupRemotePath}'\"";
                        var result = sshClient.CreateCommand(commandText).Execute();
                        _log(new LogEntry { Scope = scope, Message = $"備份完成 {result}".Trim() });
                    }
                    finally
                    {
                        // 確保安全刪除腳本
                        if (sftp.IsConnected && sftp.Exists(scriptFtpPath))
                        {
                            sftp.DeleteFile(scriptFtpPath);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    status.Report($"{server.Ip} 備份失敗：{ex.Message}");
                    // 修復：確實記錄備份失敗的 LOG
                    _log(new LogEntry { Scope = scope, Level = "ERROR", Message = $"備份失敗：{ex.Message}" });
                }
                finally
                {
                    progress.Report(1);
                }
            });
        }, cancellationToken);
    }

    /// <summary>
    /// 上傳 app_offline.htm 到遠端伺服器，讓 ASP.NET Core 應用程式進入離線模式
    /// </summary>
    /// <param name="sftp"></param>
    /// <param name="remotePath"></param>
    private void UploadAppOffline(SftpClient sftp, string remotePath)
    {
        using var stream = new FileStream(_appOfflinePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        sftp.UploadFile(stream, remotePath);
    }

    /// <summary>
    /// 測試連線到遠端伺服器，並取得遠端硬碟剩餘空間資訊
    /// </summary>
    /// <param name="server"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<DiskSpaceInfo> TestConnectionAsync(Server server, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // 1. 使用通用方法取得連線設定 (已內建處理空白字元、純密碼驗證、60秒 Timeout)
                var connectionInfo = CreateConnectionInfo(server.Ip, server.Username, server.Password);

                // 2. 建立並連接 SSH 與 SFTP (已內建 KeepAlive、自動信任指紋與 Connect)
                using var sshClient = GetConnectedSshClient(connectionInfo);
                using var sftp = GetConnectedSftpClient(connectionInfo);

                // SFTP 只要能連上就算過關，馬上斷開節省資源
                sftp.Disconnect();

                // ==========================================
                // 以下為獨立嘗試取得硬碟容量 (PowerShell 執行)
                // ==========================================
                var script = File.ReadAllText(_diskSpaceScriptPath);
                var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

                var result = sshClient
                    .CreateCommand($"powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedScript}")
                    .Execute()
                    .Trim();

                // 執行完畢手動斷開連線
                sshClient.Disconnect();

                if (!double.TryParse(result, NumberStyles.Float, CultureInfo.InvariantCulture, out var freeSpaceGb))
                {
                    return new DiskSpaceInfo(null, $"讀取異常：{result}");
                }

                return new DiskSpaceInfo(freeSpaceGb);
            }
            catch (Exception ex)
            {
                // 捕捉到任何連線或檢查錯誤，不再阻斷外層，而是優雅回傳錯誤訊息
                return new DiskSpaceInfo(null, ex.Message);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 下載遠端檔案到本機資料夾，並可選擇指定本機檔名
    /// </summary>
    /// <param name="configs"></param>
    /// <param name="ip"></param>
    /// <param name="remotePath"></param>
    /// <param name="localFolderPath"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="localFileName"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="FileNotFoundException"></exception>
    public async Task<string> DownloadFileAsync(
        IEnumerable<EnvironmentConfig> configs,
        string ip,
        string remotePath,
        string localFolderPath,
        CancellationToken cancellationToken,
        string? localFileName = null)
    {
        var server = configs
            .SelectMany(config => config.Server)
            .FirstOrDefault(server => server.Ip.Equals(ip.Trim(), StringComparison.OrdinalIgnoreCase));

        if (server is null)
        {
            throw new InvalidOperationException($"找不到 IP {ip} 的連線設定");
        }

        remotePath = remotePath.Trim().Trim('"');
        localFolderPath = localFolderPath.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(remotePath))
        {
            throw new InvalidOperationException("請輸入遠端檔案路徑");
        }

        if (string.IsNullOrWhiteSpace(localFolderPath))
        {
            throw new InvalidOperationException("請輸入本機資料夾路徑");
        }

        var fileName = Path.GetFileName(remotePath.Replace('/', '\\'));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("遠端路徑必須包含檔名");
        }

        Directory.CreateDirectory(localFolderPath);
        var localPath = Path.Combine(localFolderPath, string.IsNullOrWhiteSpace(localFileName) ? fileName : localFileName);
        var remoteFtpPath = remotePath.ToFtpPath();

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var connectionInfo = CreateConnectionInfo(server.Ip, server.Username, server.Password);
            using var sshClient = GetConnectedSshClient(connectionInfo);
            using var sftp = GetConnectedSftpClient(connectionInfo);

            if (!sftp.Exists(remoteFtpPath))
            {
                throw new FileNotFoundException($"遠端檔案不存在：{remotePath}");
            }

            using var stream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
            sftp.DownloadFile(remoteFtpPath, stream);
            sftp.Disconnect();
            sshClient.Disconnect();
        }, cancellationToken);

        return localPath;
    }

    public async Task<IReadOnlyList<RemoteDirectoryFile>> ListRemoteDirectoryFilesAsync(
        IEnumerable<EnvironmentConfig> configs,
        string ip,
        string remoteFolderPath,
        CancellationToken cancellationToken)
    {
        var server = configs
            .SelectMany(config => config.Server)
            .FirstOrDefault(server => server.Ip.Equals(ip.Trim(), StringComparison.OrdinalIgnoreCase));

        if (server is null)
        {
            throw new InvalidOperationException($"找不到 IP {ip} 的連線設定");
        }

        remoteFolderPath = remoteFolderPath.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(remoteFolderPath))
        {
            throw new InvalidOperationException("請輸入遠端資料夾位置");
        }

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootPath = remoteFolderPath.Replace('/', '\\').TrimEnd('\\');

            var connectionInfo = CreateConnectionInfo(server.Ip, server.Username, server.Password);
            using var sshClient = GetConnectedSshClient(connectionInfo);

            var escapedRootPath = rootPath.Replace("'", "''");
            var script = $"$RootPath = '{escapedRootPath}'{Environment.NewLine}" +
                         File.ReadAllText(_remoteDirectoryListScriptPath);
            var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var command = sshClient.CreateCommand($"powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedScript}");
            var result = command.Execute();
            sshClient.Disconnect();
            if (command.ExitStatus != 0)
            {
                throw new InvalidOperationException($"遠端列檔失敗：{command.Error}");
            }

            return (IReadOnlyList<RemoteDirectoryFile>)result
                .Split([Environment.NewLine, "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t'))
                .Where(parts => parts.Length == 4 && DateTime.TryParse(parts[2], out _))
                .Select(parts => new RemoteDirectoryFile(
                    parts[0],
                    parts[1],
                    DateTime.Parse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    parts[3]))
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken);
    }

    public async Task<int> DownloadDirectoryFilesAsync(
        Server server,
        string remoteFolderPath,
        string localFolderPath,
        string localFilePrefix,
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken)
    {
        remoteFolderPath = remoteFolderPath.Trim().Trim('"');
        localFolderPath = localFolderPath.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(remoteFolderPath))
        {
            throw new InvalidOperationException("請輸入遠端LOG資料夾");
        }

        if (string.IsNullOrWhiteSpace(localFolderPath))
        {
            throw new InvalidOperationException("請輸入本機資料夾路徑");
        }

        Directory.CreateDirectory(localFolderPath);
        var remoteFtpFolderPath = remoteFolderPath.ToFtpPath().TrimEnd('/');
        var safePrefix = string.Join("_", localFilePrefix.Split(Path.GetInvalidFileNameChars()));

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var connectionInfo = CreateConnectionInfo(server.Ip, server.Username, server.Password);
            using var sshClient = GetConnectedSshClient(connectionInfo);
            using var sftp = GetConnectedSftpClient(connectionInfo);

            if (!sftp.Exists(remoteFtpFolderPath))
            {
                throw new DirectoryNotFoundException($"遠端LOG資料夾不存在：{remoteFolderPath}");
            }

            var startDateTime = startDate.Date;
            var endDateExclusive = endDate.Date.AddDays(1);
            var remoteFiles = sftp.ListDirectory(remoteFtpFolderPath)
                .Where(file =>
                    file.LastWriteTime >= startDateTime &&
                    file.LastWriteTime < endDateExclusive)
                .OrderBy(file => file.LastWriteTime)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var count = 0;
            foreach (var file in remoteFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var localPath = Path.Combine(localFolderPath, $"{safePrefix}_{file.Name}");
                using var stream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
                sftp.DownloadFile(file.FullName, stream);
                File.SetCreationTime(localPath, file.LastWriteTime);
                File.SetLastWriteTime(localPath, file.LastWriteTime);
                count++;
            }

            sftp.Disconnect();
            sshClient.Disconnect();
            return count;
        }, cancellationToken);
    }

    private void UploadFileWithRetry(
        SftpClient sftp,
        string serverIp,
        string remoteFilePath,
        DeploymentFile file,
        string scope,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var before = sftp.SafeGetLastWriteTime(remoteFilePath);
                EnsureDirectoryExists(sftp, Path.GetDirectoryName(remoteFilePath)?.Replace("\\", "/") ?? "/");

                using var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                sftp.UploadFile(stream, remoteFilePath);

                var localLastModified = File.GetLastWriteTime(file.FullPath);
                sftp.SetLastWriteTime(remoteFilePath, localLastModified);
                var after = sftp.SafeGetLastWriteTime(remoteFilePath);

                _log(new LogEntry
                {
                    Scope = scope,
                    Message = $"{file.RelativePath} 上傳完成 ({before:yyyy-MM-dd HH:mm:ss} -> {after:yyyy-MM-dd HH:mm:ss})",
                });
                return;
            }
            catch (SshConnectionException ex)
            {
                _log(new LogEntry { Scope = scope, Level = "ERROR", Message = $"連線中斷：{ex.Message}" });
                throw;
            }
            catch (Exception ex) when (started.Elapsed < TimeSpan.FromMinutes(5))
            {
                _log(new LogEntry { Scope = scope, Level = "WARN", Message = $"{serverIp} {file.FileName} 重試：{ex.Message}" });
                Thread.Sleep(1000);
            }
        }
    }

    private static void EnsureDirectoryExists(SftpClient sftp, string remoteDirectory)
    {
        if (string.IsNullOrWhiteSpace(remoteDirectory) || sftp.Exists(remoteDirectory))
        {
            return;
        }

        var current = "";
        foreach (var part in remoteDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            if (!sftp.Exists(current))
            {
                sftp.CreateDirectory(current);
            }
        }
    }


    private ConnectionInfo CreateConnectionInfo(string ip, string username, string password, int timeoutSeconds = 60)
    {
        string safeUsername = username?.Trim() ?? string.Empty;
        string safePassword = password ?? string.Empty;

        // 強制使用純密碼驗證，避免 Windows OpenSSH 驗證死鎖
        var passwordAuth = new PasswordAuthenticationMethod(safeUsername, safePassword);

        return new ConnectionInfo(ip, 22, safeUsername, passwordAuth)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
    }

    private SshClient GetConnectedSshClient(ConnectionInfo connectionInfo)
    {
        var sshClient = new SshClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30) // 防止防火牆靜默切斷
        };
        sshClient.HostKeyReceived += (sender, e) => { e.CanTrust = true; }; // 自動信任指紋
        sshClient.Connect();
        return sshClient;
    }

    private SftpClient GetConnectedSftpClient(ConnectionInfo connectionInfo)
    {
        var sftpClient = new SftpClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(10) // 防止傳檔大檔時中斷
        };
        sftpClient.HostKeyReceived += (sender, e) => { e.CanTrust = true; }; // 自動信任指紋
        sftpClient.Connect();
        return sftpClient;
    }
}
