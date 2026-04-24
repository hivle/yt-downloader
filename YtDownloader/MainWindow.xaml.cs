using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;

namespace YtDownloader;

public partial class MainWindow : Window
{
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s<>""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DestinationRegex = new(
        @"\[(?:download|Merger)\]\s+(?:Destination:\s+|Merging formats into\s+""?)(.+?)""?$",
        RegexOptions.Compiled);

    private readonly CancellationTokenSource _shutdown = new();
    public ObservableCollection<DownloadJob> Jobs { get; } = new();

    private double _savedWidth, _savedHeight, _savedMinWidth, _savedMinHeight;
    private ResizeMode _savedResizeMode;

    public MainWindow()
    {
        InitializeComponent();
        SaveDirBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Videos", "YouTube");
        JobsList.ItemsSource = Jobs;
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
            var extracted = ExtractUrl(args[1]);
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

    private void EnterMiniClick(object sender, RoutedEventArgs e)
    {
        _savedWidth = Width;
        _savedHeight = Height;
        _savedMinWidth = MinWidth;
        _savedMinHeight = MinHeight;
        _savedResizeMode = ResizeMode;

        MainPanel.Visibility = Visibility.Collapsed;
        MiniPanel.Visibility = Visibility.Visible;

        MinWidth = 300;
        MinHeight = 160;
        Width = 360;
        Height = 170;
        MaxWidth = 360;
        MaxHeight = 170;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        UpdateMiniStatus();
    }

    private void ExitMiniClick(object sender, RoutedEventArgs e)
    {
        MainPanel.Visibility = Visibility.Visible;
        MiniPanel.Visibility = Visibility.Collapsed;

        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = _savedResizeMode == 0 ? ResizeMode.CanResize : _savedResizeMode;
        MinWidth = _savedMinWidth > 0 ? _savedMinWidth : 440;
        MinHeight = _savedMinHeight > 0 ? _savedMinHeight : 420;
        Width = _savedWidth > 0 ? _savedWidth : 560;
        Height = _savedHeight > 0 ? _savedHeight : 540;
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
            Log("Dropped data did not contain a URL.");
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
                var url = ExtractUrl(text);
                if (url is not null) return url;
            }
            catch { }
        }
        return null;
    }

    private static string? ExtractUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = UrlRegex.Match(text.Trim());
        return m.Success ? m.Value : null;
    }

    private void OpenFolderClick(object sender, RoutedEventArgs e)
    {
        var dir = SaveDirBox.Text.Trim();
        try { Directory.CreateDirectory(dir); } catch { }
        if (!Directory.Exists(dir))
        {
            MessageBox.Show(this, "Folder doesn't exist:\n\n" + dir, "YT Downloader",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't open folder: " + ex.Message, "YT Downloader",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
        await StartDownloadAsync();
    }

    private async void FromBrowserClick(object sender, RoutedEventArgs e)
    {
        FromBrowserBtn.IsEnabled = false;
        SetStatus("Looking for a browser window…", ok: true);

        var selfHwnd = new WindowInteropHelper(this).Handle;
        (string? url, string? title) = await Task.Run(() =>
            BrowserUrlService.TryGetTopmostBrowserUrl(selfHwnd));

        FromBrowserBtn.IsEnabled = true;

        if (url is null)
        {
            MessageBox.Show(this,
                "Couldn't find a browser window. Make sure Chrome / Edge / Firefox / Brave etc. is open behind this app, then try again.",
                "No browser found", MessageBoxButton.OK, MessageBoxImage.Information);
            SetStatus("No browser window found.", ok: false);
            return;
        }

        Log($"Got URL from {title}: {url}");
        UrlBox.Text = url;

        if (!UrlRegex.IsMatch(url))
        {
            MessageBox.Show(this,
                "The active browser tab isn't a normal http/https URL:\n\n" + url,
                "Not a usable URL", MessageBoxButton.OK, MessageBoxImage.Information);
            SetStatus("Not a usable URL.", ok: false);
            return;
        }

        await StartDownloadAsync();
    }

    private async Task StartDownloadAsync()
    {
        var url = UrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this, "Enter a URL first.", "YT Downloader",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool playlist = false;
        if (UrlIsChannel(url))
        {
            var res = MessageBox.Show(this,
                "This URL points to a channel.\n\n" +
                "OK     – download every video on the channel (this can be hundreds of files / many GB)\n" +
                "Cancel – abort",
                "Channel detected",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (res != MessageBoxResult.OK) return;
            playlist = true;
        }
        else if (UrlContainsPlaylist(url))
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

        var quality = ((ComboBoxItem)QualityBox.SelectedItem).Content?.ToString() ?? "480p";
        var browser = ((ComboBoxItem)BrowserBox.SelectedItem).Content?.ToString() ?? "(none)";
        var audioOnly = AudioOnlyCheck.IsChecked == true;

        var req = new DownloadRequest(url, saveDir, quality, audioOnly, playlist,
            browser == "(none)" ? null : browser);

        var job = new DownloadJob(url) { Status = "Starting…" };
        Jobs.Add(job);
        UrlBox.Clear();

        SetStatus($"{Jobs.Count(j => j.IsActive)} active.", ok: true);
        UpdateMiniStatus();

        // Fire-and-forget; each job runs independently.
        _ = RunJobAsync(job, req);

        await Task.CompletedTask;
    }

    private async Task RunJobAsync(DownloadJob job, DownloadRequest req)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token, job.Cts.Token);

        try
        {
            var exit = await YtDlpService.DownloadAsync(
                req,
                p => Dispatcher.Invoke(() =>
                {
                    job.Percent = p.Percent;
                    var item = p.Item is not null ? $"[{p.Item}/{p.Total}] " : "";
                    var eta = p.Eta is null ? "" : $" · ETA {p.Eta}";
                    job.Status = $"{item}{p.Percent:0.0}% · {p.Speed}{eta}";
                    UpdateMiniStatus();
                }),
                line =>
                {
                    Log(line);
                    var t = ExtractTitleFromLine(line);
                    if (t is not null)
                        Dispatcher.Invoke(() => { if (job.Title is null) job.Title = t; });
                },
                linked.Token);

            Dispatcher.Invoke(() =>
            {
                job.IsActive = false;
                if (exit == 0) { job.Percent = 100; job.Status = "Done."; }
                else job.Status = $"yt-dlp exit {exit} — see log.";
                RefreshGlobalStatus();
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.Invoke(() =>
            {
                job.IsActive = false;
                job.Status = "Cancelled.";
                RefreshGlobalStatus();
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                job.IsActive = false;
                job.Status = "Failed: " + ex.Message;
                RefreshGlobalStatus();
            });
            Log("ERROR: " + ex.Message);
        }
    }

    private void JobActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not DownloadJob job) return;
        if (job.IsActive)
        {
            try { job.Cts.Cancel(); } catch { }
        }
        else
        {
            Jobs.Remove(job);
            RefreshGlobalStatus();
        }
    }

    private void ClearFinishedClick(object sender, RoutedEventArgs e)
    {
        for (int i = Jobs.Count - 1; i >= 0; i--)
            if (!Jobs[i].IsActive) Jobs.RemoveAt(i);
        RefreshGlobalStatus();
    }

    private void RefreshGlobalStatus()
    {
        var active = Jobs.Count(j => j.IsActive);
        var done = Jobs.Count - active;
        SetStatus(active == 0 && done == 0 ? "Ready." : $"{active} active · {done} finished.", ok: true);
        UpdateMiniStatus();
    }

    private void UpdateMiniStatus()
    {
        var active = Jobs.Where(j => j.IsActive).ToList();
        if (active.Count == 0)
        {
            var done = Jobs.Count;
            MiniStatus.Text = done > 0 ? $"{done} finished" : "Ready";
            MiniProgress.Value = 0;
            return;
        }
        if (active.Count == 1)
        {
            var j = active[0];
            MiniStatus.Text = $"{j.Percent:0}% · {Truncate(j.Display, 38)}";
            MiniProgress.Value = j.Percent;
        }
        else
        {
            var avg = active.Average(j => j.Percent);
            MiniStatus.Text = $"{active.Count} active · avg {avg:0}%";
            MiniProgress.Value = avg;
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string? ExtractTitleFromLine(string line)
    {
        var m = DestinationRegex.Match(line);
        if (!m.Success) return null;
        var path = m.Groups[1].Value.Trim().Trim('"');
        try
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var idx = name.LastIndexOf(" [");
            return (idx > 0 ? name[..idx] : name).Trim();
        }
        catch { return null; }
    }

    private static bool UrlContainsPlaylist(string url)
        => url.Contains("list=", StringComparison.OrdinalIgnoreCase)
           || url.Contains("/playlist", StringComparison.OrdinalIgnoreCase);

    private static bool UrlIsChannel(string url)
    {
        if (url.Contains("/watch", StringComparison.OrdinalIgnoreCase)) return false;
        var lower = url.ToLowerInvariant();
        return lower.Contains("/@")
            || lower.Contains("/channel/")
            || lower.Contains("/c/")
            || lower.Contains("/user/");
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
