using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using auto_updater_wpf.Models;
using auto_updater_wpf.Services;

namespace auto_updater_wpf.ViewModels;

public sealed class ProjectDeploymentRow
{
    public string ProjectName { get; init; } = string.Empty;
    public ObservableCollection<ProjectDeploymentCell> Cells { get; init; } = [];
}

public sealed class ProjectDeploymentCell
{
    public DeploymentJobViewModel? Job { get; init; }
    public bool HasJob => Job is not null;
}

public sealed class LoadedLogLine
{
    public DateTime FileDate { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}

public sealed class MainViewModel : ObservableObject
{
    private const string DefaultReadmeText = """
Auto Updater WPF 使用說明

一、發布
1. 發布畫面依 EnvSetting.json 自動產生專案與環境。
2. 每個專案一列，每個環境一欄。
3. 每個 IP 預設勾選。取消勾選後，發布、備份、檢查檔案時都會略過該 IP。
4. 「測」按鈕可測試該 IP 的 SFTP 連線。
5. 「只傳特定檔案」會依 SpecificFiles.ini 的檔名清單發布，同時補上 Local 有但 Server 沒有的檔案。
6. 「只傳修改日不同檔案」未勾選「只傳特定檔案」時才生效。
7. 發布 Prod 前會跳出確認訊息。
8. 發布完成後會跳出完成訊息。

二、建立部版環境
1. 如果 C:\SVN_Release 下缺少對應發布資料夾或 PowerShell 腳本，畫面會顯示「建立部版環境」按鈕。
2. 按下後會建立空資料夾，並依 EnvSetting.json 產生發布用 ps1。
3. 若資料夾和 ps1 都存在，按鈕會隱藏。

三、備份
1. 備份按鈕在發布按鈕旁邊。
2. 備份與發布共用同一個 Job 狀態；備份中不可同時發布。
3. 備份只會對已勾選的 IP 執行。

四、特定檔案
1. 特定檔案分頁用來維護 SpecificFiles.ini。
2. 一行代表一個檔名，例如 app_offline.htm 或 AutoMapper.dll。
3. 關閉程式時會自動儲存，按新增/刪除時也會儲存。

五、LOG
1. LOG 分頁可依 projName、日期區間與關鍵字載入本機 C:\temp 下的 log。
2. 每張發布卡片也有唯讀狀態文字框，可查看最近狀態。

六、教學
1. 本分頁內容來自 README.txt。
2. 編輯後請按「儲存 README」更新檔案。
""";

    private readonly string _baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
    private readonly object _logFileLock = new();
    private readonly SpecificFileStore _specificFileStore;
    private readonly DownloadFileSettingsStore _downloadFileSettingsStore;
    private readonly DiffCompareSettingsStore _diffCompareSettingsStore;
    private readonly UiStateStore _uiStateStore;
    private readonly DeploymentService _deploymentService;
    private readonly List<EnvironmentConfig> _configs = new();
    private string _specificFileInput = string.Empty;
    private string? _selectedSpecificFile;
    private string? _selectedLogProjectName;
    private string? _selectedLogTypeName;
    private DateTime _logStartDate = DateTime.Today.AddDays(-7);
    private DateTime _logEndDate = DateTime.Today;
    private string _logFilterText = string.Empty;
    private string _readmeText = string.Empty;
    private string _downloadRemoteIp = string.Empty;
    private string _downloadRemoteFilePath = string.Empty;
    private string _downloadRemoteFilePathInput = string.Empty;
    private string _downloadLocalFolderPath = @"C:\SVN_Release";
    private string _downloadStatus = string.Empty;
    private string _logDownloadRemoteFilePath = string.Empty;
    private string _logDownloadLocalFolderPath = @"C:\temp";
    private string _logDownloadStatus = string.Empty;

    public MainViewModel()
    {
        LogFilePath = Path.Combine(_baseDirectory, "Log", $"{DateTime.Now:yyyy_MM_dd}_wpf_log.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);

        ReadmePath = Path.Combine(_baseDirectory, "README.txt");
        if (!File.Exists(ReadmePath))
        {
            File.WriteAllText(ReadmePath, DefaultReadmeText);
        }
        _readmeText = File.ReadAllText(ReadmePath);

        _specificFileStore = new SpecificFileStore(_baseDirectory);
        _downloadFileSettingsStore = new DownloadFileSettingsStore(_baseDirectory);
        _diffCompareSettingsStore = new DiffCompareSettingsStore(_baseDirectory);
        _uiStateStore = new UiStateStore(_baseDirectory);
        foreach (var fileName in _specificFileStore.Load())
        {
            SpecificFiles.Add(fileName);
        }

        var envPath = Path.Combine(_baseDirectory, "EnvSetting.json");
        _deploymentService = new DeploymentService(
            Path.Combine(_baseDirectory, "app_offline.htm"),
            Path.Combine(_baseDirectory, "BackupScript.ps1"),
            Path.Combine(_baseDirectory, "DiskSpaceScript.ps1"),
            Path.Combine(_baseDirectory, "RemoteDirectoryListScript.ps1"),
            AddLog);

        // 初始化 Reload Command
        ReloadEnvSettingCommand = new RelayCommand(() =>
        {
            LoadSettings(true);
            return Task.CompletedTask;
        });

        var downloadFileSettings = _downloadFileSettingsStore.Load();
        foreach (var remoteFilePath in downloadFileSettings.RemoteFilePaths)
        {
            DownloadRemoteFilePaths.Add(remoteFilePath);
        }
        if (!string.IsNullOrWhiteSpace(downloadFileSettings.RemoteFilePath) &&
            !DownloadRemoteFilePaths.Contains(downloadFileSettings.RemoteFilePath, StringComparer.OrdinalIgnoreCase))
        {
            DownloadRemoteFilePaths.Add(downloadFileSettings.RemoteFilePath);
        }
        DownloadRemoteFilePath = DownloadRemoteFilePaths.Contains(downloadFileSettings.RemoteFilePath, StringComparer.OrdinalIgnoreCase)
            ? downloadFileSettings.RemoteFilePath
            : DownloadRemoteFilePaths.FirstOrDefault() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(downloadFileSettings.LocalFolderPath))
        {
            DownloadLocalFolderPath = downloadFileSettings.LocalFolderPath;
        }

        // 呼叫獨立出來的載入方法 (false 代表是初次載入，不跳 MessageBox)
        LoadSettings(false);

        // 綁定其他按鈕命令
        AddSpecificFileCommand = new RelayCommand(AddSpecificFileAsync);
        RemoveSpecificFileCommand = new RelayCommand(RemoveSpecificFileAsync, () => SelectedSpecificFile is not null);
        OpenLogCommand = new RelayCommand(OpenLogConsoleAsync);
        LoadLogsCommand = new RelayCommand(LoadLogsAsync);
        SaveReadmeCommand = new RelayCommand(SaveReadmeAsync);
        DownloadSpecificFileCommand = new RelayCommand(DownloadSpecificFileAsync);
        AddDownloadRemoteFilePathCommand = new RelayCommand(AddDownloadRemoteFilePathAsync);
        RemoveDownloadRemoteFilePathCommand = new RelayCommand(
            RemoveDownloadRemoteFilePathAsync,
            () => !string.IsNullOrWhiteSpace(DownloadRemoteFilePath));
        DownloadLogFileCommand = new RelayCommand(DownloadLogFileAsync);
        AddDiffCompareItemCommand = new RelayCommand(AddDiffCompareItemAsync);
        AddAllIpDiffCompareItemsCommand = new RelayCommand(AddAllIpDiffCompareItemsAsync);
        CompareAllDiffCompareItemsCommand = new RelayCommand(CompareAllDiffCompareItemsAsync);
        ClearDiffCompareItemsCommand = new RelayCommand(ClearDiffCompareItemsAsync);

        foreach (var item in _diffCompareSettingsStore.Load())
        {
            var viewModel = AddDiffCompareItem();
            viewModel.SelectedIp = DownloadRemoteIps.Contains(item.Ip, StringComparer.OrdinalIgnoreCase)
                ? item.Ip
                : DownloadRemoteIps.FirstOrDefault() ?? string.Empty;
            viewModel.RemoteFilePath = item.RemoteFilePath;
            viewModel.LocalFilePath = item.LocalFilePath;
            viewModel.Note = item.Note;
        }

        if (DiffCompareItems.Count == 0)
        {
            AddDiffCompareItem();
        }
    }

    private void LoadSettings(bool isReload)
    {
        try
        {
            var envPath = Path.Combine(_baseDirectory, "EnvSetting.json");
            var newConfigs = EnvSetting.Load(envPath).EnvironmentConfigs;

            // 清空舊資料
            _configs.Clear();
            _configs.AddRange(newConfigs);

            DownloadRemoteIps.Clear();
            TypeColumns.Clear();
            Jobs.Clear();
            ProjectRows.Clear();
            LogProjectNames.Clear();

            var uiState = _uiStateStore.Load();

            // 重新填入遠端 IP
            foreach (var ip in _configs
                .SelectMany(config => config.Server)
                .Select(server => server.Ip)
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(ip => ip))
            {
                DownloadRemoteIps.Add(ip);
            }

            var downloadFileSettings = _downloadFileSettingsStore.Load();
            DownloadRemoteIp = DownloadRemoteIps.Contains(downloadFileSettings.RemoteIp, StringComparer.OrdinalIgnoreCase)
                ? downloadFileSettings.RemoteIp
                : DownloadRemoteIps.FirstOrDefault() ?? string.Empty;

            // 重新填入環境類型欄位
            foreach (var type in _configs
                .Select(c => GetTypeColumnKey(c.Type))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(GetTypeOrder)
                .ThenBy(type => type)
                .Take(4))
            {
                TypeColumns.Add(type);
            }

            // 重新產生發佈 Job
            foreach (var config in _configs)
            {
                var job = new DeploymentJobViewModel(config, _deploymentService, () => SpecificFiles.ToList(), AddLog);
                if (uiState.Jobs.TryGetValue(job.StateKey, out var jobState))
                {
                    job.ApplyUiState(jobState);
                }
                Jobs.Add(job);
            }

            // 重新產生發佈卡片列
            foreach (var projectName in _configs.Select(c => c.ProjectName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ProjectRows.Add(CreateRow(projectName, Jobs));
                LogProjectNames.Add(projectName);
            }

            SelectedLogProjectName = LogProjectNames.FirstOrDefault();

            // 如果是手動點擊 Reload，就彈出提示視窗
            if (isReload)
            {
                AddLog(new LogEntry { Scope = "System", Message = $"重新載入設定完成：{Jobs.Count} 個部署項目" });
                MessageBox.Show("設定檔重新載入完成！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                AddLog(new LogEntry { Scope = "System", Message = $"載入設定完成：{Jobs.Count} 個部署項目" });
            }
        }
        catch (Exception ex)
        {
            AddLog(new LogEntry { Scope = "System", Level = "ERROR", Message = $"載入設定失敗：{ex.Message}" });
            if (isReload)
            {
                MessageBox.Show($"載入設定失敗：\n{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    public RelayCommand ReloadEnvSettingCommand { get; }
    public ObservableCollection<string> TypeColumns { get; } = [];
    public ObservableCollection<DeploymentJobViewModel> Jobs { get; } = [];
    public ObservableCollection<ProjectDeploymentRow> ProjectRows { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ObservableCollection<string> SpecificFiles { get; } = [];
    public ObservableCollection<string> LogProjectNames { get; } = [];
    public ObservableCollection<string> LogTypeNames { get; } = [];
    public ObservableCollection<LoadedLogLine> LoadedLogs { get; } = [];
    public ObservableCollection<string> DownloadRemoteIps { get; } = [];
    public ObservableCollection<string> DownloadRemoteFilePaths { get; } = [];
    public ObservableCollection<DiffCompareItemViewModel> DiffCompareItems { get; } = [];
    public RelayCommand AddSpecificFileCommand { get; }
    public RelayCommand RemoveSpecificFileCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand LoadLogsCommand { get; }
    public RelayCommand SaveReadmeCommand { get; }
    public RelayCommand DownloadSpecificFileCommand { get; }
    public RelayCommand AddDownloadRemoteFilePathCommand { get; }
    public RelayCommand RemoveDownloadRemoteFilePathCommand { get; }
    public RelayCommand DownloadLogFileCommand { get; }
    public RelayCommand AddDiffCompareItemCommand { get; }
    public RelayCommand AddAllIpDiffCompareItemsCommand { get; }
    public RelayCommand CompareAllDiffCompareItemsCommand { get; }
    public RelayCommand ClearDiffCompareItemsCommand { get; }
    public string AppTitle => $"Auto Updater WPF [{Assembly.GetExecutingAssembly().GetName().Version}]";
    public string LogFilePath { get; }
    public string ReadmePath { get; }
    public string SpecificFileIniPath => _specificFileStore.Path;
    public string UiStatePath => _uiStateStore.Path;

    public string SpecificFileInput
    {
        get => _specificFileInput;
        set => SetProperty(ref _specificFileInput, value);
    }

    public string? SelectedSpecificFile
    {
        get => _selectedSpecificFile;
        set
        {
            if (SetProperty(ref _selectedSpecificFile, value))
            {
                RemoveSpecificFileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? SelectedLogProjectName
    {
        get => _selectedLogProjectName;
        set
        {
            if (SetProperty(ref _selectedLogProjectName, value))
            {
                UpdateLogTypeNames();
                UpdateLogDownloadRemoteFolder();
            }
        }
    }

    public string? SelectedLogTypeName
    {
        get => _selectedLogTypeName;
        set
        {
            if (SetProperty(ref _selectedLogTypeName, value))
            {
                UpdateLogDownloadRemoteFolder();
            }
        }
    }

    public DateTime LogStartDate
    {
        get => _logStartDate;
        set => SetProperty(ref _logStartDate, value);
    }

    public DateTime LogEndDate
    {
        get => _logEndDate;
        set => SetProperty(ref _logEndDate, value);
    }

    public string LogFilterText
    {
        get => _logFilterText;
        set => SetProperty(ref _logFilterText, value);
    }

    public string ReadmeText
    {
        get => _readmeText;
        set => SetProperty(ref _readmeText, value);
    }

    public string DownloadRemoteIp
    {
        get => _downloadRemoteIp;
        set => SetProperty(ref _downloadRemoteIp, value);
    }

    public string DownloadRemoteFilePath
    {
        get => _downloadRemoteFilePath;
        set
        {
            if (SetProperty(ref _downloadRemoteFilePath, value))
            {
                RemoveDownloadRemoteFilePathCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string DownloadRemoteFilePathInput
    {
        get => _downloadRemoteFilePathInput;
        set => SetProperty(ref _downloadRemoteFilePathInput, value);
    }

    public string DownloadLocalFolderPath
    {
        get => _downloadLocalFolderPath;
        set => SetProperty(ref _downloadLocalFolderPath, value);
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        set => SetProperty(ref _downloadStatus, value);
    }

    public string LogDownloadRemoteFilePath
    {
        get => _logDownloadRemoteFilePath;
        set => SetProperty(ref _logDownloadRemoteFilePath, value);
    }

    public string LogDownloadLocalFolderPath
    {
        get => _logDownloadLocalFolderPath;
        set => SetProperty(ref _logDownloadLocalFolderPath, value);
    }

    public string LogDownloadStatus
    {
        get => _logDownloadStatus;
        set => SetProperty(ref _logDownloadStatus, value);
    }

    public void SaveSpecificFiles()
    {
        _specificFileStore.Save(SpecificFiles);
    }

    public void SaveDownloadFileSettings()
    {
        _downloadFileSettingsStore.Save(new DownloadFileSettings
        {
            RemoteIp = DownloadRemoteIp,
            RemoteFilePath = DownloadRemoteFilePath,
            RemoteFilePaths = DownloadRemoteFilePaths.ToList(),
            LocalFolderPath = DownloadLocalFolderPath,
        });
    }

    public void SaveDiffCompareSettings()
    {
        _diffCompareSettingsStore.Save(DiffCompareItems.Select(item => new DiffCompareSettingsItem
        {
            Ip = item.SelectedIp,
            RemoteFilePath = item.RemoteFilePath,
            LocalFilePath = item.LocalFilePath,
            Note = item.Note,
        }));
    }

    public void SaveUiState()
    {
        var state = new UiState();
        foreach (var job in Jobs)
        {
            state.Jobs[job.StateKey] = job.CaptureUiState();
        }

        _uiStateStore.Save(state);
    }

    public void AddLog(LogEntry entry)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Logs.Insert(0, entry);
            while (Logs.Count > 500)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }
        });

        var line = $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} [{entry.Level}] [{entry.Scope}] {entry.Message}";
        lock (_logFileLock)
        {
            File.AppendAllText(LogFilePath, line + Environment.NewLine);
        }
    }

    private ProjectDeploymentRow CreateRow(string projectName, IEnumerable<DeploymentJobViewModel> jobs)
    {
        var row = new ProjectDeploymentRow { ProjectName = projectName };
        foreach (var type in TypeColumns)
        {
            row.Cells.Add(new ProjectDeploymentCell
            {
                Job = jobs.FirstOrDefault(job =>
                    job.Config.ProjectName.Equals(projectName, StringComparison.OrdinalIgnoreCase) &&
                    GetTypeColumnKey(job.Config.Type).Equals(type, StringComparison.OrdinalIgnoreCase)),
            });
        }

        return row;
    }

    private Task AddSpecificFileAsync()
    {
        var fileName = SpecificFileInput.Trim();
        if (fileName.Length > 0 && !SpecificFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            SpecificFiles.Add(fileName);
            SpecificFileInput = string.Empty;
            SaveSpecificFiles();
        }

        return Task.CompletedTask;
    }

    private Task RemoveSpecificFileAsync()
    {
        if (SelectedSpecificFile is not null)
        {
            SpecificFiles.Remove(SelectedSpecificFile);
            SaveSpecificFiles();
        }

        return Task.CompletedTask;
    }

    private Task OpenLogConsoleAsync()
    {
        var command = $"powershell -NoExit -Command \"Get-Content -LiteralPath '{LogFilePath}' -Wait -Tail 80\"";
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/k " + command,
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }

    private async Task DownloadSpecificFileAsync()
    {
        try
        {
            DownloadStatus = "下載中...";
            var localPath = await _deploymentService.DownloadFileAsync(
                _configs,
                DownloadRemoteIp,
                DownloadRemoteFilePath,
                DownloadLocalFolderPath,
                CancellationToken.None);

            DownloadStatus = $"下載完成：{localPath}";
            AddLog(new LogEntry { Scope = "Download", Message = DownloadStatus });
            MessageBox.Show(DownloadStatus);
        }
        catch (Exception ex)
        {
            DownloadStatus = $"下載失敗：{ex.Message}";
            AddLog(new LogEntry { Scope = "Download", Level = "ERROR", Message = DownloadStatus });
            MessageBox.Show(DownloadStatus);
        }
    }

    private Task AddDownloadRemoteFilePathAsync()
    {
        var remoteFilePath = DownloadRemoteFilePathInput.Trim().Trim('"');
        if (remoteFilePath.Length == 0)
        {
            return Task.CompletedTask;
        }

        var existingPath = DownloadRemoteFilePaths.FirstOrDefault(path =>
            path.Equals(remoteFilePath, StringComparison.OrdinalIgnoreCase));
        if (existingPath is null)
        {
            DownloadRemoteFilePaths.Add(remoteFilePath);
            DownloadRemoteFilePath = remoteFilePath;
        }
        else
        {
            DownloadRemoteFilePath = existingPath;
        }

        DownloadRemoteFilePathInput = string.Empty;
        SaveDownloadFileSettings();
        return Task.CompletedTask;
    }

    private Task RemoveDownloadRemoteFilePathAsync()
    {
        var selectedPath = DownloadRemoteFilePath;
        var selectedIndex = DownloadRemoteFilePaths.IndexOf(selectedPath);
        if (selectedIndex < 0)
        {
            return Task.CompletedTask;
        }

        DownloadRemoteFilePaths.RemoveAt(selectedIndex);
        DownloadRemoteFilePath = DownloadRemoteFilePaths.Count == 0
            ? string.Empty
            : DownloadRemoteFilePaths[Math.Min(selectedIndex, DownloadRemoteFilePaths.Count - 1)];
        SaveDownloadFileSettings();
        return Task.CompletedTask;
    }

    private Task AddDiffCompareItemAsync()
    {
        AddDiffCompareItem();
        return Task.CompletedTask;
    }

    private Task AddAllIpDiffCompareItemsAsync()
    {
        var remoteFilePath = DiffCompareItems.LastOrDefault()?.RemoteFilePath ?? string.Empty;
        var localFilePath = DiffCompareItems.LastOrDefault()?.LocalFilePath ?? string.Empty;
        var note = DiffCompareItems.LastOrDefault()?.Note ?? string.Empty;
        foreach (var ip in DownloadRemoteIps)
        {
            var item = AddDiffCompareItem();
            item.SelectedIp = ip;
            item.RemoteFilePath = remoteFilePath;
            item.LocalFilePath = localFilePath;
            item.Note = note;
        }

        return Task.CompletedTask;
    }

    private async Task CompareAllDiffCompareItemsAsync()
    {
        var items = DiffCompareItems.ToList();
        using var semaphore = new SemaphoreSlim(4);
        await Task.WhenAll(items.Select(async item =>
        {
            await semaphore.WaitAsync();
            try
            {
                await item.CompareNowAsync();
            }
            finally
            {
                semaphore.Release();
            }
        }));
    }

    private Task ClearDiffCompareItemsAsync()
    {
        DiffCompareItems.Clear();
        return Task.CompletedTask;
    }

    private DiffCompareItemViewModel AddDiffCompareItem()
    {
        var item = new DiffCompareItemViewModel(_deploymentService, _configs, DownloadRemoteIps, RemoveDiffCompareItem);
        DiffCompareItems.Add(item);
        return item;
    }

    private void RemoveDiffCompareItem(DiffCompareItemViewModel item)
    {
        DiffCompareItems.Remove(item);
    }

    private async Task DownloadLogFileAsync()
    {
        try
        {
            LogDownloadStatus = "下載LOG中...";
            var logConfigs = GetSelectedLogConfigs().ToList();
            if (logConfigs.Count == 0)
            {
                throw new InvalidOperationException("請先選擇專案");
            }

            var downloaded = 0;
            var failed = 0;
            var downloadTasks = logConfigs
                .SelectMany(config => config.Server.Select(server => (Config: config, Server: server)))
                .Select(async target =>
                {
                    try
                    {
                        var count = await _deploymentService.DownloadDirectoryFilesAsync(
                            target.Server,
                            BuildLogFolderPath(target.Config.RemotePath),
                            GetSelectedLogLocalFolderPath(target.Config.Type),
                            $"{target.Config.ProjectName}_{target.Config.Type}_{target.Server.Ip}",
                            LogStartDate,
                            LogEndDate,
                            CancellationToken.None);
                        Interlocked.Add(ref downloaded, count);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        AddLog(new LogEntry
                        {
                            Scope = "LogDownload",
                            Level = "ERROR",
                            Message = $"{target.Config.Type}/{target.Server.Ip} 下載失敗：{ex.Message}",
                        });
                    }
                });

            await Task.WhenAll(downloadTasks);

            LogDownloadStatus = $"下載完成：{downloaded} 個檔案，失敗 {failed} 台，位置：{GetSelectedLogLocalFolderPath(SelectedLogTypeName)}";
            AddLog(new LogEntry { Scope = "LogDownload", Message = LogDownloadStatus });
            MessageBox.Show(LogDownloadStatus);
        }
        catch (Exception ex)
        {
            LogDownloadStatus = $"下載失敗：{ex.Message}";
            AddLog(new LogEntry { Scope = "LogDownload", Level = "ERROR", Message = LogDownloadStatus });
            MessageBox.Show(LogDownloadStatus);
        }
    }

    private Task SaveReadmeAsync()
    {
        File.WriteAllText(ReadmePath, ReadmeText);
        AddLog(new LogEntry { Scope = "ReadMe", Message = "README.txt 已儲存" });
        return Task.CompletedTask;
    }

    private Task LoadLogsAsync()
    {
        LoadedLogs.Clear();
        if (string.IsNullOrWhiteSpace(SelectedLogProjectName) ||
            string.IsNullOrWhiteSpace(SelectedLogTypeName))
        {
            return Task.CompletedTask;
        }

        var root = GetSelectedLogLocalFolderPath(SelectedLogTypeName);
        var logFiles = Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly)
            : [];
        var selectedConfigs = GetSelectedLogConfigs().ToList();
        var filterKeywords = LogFilterText
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
            .Select(keyword => keyword.Trim())
            .Where(keyword => keyword.Length > 0)
            .ToList();

        foreach (var file in logFiles)
        {
            if (!TryParseDownloadedLogFileName(file, selectedConfigs, out var originalFileName))
            {
                continue;
            }

            var date = File.GetCreationTime(file).Date;
            if (date < LogStartDate.Date || date > LogEndDate.Date)
            {
                continue;
            }

            foreach (var line in File.ReadLines(file))
            {
                if (filterKeywords.Count > 0 &&
                    filterKeywords.Any(keyword => !line.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                LoadedLogs.Add(new LoadedLogLine
                {
                    FileDate = date,
                    ProjectName = SelectedLogProjectName,
                    Type = SelectedLogTypeName,
                    FileName = originalFileName,
                    Content = line,
                });
            }
        }

        AddLog(new LogEntry { Scope = "Log", Message = $"載入 {SelectedLogProjectName} Log：{LoadedLogs.Count} 筆" });
        return Task.CompletedTask;
    }

    private static bool TryParseDownloadedLogFileName(
        string localFilePath,
        IEnumerable<EnvironmentConfig> configs,
        out string originalFileName)
    {
        var localFileName = Path.GetFileName(localFilePath);
        foreach (var config in configs)
        {
            foreach (var server in config.Server)
            {
                var prefix = $"{config.ProjectName}_{config.Type}_{server.Ip}_";
                if (!localFileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                originalFileName = localFileName[prefix.Length..];
                return !string.IsNullOrWhiteSpace(originalFileName);
            }
        }

        originalFileName = string.Empty;
        return false;
    }

    private IEnumerable<EnvironmentConfig> GetSelectedLogConfigs()
    {
        if (string.IsNullOrWhiteSpace(SelectedLogProjectName))
        {
            return [];
        }

        return _configs
            .Where(config => config.ProjectName.Equals(SelectedLogProjectName, StringComparison.OrdinalIgnoreCase))
            .Where(config => string.IsNullOrWhiteSpace(SelectedLogTypeName) ||
                config.Type.Equals(SelectedLogTypeName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(config => GetTypeOrder(config.Type))
            .ThenBy(config => config.Type);
    }

    private void UpdateLogTypeNames()
    {
        LogTypeNames.Clear();
        if (string.IsNullOrWhiteSpace(SelectedLogProjectName))
        {
            SelectedLogTypeName = null;
            return;
        }

        foreach (var type in _configs
            .Where(config => config.ProjectName.Equals(SelectedLogProjectName, StringComparison.OrdinalIgnoreCase))
            .Select(config => config.Type)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetTypeOrder)
            .ThenBy(type => type))
        {
            LogTypeNames.Add(type);
        }

        SelectedLogTypeName = LogTypeNames.FirstOrDefault();
    }

    private void UpdateLogDownloadRemoteFolder()
    {
        var config = GetSelectedLogConfigs().FirstOrDefault();
        LogDownloadRemoteFilePath = config is null ? string.Empty : BuildLogFolderPath(config.RemotePath);
    }

    private string GetSelectedLogLocalFolderPath(string? typeName)
    {
        var root = string.IsNullOrWhiteSpace(LogDownloadLocalFolderPath)
            ? @"C:\temp"
            : LogDownloadLocalFolderPath.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(SelectedLogProjectName))
        {
            return root;
        }

        var projectFolder = Path.Combine(root, $"{SelectedLogProjectName}_Log");
        return string.IsNullOrWhiteSpace(typeName)
            ? projectFolder
            : Path.Combine(projectFolder, typeName);
    }

    private static string BuildLogFolderPath(string remotePath)
    {
        return remotePath.Trim().Trim('"').TrimEnd('/', '\\') + "/logs";
    }

    private static int GetTypeOrder(string type)
    {
        return GetTypeColumnKey(type).ToLowerInvariant() switch
        {
            "test" => 0,
            "stage" => 1,
            "prod" => 2,
            "preprod" => 3,
            _ => 9,
        };
    }

    private static string GetTypeColumnKey(string type)
    {
        if (type.Contains("test", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("測試", StringComparison.OrdinalIgnoreCase))
        {
            return "Test";
        }

        if (type.Contains("stage", StringComparison.OrdinalIgnoreCase))
        {
            return "Stage";
        }

        if (type.Contains("preprod", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("前測", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("pioneer", StringComparison.OrdinalIgnoreCase))
        {
            return "PreProd";
        }

        if (type.Contains("prod", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("正式", StringComparison.OrdinalIgnoreCase))
        {
            return "Prod";
        }

        return type;
    }
}
