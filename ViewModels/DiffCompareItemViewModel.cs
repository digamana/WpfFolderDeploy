using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using auto_updater_wpf.Models;
using auto_updater_wpf.Services;

namespace auto_updater_wpf.ViewModels;

public sealed class DiffCompareDetailRow
{
    public string RelativePath { get; init; } = string.Empty;
    public string RemoteDisplay { get; init; } = string.Empty;
    public string LocalDisplay { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool IsDifferent { get; init; }
    public Brush Foreground => IsDifferent ? Brushes.Firebrick : Brushes.DimGray;
}

public sealed class DiffCompareItemViewModel : ObservableObject
{
    private readonly DeploymentService _service;
    private readonly IReadOnlyList<EnvironmentConfig> _configs;
    private readonly Action<DiffCompareItemViewModel> _remove;
    private string _selectedIp = string.Empty;
    private string _remoteFilePath = string.Empty;
    private string _localFilePath = string.Empty;
    private string _result = string.Empty;
    private string _note = string.Empty;
    private string _remoteDetailHeader = "遠端";
    private string _localDetailHeader = "本機";
    private bool _hasDetails;

    public DiffCompareItemViewModel(
        DeploymentService service,
        IReadOnlyList<EnvironmentConfig> configs,
        ObservableCollection<string> ipOptions,
        Action<DiffCompareItemViewModel> remove)
    {
        _service = service;
        _configs = configs;
        _remove = remove;
        IpOptions = ipOptions;
        SelectedIp = IpOptions.FirstOrDefault() ?? string.Empty;
        CompareCommand = new RelayCommand(CompareNowAsync);
        RemoveCommand = new RelayCommand(RemoveAsync);
        ViewDetailsCommand = new RelayCommand(ViewDetailsAsync, () => HasDetails);
    }

    public ObservableCollection<string> IpOptions { get; }
    public ObservableCollection<DiffCompareDetailRow> DetailRows { get; } = [];
    public RelayCommand CompareCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ViewDetailsCommand { get; }

    public string SelectedIp
    {
        get => _selectedIp;
        set => SetProperty(ref _selectedIp, value);
    }

    public string RemoteFilePath
    {
        get => _remoteFilePath;
        set => SetProperty(ref _remoteFilePath, value);
    }

    public string LocalFilePath
    {
        get => _localFilePath;
        set => SetProperty(ref _localFilePath, value);
    }

    public string Result
    {
        get => _result;
        set => SetProperty(ref _result, value);
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public bool HasDetails
    {
        get => _hasDetails;
        set
        {
            if (SetProperty(ref _hasDetails, value))
            {
                ViewDetailsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task CompareNowAsync()
    {
        try
        {
            Result = "比對中...";
            DetailRows.Clear();
            HasDetails = false;

            if (string.IsNullOrWhiteSpace(SelectedIp))
            {
                throw new InvalidOperationException("請選擇 IP");
            }

            var remotePath = RemoteFilePath.Trim().Trim('"');
            var localPath = LocalFilePath.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                throw new InvalidOperationException("請輸入遠端檔案或資料夾位置");
            }

            if (string.IsNullOrWhiteSpace(localPath))
            {
                throw new InvalidOperationException("請輸入本機檔案或資料夾位置");
            }

            var remoteHasExtension = HasFileExtension(remotePath);
            var localHasExtension = HasFileExtension(localPath);
            if (remoteHasExtension != localHasExtension)
            {
                throw new InvalidOperationException("遠端與本機路徑必須同時是檔案，或同時是資料夾。");
            }

            if (remoteHasExtension)
            {
                if (IsLogFile(remotePath) || IsLogFile(localPath))
                {
                    throw new InvalidOperationException("差異比對已排除 .log 檔案");
                }

                await CompareSingleFileAsync(remotePath, localPath);
            }
            else
            {
                await CompareDirectoryAsync(remotePath, localPath);
            }
        }
        catch (Exception ex)
        {
            Result = $"比對失敗：{ex.Message}";
        }
    }

    private async Task CompareSingleFileAsync(string remotePath, string localPath)
    {
        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException($"本機檔案不存在：{LocalFilePath}");
        }

        var tempFolder = Path.Combine(Path.GetTempPath(), "auto_updater_wpf_diff");
        var remoteFileName = Path.GetFileName(remotePath.Replace('/', '\\'));
        var tempFileName = $"{Guid.NewGuid():N}_{remoteFileName}";
        var downloadedRemotePath = await _service.DownloadFileAsync(
            _configs,
            SelectedIp,
            remotePath,
            tempFolder,
            CancellationToken.None,
            tempFileName);

        Result = CompareFiles(downloadedRemotePath, localPath);
    }

    private async Task CompareDirectoryAsync(string remotePath, string localPath)
    {
        if (!Directory.Exists(localPath))
        {
            throw new DirectoryNotFoundException($"本機資料夾不存在：{LocalFilePath}");
        }

        _remoteDetailHeader = remotePath;
        _localDetailHeader = localPath;

        var remoteFiles = await _service.ListRemoteDirectoryFilesAsync(
            _configs,
            SelectedIp,
            remotePath,
            CancellationToken.None);

        var remoteMap = remoteFiles.ToDictionary(
            file => NormalizeRelativePath(file.RelativePath),
            file => file,
            StringComparer.OrdinalIgnoreCase);

        var localMap = Directory.GetFiles(localPath, "*", SearchOption.AllDirectories)
            .Where(file => !IsLogFile(file))
            .ToDictionary(
                file => NormalizeRelativePath(Path.GetRelativePath(localPath, file)),
                file => file,
                StringComparer.OrdinalIgnoreCase);

        var allRelativePaths = remoteMap.Keys
            .Concat(localMap.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var differentCount = 0;
        foreach (var relativePath in allRelativePaths)
        {
            var hasRemote = remoteMap.TryGetValue(relativePath, out var remoteFile);
            var hasLocal = localMap.TryGetValue(relativePath, out var localFile);

            if (!hasRemote)
            {
                differentCount++;
                DetailRows.Add(new DiffCompareDetailRow
                {
                    RelativePath = relativePath,
                    RemoteDisplay = string.Empty,
                    LocalDisplay = localFile!,
                    Status = "遠端缺少",
                    IsDifferent = true,
                });
                continue;
            }

            if (!hasLocal)
            {
                differentCount++;
                DetailRows.Add(new DiffCompareDetailRow
                {
                    RelativePath = relativePath,
                    RemoteDisplay = remoteFile!.FullPath,
                    LocalDisplay = string.Empty,
                    Status = "本機缺少",
                    IsDifferent = true,
                });
                continue;
            }

            var sameContent = string.Equals(
                remoteFile!.Sha256,
                ComputeSha256(localFile!),
                StringComparison.OrdinalIgnoreCase);
            var sameLastWriteTime = remoteFile.LastWriteTime == File.GetLastWriteTime(localFile!);
            var isDifferent = !sameContent || !sameLastWriteTime;
            if (isDifferent)
            {
                differentCount++;
            }

            DetailRows.Add(new DiffCompareDetailRow
            {
                RelativePath = relativePath,
                RemoteDisplay = $"{remoteFile.FullPath} ({remoteFile.LastWriteTime:yyyy-MM-dd HH:mm:ss})",
                LocalDisplay = $"{localFile} ({File.GetLastWriteTime(localFile!):yyyy-MM-dd HH:mm:ss})",
                Status = sameContent
                    ? sameLastWriteTime ? "一致" : "內容一致，修改時間不同"
                    : "內容不同",
                IsDifferent = isDifferent,
            });
        }

        HasDetails = DetailRows.Count > 0;
        Result = differentCount == 0
            ? $"一致：資料夾 {DetailRows.Count} 個檔案皆相同"
            : $"不一致：資料夾 {DetailRows.Count} 個檔案，差異 {differentCount} 個";
    }

    private Task RemoveAsync()
    {
        _remove(this);
        return Task.CompletedTask;
    }

    private Task ViewDetailsAsync()
    {
        var grid = new DataGrid
        {
            ItemsSource = DetailRows,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            IsReadOnly = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            Margin = new Thickness(12),
        };

        grid.Columns.Add(CreateTextColumn("相對路徑", "RelativePath", 260));
        grid.Columns.Add(CreateTextColumn(_remoteDetailHeader, "RemoteDisplay", 360));
        grid.Columns.Add(CreateTextColumn(_localDetailHeader, "LocalDisplay", 360));
        grid.Columns.Add(CreateTextColumn("狀態", "Status", 180));

        var window = new Window
        {
            Title = string.IsNullOrWhiteSpace(Note) ? "差異比對明細" : $"差異比對明細 - {Note}",
            Width = 1180,
            Height = 620,
            MinWidth = 760,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = grid,
        };
        window.ShowDialog();
        return Task.CompletedTask;
    }

    private static DataGridTextColumn CreateTextColumn(string header, string path, double width)
    {
        return new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path),
            Width = width,
            ElementStyle = new Style(typeof(TextBlock))
            {
                Setters =
                {
                    new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap),
                    new Setter(TextBlock.ForegroundProperty, new Binding("Foreground")),
                },
            },
        };
    }

    private static string CompareFiles(string remoteLocalPath, string localFilePath)
    {
        var extension = Path.GetExtension(localFilePath);
        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            var remoteText = File.ReadAllText(remoteLocalPath);
            var localText = File.ReadAllText(localFilePath);
            return string.Equals(remoteText, localText, StringComparison.Ordinal)
                ? "一致：txt 內容相同"
                : "不一致：txt 內容不同";
        }

        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var remoteVersion = FileVersionInfo.GetVersionInfo(remoteLocalPath).FileVersion;
            var localVersion = FileVersionInfo.GetVersionInfo(localFilePath).FileVersion;
            if (!string.IsNullOrWhiteSpace(remoteVersion) || !string.IsNullOrWhiteSpace(localVersion))
            {
                return string.Equals(remoteVersion, localVersion, StringComparison.OrdinalIgnoreCase)
                    ? $"一致：版本 {localVersion}"
                    : $"不一致：遠端版本 {remoteVersion}，本機版本 {localVersion}";
            }
        }

        var remoteHash = ComputeSha256(remoteLocalPath);
        var localHash = ComputeSha256(localFilePath);
        return string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase)
            ? "一致：檔案雜湊相同"
            : "不一致：檔案雜湊不同";
    }

    private static bool HasFileExtension(string path)
    {
        return !string.IsNullOrWhiteSpace(Path.GetExtension(path.Trim().Trim('"').Replace('/', '\\')));
    }

    private static bool IsLogFile(string path)
    {
        return Path.GetExtension(path.Trim().Trim('"').Replace('/', '\\'))
            .Equals(".log", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static string ComputeSha256(string path)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha256.ComputeHash(stream));
    }
}
