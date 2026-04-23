using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace YtDownloader;

public record DownloadRequest(
    string Url,
    string OutputDir,
    string Quality,
    bool AudioOnly,
    bool Playlist,
    string? Browser);

public record ProgressInfo(
    double Percent,
    string Speed,
    string? Eta,
    int? Item = null,
    int? Total = null);

public static class YtDlpService
{
    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YtDownloader");

    public static string YtDlpPath => Path.Combine(AppDir, "yt-dlp.exe");
    public static string LocalFfmpegPath => Path.Combine(AppDir, "ffmpeg.exe");

    private const string LatestReleaseApi =
        "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

    public static async Task EnsureYtDlpAsync(Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(AppDir);

        string? current = File.Exists(YtDlpPath)
            ? await GetYtDlpVersionAsync(ct)
            : null;

        string? latest = null;
        try { latest = await GetLatestTagAsync(ct); }
        catch (Exception ex) { log("Update check failed: " + ex.Message); }

        if (current is null)
        {
            log("Downloading yt-dlp…");
            await DownloadYtDlpAsync(latest, log, ct);
        }
        else if (latest is not null && !string.Equals(current, latest, StringComparison.OrdinalIgnoreCase))
        {
            log($"Updating yt-dlp {current} → {latest}…");
            await DownloadYtDlpAsync(latest, log, ct);
        }
        else
        {
            log($"yt-dlp v{current} is up to date.");
        }
    }

    private static async Task<string> GetLatestTagAsync(CancellationToken ct)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YtDownloader", "1.0"));
        http.Timeout = TimeSpan.FromSeconds(15);
        var json = await http.GetStringAsync(LatestReleaseApi, ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("tag_name").GetString()
               ?? throw new InvalidOperationException("no tag_name in release JSON");
    }

    private static async Task DownloadYtDlpAsync(string? tag, Action<string> log, CancellationToken ct)
    {
        var url = tag is null
            ? "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
            : $"https://github.com/yt-dlp/yt-dlp/releases/download/{tag}/yt-dlp.exe";

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YtDownloader", "1.0"));
        http.Timeout = TimeSpan.FromMinutes(3);

        var tmp = YtDlpPath + ".tmp";
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            await src.CopyToAsync(dst, ct);
        }
        if (File.Exists(YtDlpPath)) File.Delete(YtDlpPath);
        File.Move(tmp, YtDlpPath);
        log("yt-dlp installed.");
    }

    public static async Task<string> GetYtDlpVersionAsync(CancellationToken ct)
    {
        if (!File.Exists(YtDlpPath)) return "unknown";
        var psi = new ProcessStartInfo(YtDlpPath, "--version")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return output.Trim();
    }

    public static async Task EnsureFfmpegAsync(Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(AppDir);

        var beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(beside))
        {
            log("Using ffmpeg.exe bundled next to the app.");
            return;
        }

        if (File.Exists(LocalFfmpegPath))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(LocalFfmpegPath);
            if (age.TotalDays < 30)
            {
                log($"ffmpeg is current ({(int)age.TotalDays}d old).");
                return;
            }
            log($"ffmpeg is {(int)age.TotalDays}d old — refreshing…");
        }
        else
        {
            log("ffmpeg not found — downloading (~35 MB, one-time)…");
        }

        const string zipUrl =
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
        var tmpZip = Path.Combine(AppDir, "ffmpeg-download.zip");

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YtDownloader", "1.0"));
            http.Timeout = TimeSpan.FromMinutes(10);

            using (var resp = await http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmpZip);
                await src.CopyToAsync(dst, ct);
            }

            log("Extracting ffmpeg.exe…");
            using (var zip = ZipFile.OpenRead(tmpZip))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    log("Couldn't find ffmpeg.exe inside the zip — aborting update.");
                    return;
                }
                var tmpExe = LocalFfmpegPath + ".tmp";
                entry.ExtractToFile(tmpExe, overwrite: true);
                if (File.Exists(LocalFfmpegPath)) File.Delete(LocalFfmpegPath);
                File.Move(tmpExe, LocalFfmpegPath);
            }
            log("ffmpeg ready.");
        }
        catch (Exception ex)
        {
            log("ffmpeg download failed: " + ex.Message);
            if (!File.Exists(LocalFfmpegPath))
                log("1080p+ merging and MP3 conversion won't work until ffmpeg is installed.");
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
        }
    }

    public static bool TryFindFfmpeg(out string path)
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(beside)) { path = beside; return true; }
        if (File.Exists(LocalFfmpegPath)) { path = LocalFfmpegPath; return true; }
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate)) { path = candidate; return true; }
            }
            catch { }
        }
        path = "";
        return false;
    }

    private static string FormatSelector(string quality, bool audioOnly)
    {
        if (audioOnly) return "bestaudio/best";
        if (quality == "Best") return "bestvideo*+bestaudio/best";
        var h = quality.TrimEnd('p');
        return $"bestvideo[height<={h}]+bestaudio/best[height<={h}]/best";
    }

    private static readonly Regex ProgressRegex = new(
        @"\[download\]\s+([\d.]+)%\s+of\s+~?\s*([\d.]+\w+)\s+at\s+(\S+)(?:\s+ETA\s+(\S+))?",
        RegexOptions.Compiled);

    private static readonly Regex PlaylistItemRegex = new(
        @"\[download\]\s+Downloading\s+(?:item|video)\s+(\d+)\s+of\s+(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static async Task<int> DownloadAsync(
        DownloadRequest req,
        Action<ProgressInfo> onProgress,
        Action<string> onLog,
        CancellationToken ct)
    {
        var outputTemplate = req.Playlist
            ? Path.Combine(req.OutputDir, "%(playlist_title|)s", "%(playlist_index)03d - %(title)s [%(id)s].%(ext)s")
            : Path.Combine(req.OutputDir, "%(title)s [%(id)s].%(ext)s");

        var args = new List<string>
        {
            "--newline",
            "--no-warnings",
            "-f", FormatSelector(req.Quality, req.AudioOnly),
            "-o", outputTemplate,
        };

        if (!req.Playlist) args.Add("--no-playlist");
        else args.Add("--yes-playlist");

        if (req.AudioOnly)
        {
            args.AddRange(new[] { "-x", "--audio-format", "mp3", "--audio-quality", "192K" });
        }
        else
        {
            args.AddRange(new[] { "--merge-output-format", "mp4" });
        }

        if (!string.IsNullOrEmpty(req.Browser))
        {
            args.AddRange(new[] { "--cookies-from-browser", req.Browser });
        }

        if (TryFindFfmpeg(out var ffmpeg))
        {
            args.AddRange(new[] { "--ffmpeg-location", ffmpeg });
        }

        args.Add(req.Url);

        var psi = new ProcessStartInfo
        {
            FileName = YtDlpPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        int? curItem = null, totalItems = null;

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;

            var pl = PlaylistItemRegex.Match(e.Data);
            if (pl.Success)
            {
                curItem = int.Parse(pl.Groups[1].Value);
                totalItems = int.Parse(pl.Groups[2].Value);
            }

            var m = ProgressRegex.Match(e.Data);
            if (m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var pct))
            {
                onProgress(new ProgressInfo(
                    pct,
                    m.Groups[3].Value,
                    m.Groups[4].Success ? m.Groups[4].Value : null,
                    curItem,
                    totalItems));
            }

            onLog(e.Data);
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLog(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }
}
