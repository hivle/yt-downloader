using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace YtDownloader;

public partial class MainWindow : Window
{
    private static readonly Regex YtUrlRegex = new(
        @"https?://(?:www\.|m\.)?(?:youtube\.com|youtu\.be|music\.youtube\.com)/\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            await YtDlpService.EnsureFfmpegAsync(Log, _shutdown.Token);
            var version = await YtDlpService.GetYtDlpVersionAsync(_shutdown.Token);
            SetStatus($"yt-dlp v{version} ready.", ok: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log("Startup error: " + ex.Message);
            SetStatus("yt-dlp setup failed — check log.", ok: false);
        }

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
        {
            var extracted = ExtractYoutubeUrl(args[1]);
            if (extracted is not null)
            {
                UrlBox.Text = extracted;
                _ = StartDownloadAsync();
            }
        }
    }

    private void TopmostCheck_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostCheck.IsChecked == true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = ContainsUrlData(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var url = ExtractUrlFromDropData(e.Data);
        if (url is null)
        {
            Log("Dropped data did not contain a YouTube URL.");
            return;
        }

        UrlBox.Text = url;
        Log($"Dropped: {url}");
        _ = StartDownloadAsync();
        e.Handled = true;
    }

    private static bool ContainsUrlData(IDataObject data)
    {
        return data.GetDataPresent("UniformResourceLocatorW")
               || data.GetDataPresent("UniformResourceLocator")
               || data.GetDataPresent(DataFormats.UnicodeText)
               || data.GetDataPresent(DataFormats.Text)
               || data.GetDataPresent(DataFormats.StringFormat);
    }

    private static string? ExtractUrlFromDropData(IDataObject data)
    {
        foreach (var fmt in new[] { "UniformResourceLocatorW", "UniformResourceLocator",
                                     DataFormats.UnicodeText, DataFormats.Text, DataFormats.StringFormat })
        {
            if (!data.GetDataPresent(fmt)) continue;
            try
            {
                var obj = data.GetData(fmt);
                string? text = obj switch
                {
                    string s => s,
                    byte[] b when fmt == "UniformResourceLocatorW"
                        => System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0'),
                    byte[] b when fmt == "UniformResourceLocator"
                        => System.Text.Encoding.ASCII.GetString(b).TrimEnd('\0'),
                    MemoryStream ms => new StreamReader(ms,
                        fmt == "UniformResourceLocatorW" ? System.Text.Encoding.Unicode : System.Text.Encoding.ASCII
                        ).ReadToEnd().TrimEnd('\0'),
                    _ => null,
                };
                var url = ExtractYoutubeUrl(text);
                if (url is not null) return url;
            }
            catch { }
        }
        return null;
    }

    private static string? ExtractYoutubeUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = YtUrlRegex.Match(text.Trim());
        return m.Success ? m.Value : null;
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
