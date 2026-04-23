using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace YtDownloader;

public partial class MainWindow : Window
{
    private static readonly Regex YtUrlRegex = new(
        @"^https?://(?:www\.|m\.)?(?:youtube\.com|youtu\.be|music\.youtube\.com)/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly CancellationTokenSource _shutdown = new();
    private DispatcherTimer? _clipboardTimer;
    private string _lastSeenClipboard = "";
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        SaveDirBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Videos", "YouTube");
        Loaded += OnLoaded;
        Closed += (_, _) => { _shutdown.Cancel(); _clipboardTimer?.Stop(); };
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

        _clipboardTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clipboardTimer.Tick += ClipboardTick;
        _clipboardTimer.Start();

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && LooksLikeYoutubeUrl(args[1]))
        {
            UrlBox.Text = args[1].Trim();
            _ = StartDownloadAsync();
        }
    }

    private void ClipboardTick(object? sender, EventArgs e)
    {
        if (WatchClipboardCheck.IsChecked != true) return;
        if (_busy) return;

        string text;
        try
        {
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText().Trim();
        }
        catch { return; }

        if (string.IsNullOrEmpty(text) || text == _lastSeenClipboard) return;
        if (!LooksLikeYoutubeUrl(text)) return;

        _lastSeenClipboard = text;
        UrlBox.Text = text;
        Log($"Clipboard: {text}");
        _ = StartDownloadAsync();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        string? dropped = null;
        if (e.Data.GetDataPresent(DataFormats.Text))
            dropped = (string?)e.Data.GetData(DataFormats.Text);
        else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            dropped = (string?)e.Data.GetData(DataFormats.UnicodeText);

        if (dropped is null) return;
        dropped = dropped.Trim();
        if (!LooksLikeYoutubeUrl(dropped)) return;

        UrlBox.Text = dropped;
        _ = StartDownloadAsync();
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
        await StartDownloadAsync();
    }

    private async Task StartDownloadAsync()
    {
        if (_busy) return;

        var url = UrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this, "Enter a YouTube URL first.", "YT Downloader",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool playlist = false;
        if (UrlContainsPlaylist(url))
        {
            var res = MessageBox.Show(this,
                "This URL contains a playlist.\n\n" +
                "Yes  – download the whole playlist (saved to a subfolder)\n" +
                "No   – download only this one video\n" +
                "Cancel – abort",
                "Playlist detected",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (res == MessageBoxResult.Cancel) return;
            playlist = res == MessageBoxResult.Yes;
        }

        var saveDir = SaveDirBox.Text.Trim();
        try { Directory.CreateDirectory(saveDir); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Can't create folder: " + ex.Message, "YT Downloader",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var quality = ((ComboBoxItem)QualityBox.SelectedItem).Content?.ToString() ?? "1080p";
        var browser = ((ComboBoxItem)BrowserBox.SelectedItem).Content?.ToString() ?? "(none)";
        var audioOnly = AudioOnlyCheck.IsChecked == true;

        _busy = true;
        DownloadBtn.IsEnabled = false;
        Progress.Value = 0;
        SetStatus("Starting…", ok: true);

        var req = new DownloadRequest(url, saveDir, quality, audioOnly, playlist,
            browser == "(none)" ? null : browser);

        try
        {
            var exit = await YtDlpService.DownloadAsync(
                req,
                p => Dispatcher.Invoke(() =>
                {
                    Progress.Value = p.Percent;
                    var item = p.Item is not null ? $"[{p.Item}/{p.Total}] " : "";
                    var eta = p.Eta is null ? "" : $" · ETA {p.Eta}";
                    SetStatus($"Downloading… {item}{p.Percent:0.0}% · {p.Speed}{eta}", ok: true);
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

    private static bool LooksLikeYoutubeUrl(string s)
        => !string.IsNullOrWhiteSpace(s) && YtUrlRegex.IsMatch(s.Trim());

    private static bool UrlContainsPlaylist(string url)
        => url.Contains("list=", StringComparison.OrdinalIgnoreCase)
           || url.Contains("/playlist", StringComparison.OrdinalIgnoreCase);

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
