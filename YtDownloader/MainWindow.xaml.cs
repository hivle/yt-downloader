using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace YtDownloader;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _shutdown = new();
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        SaveDirBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Videos", "YouTube");
        Loaded += OnLoaded;
        Closed += (_, _) => _shutdown.Cancel();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log("Starting up…");
        try
        {
            await YtDlpService.EnsureYtDlpAsync(Log, _shutdown.Token);
            var version = await YtDlpService.GetYtDlpVersionAsync(_shutdown.Token);
            SetStatus($"yt-dlp v{version} ready.", ok: true);
            if (!YtDlpService.TryFindFfmpeg(out _))
            {
                Log("ffmpeg not found. 1080p+ merging and MP3 conversion will fail until installed.");
                Log("Install via: winget install Gyan.FFmpeg   (then restart the app)");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log("Startup error: " + ex.Message);
            SetStatus("yt-dlp setup failed — check log.", ok: false);
        }
    }

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose download folder",
            InitialDirectory = Directory.Exists(SaveDirBox.Text)
                ? SaveDirBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog(this) == true)
            SaveDirBox.Text = dlg.FolderName;
    }

    private async void DownloadClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var url = UrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this, "Enter a YouTube URL first.", "YT Downloader",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var saveDir = SaveDirBox.Text.Trim();
        try { Directory.CreateDirectory(saveDir); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Can't create folder: " + ex.Message, "YT Downloader",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var quality = ((ComboBoxItem)QualityBox.SelectedItem).Content.ToString() ?? "1080p";
        var browser = ((ComboBoxItem)BrowserBox.SelectedItem).Content.ToString() ?? "(none)";
        var audioOnly = AudioOnlyCheck.IsChecked == true;

        _busy = true;
        DownloadBtn.IsEnabled = false;
        Progress.Value = 0;
        SetStatus("Starting…", ok: true);

        var req = new DownloadRequest(url, saveDir, quality, audioOnly,
            browser == "(none)" ? null : browser);

        try
        {
            var exit = await YtDlpService.DownloadAsync(
                req,
                p => Dispatcher.Invoke(() =>
                {
                    Progress.Value = p.Percent;
                    SetStatus($"Downloading… {p.Percent:0.0}% · {p.Speed}"
                              + (p.Eta is null ? "" : $" · ETA {p.Eta}"), ok: true);
                }),
                line => Log(line),
                _shutdown.Token);

            if (exit == 0) SetStatus("Done.", ok: true);
            else SetStatus($"yt-dlp exited with code {exit}. See log.", ok: false);
        }
        catch (OperationCanceledException) { SetStatus("Cancelled.", ok: false); }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            SetStatus("Failed — see log.", ok: false);
        }
        finally
        {
            _busy = false;
            DownloadBtn.IsEnabled = true;
        }
    }

    private void Log(string line) =>
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

    private void SetStatus(string text, bool ok) =>
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = text;
            StatusText.Foreground = ok
                ? (System.Windows.Media.Brush)FindResource("OkBrush")
                : (System.Windows.Media.Brush)FindResource("ErrBrush");
        });
}
