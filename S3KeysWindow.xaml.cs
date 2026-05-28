using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace KgmExporter;

public partial class S3KeysWindow : Window
{
    public S3KeysWindow()
    {
        InitializeComponent();

        var settings = LocalSettings.Load();
        AccessKeyBox.Text = settings.S3AccessKey ?? "";
        SecretKeyBox.Text = settings.S3SecretKey ?? "";
        Loaded += (_, _) => AccessKeyBox.Focus();
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        string? access = string.IsNullOrWhiteSpace(AccessKeyBox.Text) ? null : AccessKeyBox.Text.Trim();
        string? secret = string.IsNullOrWhiteSpace(SecretKeyBox.Text) ? null : SecretKeyBox.Text.Trim();
        var settings = LocalSettings.Load() with { S3AccessKey = access, S3SecretKey = secret };
        LocalSettings.Save(settings);
        DialogResult = true;
        Close();
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        AccessKeyBox.Text = "";
        SecretKeyBox.Text = "";
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = e.Uri.AbsoluteUri,
            UseShellExecute = true,
        });
        e.Handled = true;
    }
}
