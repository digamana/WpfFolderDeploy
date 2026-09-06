using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using auto_updater_wpf.ViewModels;

namespace auto_updater_wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        SourceInitialized += (_, _) => PlaceInsideWorkingArea();
    }

    private void PlaceInsideWorkingArea()
    {
        const double margin = 20;
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(1, workArea.Width - (margin * 2));
        var availableHeight = Math.Max(1, workArea.Height - (margin * 2));

        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);

        Left = workArea.Left + Math.Max(margin, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(margin, (workArea.Height - Height) / 2);
    }

    private void LoadedLogsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var cell = FindParent<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell?.Column.Header?.ToString() != "內容" || cell.DataContext is not LoadedLogLine logLine)
        {
            return;
        }

        var contentTextBox = new TextBox
        {
            Text = logLine.Content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(12),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
        };

        var detailWindow = new Window
        {
            Title = $"LOG 內容 - {logLine.FileName}",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 760,
            Height = 460,
            MinWidth = 480,
            MinHeight = 280,
            Icon = Icon,
            Content = contentTextBox,
        };

        detailWindow.ShowDialog();
        e.Handled = true;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T parent)
            {
                return parent;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SaveSpecificFiles();
            viewModel.SaveDownloadFileSettings();
            viewModel.SaveDiffCompareSettings();
            viewModel.SaveUiState();
        }

        base.OnClosing(e);
    }
}
