using System;
using System.Windows;
using System.Windows.Threading;

namespace KgmExporter;

public partial class FirstLaunchWindow : Window
{
    private static readonly DateTime ShutdownUtc = new(2026, 5, 29, 0, 0, 0, DateTimeKind.Utc);

    private DispatcherTimer? _timer;

    public bool OptedIn { get; private set; }

    public FirstLaunchWindow()
    {
        InitializeComponent();
        UpdateCountdown();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateCountdown();
        _timer.Start();
        Closed += (_, _) => _timer?.Stop();
    }

    private void UpdateCountdown()
    {
        TimeSpan left = ShutdownUtc - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            CountdownLabel.Text = "GONE.";
            return;
        }
        CountdownLabel.Text = $"{left.Days}d {left.Hours:D2}h {left.Minutes:D2}m {left.Seconds:D2}s";
    }

    private void YesBtn_Click(object sender, RoutedEventArgs e)
    {
        OptedIn = true;
        DialogResult = true;
        Close();
    }

    private void NoBtn_Click(object sender, RoutedEventArgs e)
    {
        OptedIn = false;
        DialogResult = true;
        Close();
    }
}
