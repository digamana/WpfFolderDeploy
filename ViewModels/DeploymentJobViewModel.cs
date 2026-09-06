using auto_updater_wpf.Models;
using auto_updater_wpf.Services;
using Renci.SshNet;
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace auto_updater_wpf.ViewModels;

public sealed class ServerTargetViewModel : ObservableObject
{
    private readonly DeploymentService _service;
    private readonly Action<LogEntry> _log;
    private bool _isSelected = true;
    private string _status = string.Empty;
    private bool _isDiskSpaceLow;
    private bool _isTesting; // 新增：用於防呆的狀態

    public ServerTargetViewModel(Server server, DeploymentService service, Action<LogEntry> log)
    {
        Server = server;
        _service = service;
        _log = log;
        // 加入防呆：當 _isTesting 為 true 時，按鈕不可點擊
        TestConnectionCommand = new RelayCommand(() => TestConnectionAsync(CancellationToken.None), () => !_isTesting);
    }

    public Server Server { get; }
    public string Ip => Server.Ip;
    public RelayCommand TestConnectionCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        set
        {
            if (SetProperty(ref _isTesting, value))
            {
                TestConnectionCommand.RaiseCanExecuteChanged(); // 更新按鈕的可點擊狀態
            }
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public bool IsDiskSpaceLow
    {
        get => _isDiskSpaceLow;
        set
        {
            if (SetProperty(ref _isDiskSpaceLow, value))
            {
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public Brush StatusBrush
    {
        get
        {
            if (IsDiskSpaceLow || Status.Contains("失敗", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.Firebrick;
            }

            if (Status.Contains("OK", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.ForestGreen;
            }

            return Brushes.DimGray;
        }
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        if (IsTesting) return; // 雙重防呆

        try
        {
            IsTesting = true;
            Status = "測試中";
            IsDiskSpaceLow = false;

            var diskInfo = await _service.TestConnectionAsync(Server, cancellationToken);

            if (diskInfo.FreeSpaceGb.HasValue)
            {
                IsDiskSpaceLow = diskInfo.FreeSpaceGb.Value <= 10;
                Status = $"連線OK, C槽剩 {diskInfo.FreeSpaceGb.Value:N1}G";
            }
            else
            {
                IsDiskSpaceLow = false;
                Status = $"連線OK, 但硬碟容量讀取異常{diskInfo.ErrorMessage}";
                _log(new LogEntry { Scope = $"Test / {Server.Ip}", Level = "WARN", Message = $"連線成功，但硬碟容量讀取失敗：{diskInfo.ErrorMessage}" });
            }

            _log(new LogEntry { Scope = $"Test / {Server.Ip}", Message = Status });
        }
        catch (OperationCanceledException)
        {
            Status = "已取消";
            throw;
        }
        catch (Exception ex)
        {
            IsDiskSpaceLow = true;
            Status = $"失敗：{ex.Message}";
            _log(new LogEntry { Scope = $"Test / {Server.Ip}", Level = "ERROR", Message = $"測試連線失敗：{ex.Message}" });
        }
        finally
        {
            IsTesting = false; // 測試結束，釋放按鈕
        }
    }
}

public sealed class DeploymentJobViewModel : ObservableObject
{
    private readonly DeploymentService _service;
    private readonly Func<IReadOnlyCollection<string>> _specificFilesProvider;
    private readonly Action<LogEntry> _log;
    private CancellationTokenSource? _cancellationTokenSource;
    private int _progressValue;
    private int _progressMaximum = 1;
    private string _status = "待命";
    private bool _isBusy;
    private bool _onlyLastWriteTimeDiff;
    private bool _onlySpecificFiles = true;
    private bool _includeAppSettings;
    private bool _isSetupRequired;
    private bool _hasPublishReport;

    public DeploymentJobViewModel(
            EnvironmentConfig config,
            DeploymentService service,
            Func<IReadOnlyCollection<string>> specificFilesProvider,
            Action<LogEntry> log)
    {
        Config = config;
        _service = service;
        _specificFilesProvider = specificFilesProvider;
        _log = log;
        PublishCommand = new RelayCommand(PublishAsync, () => !IsBusy);
        BackupCommand = new RelayCommand(BackupAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(Config.BackupRemotePath));
        ViewPublishReportCommand = new RelayCommand(ViewPublishReportAsync, () => HasPublishReport);
        OpenLocalFolderCommand = new RelayCommand(OpenLocalFolderAsync);
        SetupDeployEnvironmentCommand = new RelayCommand(SetupDeployEnvironmentAsync, () => !IsBusy);

        foreach (var server in Config.Server.OrderBy(server => GetIpSortKey(server.Ip), StringComparer.OrdinalIgnoreCase))
        {
            ServerTargets.Add(new ServerTargetViewModel(server, service, log));
        }

        RefreshSetupRequired();
    }

    public EnvironmentConfig Config { get; }
    public RelayCommand PublishCommand { get; }
    public RelayCommand BackupCommand { get; }
    public RelayCommand ViewPublishReportCommand { get; }
    public RelayCommand OpenLocalFolderCommand { get; }
    public RelayCommand SetupDeployEnvironmentCommand { get; }
    public ObservableCollection<string> ServerStatuses { get; } = [];
    public ObservableCollection<ServerTargetViewModel> ServerTargets { get; } = [];
    public ObservableCollection<PublishFileRecord> LastPublishRecords { get; } = [];

    public string DisplayName => Config.DisplayName;
    public string ProjectName => Config.ProjectName;
    public string EnvironmentType => Config.Type;
    public string Memo => Config.Memo;
    public string LocalPath => Config.LocalPath;
    public string RemotePath => Config.RemotePath;
    public string BackupRemotePath => Config.BackupRemotePath;
    public string LocalFolderPath => LocalPath.Replace('/', '\\').TrimEnd('\\');
    public string PublishScriptPath => Path.Combine(@"C:\SVN_Release", $"{Path.GetFileName(LocalFolderPath)}.ps1");
    public string StateKey => $"{Config.Type}|{Config.ProjectName}";

    public bool IsSetupRequired
    {
        get => _isSetupRequired;
        set => SetProperty(ref _isSetupRequired, value);
    }

    public bool HasPublishReport
    {
        get => _hasPublishReport;
        set
        {
            if (SetProperty(ref _hasPublishReport, value))
            {
                ViewPublishReportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool OnlyLastWriteTimeDiff
    {
        get => _onlyLastWriteTimeDiff;
        set => SetProperty(ref _onlyLastWriteTimeDiff, value);
    }

    public bool IncludeAppSettings
    {
        get => _includeAppSettings;
        set => SetProperty(ref _includeAppSettings, value);
    }

    public bool OnlySpecificFiles
    {
        get => _onlySpecificFiles;
        set => SetProperty(ref _onlySpecificFiles, value);
    }

    public int ProgressValue
    {
        get => _progressValue;
        set
        {
            if (SetProperty(ref _progressValue, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public int ProgressMaximum
    {
        get => _progressMaximum;
        set
        {
            if (SetProperty(ref _progressMaximum, Math.Max(1, value)))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public string ProgressText => $"{ProgressValue}/{ProgressMaximum}";

    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(StatusLogText));
            }
        }
    }

    public string StatusLogText => string.Join(Environment.NewLine, new[] { Status }.Concat(ServerStatuses));

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                PublishCommand.RaiseCanExecuteChanged();
                BackupCommand.RaiseCanExecuteChanged();
                SetupDeployEnvironmentCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public Brush StatusBrush
    {
        get
        {
            if (IsBusy)
            {
                return Brushes.DarkOrange;
            }

            if (Status.Contains("失敗", StringComparison.OrdinalIgnoreCase) ||
                Status.Contains("錯誤", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.Firebrick;
            }

            if (Status.Contains("完成", StringComparison.OrdinalIgnoreCase) ||
                Status.Contains("OK", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.ForestGreen;
            }

            return Brushes.DimGray;
        }
    }

    private async Task PublishAsync()
    {
        var selectedTargets = ServerTargets.Where(server => server.IsSelected).ToList();
        if (selectedTargets.Count == 0)
        {
            MessageBox.Show("請至少勾選一台要發布的 Server IP。");
            return;
        }
        var selectedServers = selectedTargets.Select(target => target.Server).ToList();

        if (Config.Type.Equals("Prod", StringComparison.OrdinalIgnoreCase))
        {
            var result = MessageBox.Show(
                $"確定要發布正式區 {Config.ProjectName} 嗎？",
                "Prod 發布確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                Status = "已取消 Prod 發布";
                return;
            }
        }

        await RunJobAsync(async token =>
        {
            Status = "測試 IP 連線";
            await Task.WhenAll(selectedTargets.Select(target => target.TestConnectionAsync(token)));

            // 整理需要發布的檔案
            #region 整理需要發布的檔案
            Status = "檢查更新檔案";
            LastPublishRecords.Clear();
            HasPublishReport = false;
            var statusProgress = new Progress<string>(SetServerStatus);

            // 將型別改為 Dictionary 以接收各 IP 專屬的檔案清單
            IReadOnlyDictionary<string, IReadOnlyList<DeploymentFile>> updateFilesMap;

            if (OnlySpecificFiles)
            {
                updateFilesMap = await _service.FindQuickUpdateFilesAsync(
                    Config,
                    selectedServers,
                    IncludeAppSettings,
                    _specificFilesProvider(),
                    statusProgress,
                    token);
            }
            else
            {
                updateFilesMap = await _service.FindUpdateFilesAsync(
                    Config,
                    selectedServers,
                    OnlyLastWriteTimeDiff,
                    IncludeAppSettings,
                    statusProgress,
                    token);
            }
            #endregion

            // 進行發布
            #region 進行發布
            ProgressValue = 0;

            // 計算總共需要上傳的實際檔案數量 (所有伺服器清單的加總)
            var totalFilesToDeploy = updateFilesMap.Values.Sum(files => files.Count);
            ProgressMaximum = Math.Max(1, totalFilesToDeploy);

            Status = totalFilesToDeploy == 0 ? "沒有檔案需要發布" : $"發布中：共 {totalFilesToDeploy} 個專屬檔案更新任務";

            var progress = new Progress<int>(value => ProgressValue += value);
            var publishFileProgress = new Progress<PublishFileRecord>(record =>
            {
                LastPublishRecords.Add(record);
                HasPublishReport = LastPublishRecords.Count > 0;
                SetServerStatus($"{record.Ip} 上傳 {record.RelativePath}");
            });

            // 將 Dictionary 傳遞給新版的 PublishAsync
            await _service.PublishAsync(Config, selectedServers, updateFilesMap, progress, statusProgress, publishFileProgress, token);
            #endregion

            Status = "發布完成";
            MessageBox.Show($"{Config.DisplayName} 發布完成。");
        });
    }

    private async Task BackupAsync()
    {
        var selectedServers = GetSelectedServers();
        if (selectedServers.Count == 0)
        {
            MessageBox.Show("請至少勾選一台要備份的 Server IP。");
            return;
        }

        await RunJobAsync(async token =>
        {
            ProgressValue = 0;
            ProgressMaximum = Math.Max(1, selectedServers.Count);
            Status = "備份中";
            var progress = new Progress<int>(value => ProgressValue += value);
            var statusProgress = new Progress<string>(SetServerStatus);
            await _service.BackupAsync(Config, selectedServers, progress, statusProgress, token);
            Status = "備份完成";
        });
    }

    private Task OpenLocalFolderAsync()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{LocalFolderPath}\"",
            UseShellExecute = true,
        });

        return Task.CompletedTask;
    }

    private Task ViewPublishReportAsync()
    {
        var table = new DataTable();
        var groupedRecords = LastPublishRecords
            .GroupBy(record => record.Ip)
            .OrderBy(group => group.Key)
            .ToList();

        foreach (var group in groupedRecords)
        {
            table.Columns.Add(group.Key);
        }

        var filesByIp = groupedRecords
            .Select(group => group
                .OrderBy(record => record.RelativePath)
                .Select(record => record.RelativePath.TrimStart('/'))
                .ToList())
            .ToList();

        var rowCount = filesByIp.Count == 0 ? 0 : filesByIp.Max(files => files.Count);
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = table.NewRow();
            for (var columnIndex = 0; columnIndex < filesByIp.Count; columnIndex++)
            {
                row[columnIndex] = rowIndex < filesByIp[columnIndex].Count
                    ? filesByIp[columnIndex][rowIndex]
                    : string.Empty;
            }

            table.Rows.Add(row);
        }

        var grid = new DataGrid
        {
            ItemsSource = table.DefaultView,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserSortColumns = false,
            IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            Margin = new Thickness(12),
        };

        foreach (DataColumn column in table.Columns)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = column.ColumnName,
                Binding = new Binding($"[{column.ColumnName}]"),
                CanUserSort = false,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            });
        }

        var window = new Window
        {
            Title = Config.ProjectName,
            Width = 900,
            Height = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = grid,
        };

        if (Application.Current.MainWindow is not null)
        {
            window.Owner = Application.Current.MainWindow;
        }

        window.Show();
        return Task.CompletedTask;
    }

    private Task SetupDeployEnvironmentAsync()
    {
        if (!Directory.Exists(LocalFolderPath))
        {
            Directory.CreateDirectory(LocalFolderPath);
        }

        if (!File.Exists(PublishScriptPath))
        {
            File.WriteAllText(PublishScriptPath, BuildPublishScript());
        }

        RefreshSetupRequired();
        Status = "部版環境已建立";
        return Task.CompletedTask;
    }

    private string BuildPublishScript()
    {
        var projectName = Config.ProjectName;
        var projectPath = $@"C:\SVN_Release\trunk\{projectName}\{projectName}\{projectName}.csproj";
        var configuration = Config.Type.Equals("Test", StringComparison.OrdinalIgnoreCase)
            ? "Dev"
            : Config.Type.Equals("PreProd", StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : Config.Type;

        return
$@"# 定義專案路徑和發布路徑
$projectPath = ""{projectPath}""
$publishPath = ""{LocalFolderPath}""

# 確保發布路徑存在，如果不存在則建立
if (-Not (Test-Path -Path $publishPath)) {{
    New-Item -ItemType Directory -Path $publishPath | Out-Null
}}

# 發布專案到指定路徑
dotnet publish $projectPath --configuration {configuration} --output $publishPath

# 輸出發布結果
if ($?) {{
    Write-Output ""Project published successfully to $publishPath""
}} else {{
    Write-Output ""Failed to publish the project""
    pause
}}
";
    }

    private async Task RunJobAsync(Func<CancellationToken, Task> action)
    {
        _cancellationTokenSource = new CancellationTokenSource();
        IsBusy = true;
        ServerStatuses.Clear();

        try
        {
            await action(_cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "已取消操作";
            _log(new LogEntry { Scope = StateKey, Level = "WARN", Message = "操作已手動取消" });
        }
        catch (Exception ex)
        {
            Status = $"失敗：{ex.Message}";
            // 修復：確保作業中斷的整體錯誤也會寫入系統 LOG
            _log(new LogEntry { Scope = StateKey, Level = "ERROR", Message = $"作業異常中斷：{ex.Message}" });
        }
        finally
        {
            IsBusy = false;
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private IReadOnlyList<Server> GetSelectedServers()
    {
        return ServerTargets
            .Where(server => server.IsSelected)
            .Select(server => server.Server)
            .ToList();
    }

    private static string GetIpSortKey(string ip)
    {
        return System.Net.IPAddress.TryParse(ip, out var address)
            ? string.Join('.', address.GetAddressBytes().Select(value => value.ToString("D3")))
            : ip;
    }

    public DeploymentJobUiState CaptureUiState()
    {
        return new DeploymentJobUiState
        {
            OnlyLastWriteTimeDiff = OnlyLastWriteTimeDiff,
            OnlySpecificFiles = OnlySpecificFiles,
            IncludeAppSettings = IncludeAppSettings,
            Servers = ServerTargets.ToDictionary(
                server => server.Ip,
                server => server.IsSelected,
                StringComparer.OrdinalIgnoreCase),
        };
    }

    public void ApplyUiState(DeploymentJobUiState? state)
    {
        if (state is null)
        {
            return;
        }

        OnlyLastWriteTimeDiff = state.OnlyLastWriteTimeDiff;
        OnlySpecificFiles = state.OnlySpecificFiles;
        IncludeAppSettings = state.IncludeAppSettings;

        foreach (var server in ServerTargets)
        {
            if (state.Servers.TryGetValue(server.Ip, out var isSelected))
            {
                server.IsSelected = isSelected;
            }
        }
    }

    private void RefreshSetupRequired()
    {
        IsSetupRequired = !Directory.Exists(LocalFolderPath) || !File.Exists(PublishScriptPath);
    }

    private void SetServerStatus(string message)
    {
        ServerStatuses.Insert(0, $"{DateTime.Now:HH:mm:ss} {message}");
        OnPropertyChanged(nameof(StatusLogText));
    }
}
