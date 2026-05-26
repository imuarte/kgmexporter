using System.Windows;
using KogamaScripts;

namespace KgmExporter;

public partial class LoginWindow : Window
{
    public string? LoggedInUsername { get; private set; }

    public LoginWindow()
    {
        InitializeComponent();
        var session = LocalAuth.LoadSession();
        if (session != null)
        {
            UsernameBox.Text = session.Username;
            RegionBox.SelectedIndex = session.Region switch
            {
                KogamaRegion.Br => 1,
                KogamaRegion.Friends => 2,
                _ => 0
            };
        }
        Loaded += (_, _) => UsernameBox.Focus();
    }

    private void UsernameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UsernamePlaceholder.Visibility = string.IsNullOrEmpty(UsernameBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private async void SignInBtn_Click(object sender, RoutedEventArgs e)
    {
        string username = UsernameBox.Text.Trim();
        string password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            StatusLabel.Text = "Fill both fields.";
            return;
        }

        KogamaRegion region = ((System.Windows.Controls.ComboBoxItem)RegionBox.SelectedItem).Content?.ToString() switch
        {
            "br" => KogamaRegion.Br,
            "friends" => KogamaRegion.Friends,
            _ => KogamaRegion.Www
        };

        SignInBtn.IsEnabled = false;
        StatusLabel.Foreground = System.Windows.Media.Brushes.Gray;
        StatusLabel.Text = "Signing in...";

        try
        {
            int profileId = await Auth.LoginAsync(username, password, region);
            if (profileId <= 0)
            {
                StatusLabel.Foreground = System.Windows.Media.Brushes.Firebrick;
                StatusLabel.Text = "Login failed.";
                return;
            }
            LocalAuth.SaveSession(new SessionData(username, profileId, region));
            LocalAuth.SaveCookies();
            LoggedInUsername = username;
            DialogResult = true;
            Close();
        }
        catch (System.Exception ex)
        {
            StatusLabel.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            SignInBtn.IsEnabled = true;
        }
    }
}
