using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace auto_updater_wpf.Views;

public partial class EnvSettingEditorControl : UserControl
{
    private readonly string _envSettingPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EnvSetting.json");

    public EnvSettingEditorControl()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadEnvSetting();
    }

    private void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        LoadEnvSetting();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            File.WriteAllText(_envSettingPath, EnvSettingTextBox.Text);
            StatusTextBlock.Text = $"已儲存：{_envSettingPath}";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"儲存失敗：{ex.Message}";
        }
    }

    private void LoadEnvSetting()
    {
        try
        {
            EnvSettingTextBox.Text = File.Exists(_envSettingPath)
                ? File.ReadAllText(_envSettingPath)
                : string.Empty;
            StatusTextBlock.Text = $"已載入：{_envSettingPath}";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"載入失敗：{ex.Message}";
        }
    }
}
