using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;

namespace AviatesAirTracker.Services;

// ============================================================
// UPDATE SERVICE
//
// Checks GitHub Releases for a newer version of the app and
// provides a one-click download + install flow.
//
// Flow:
//   1. CheckAsync() is called on startup (8s delay).
//   2. If a newer tag exists and its installer asset is present,
//      UpdateFound fires and AvailableUpdate is set.
//   3. DownloadAndInstallAsync() downloads the Setup EXE to %TEMP%,
//      launches it, then shuts down the running process so the
//      installer can overwrite the EXE cleanly.
// ============================================================

public record UpdateInfo(
    string  Version,
    string  DownloadUrl,
    string? ReleaseNotes,
    string? ReleasePageUrl
);

public class UpdateService : IDisposable
{
    private const string GITHUB_OWNER = "z0mb1xcat";
    private const string GITHUB_REPO  = "AviatesAirTracker";
    private const string API_URL      = $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases/latest";

    private readonly HttpClient _http;
    private bool _disposed;

    public UpdateInfo? AvailableUpdate  { get; private set; }
    public bool        IsChecking       { get; private set; }
    public bool        IsDownloading    { get; private set; }
    public int         DownloadProgress { get; private set; }

    public event EventHandler<UpdateInfo>? UpdateFound;
    public event EventHandler<int>?        DownloadProgressChanged;

    public UpdateService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "AviatesAirTracker-UpdateChecker/1.0");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    // ─── Check for update ───────────────────────────────────────────────────

    public async Task CheckAsync()
    {
        if (IsChecking || IsDownloading) return;

        IsChecking = true;
        try
        {
            Log.Information("[Update] Checking for updates at {Url}", API_URL);

            string json;
            try
            {
                json = await _http.GetStringAsync(API_URL);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Update] Could not reach GitHub releases API");
                return;
            }

            var release = JObject.Parse(json);
            var tagName = release["tag_name"]?.ToString();
            if (string.IsNullOrEmpty(tagName))
            {
                Log.Warning("[Update] GitHub response missing tag_name");
                return;
            }

            if (!TryParseVersion(tagName, out var latestVer))
            {
                Log.Warning("[Update] Could not parse version from tag '{Tag}'", tagName);
                return;
            }

            var current = CurrentVersion();
            Log.Information("[Update] Current: {Current}  Latest: {Latest}", current, latestVer);

            if (latestVer <= current)
            {
                Log.Information("[Update] Already up to date.");
                return;
            }

            // Find the Setup installer asset
            var assets = release["assets"] as JArray;
            var installerAsset = assets?
                .FirstOrDefault(a =>
                {
                    var name = a["name"]?.ToString() ?? "";
                    return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && name.Contains("Setup", StringComparison.OrdinalIgnoreCase);
                });

            if (installerAsset == null)
            {
                Log.Warning("[Update] No installer asset found in release {Tag}", tagName);
                return;
            }

            var downloadUrl  = installerAsset["browser_download_url"]?.ToString() ?? "";
            var releaseNotes = release["body"]?.ToString();
            var releaseUrl   = release["html_url"]?.ToString();

            var info = new UpdateInfo(
                Version:       tagName.TrimStart('v'),
                DownloadUrl:   downloadUrl,
                ReleaseNotes:  releaseNotes,
                ReleasePageUrl: releaseUrl
            );

            AvailableUpdate = info;
            UpdateFound?.Invoke(this, info);
            Log.Information("[Update] Update available: {Version}", info.Version);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Update] Unexpected error during update check");
        }
        finally
        {
            IsChecking = false;
        }
    }

    // ─── Download and install ────────────────────────────────────────────────

    public async Task DownloadAndInstallAsync()
    {
        if (AvailableUpdate == null || IsDownloading) return;

        var info = AvailableUpdate;
        IsDownloading    = true;
        DownloadProgress = 0;

        try
        {
            var fileName  = $"AviatesUpdate_v{info.Version}.exe";
            var destPath  = Path.Combine(Path.GetTempPath(), fileName);

            Log.Information("[Update] Downloading {Url} → {Path}", info.DownloadUrl, destPath);

            using var response = await _http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var total  = response.Content.Headers.ContentLength ?? -1L;
            var buffer = new byte[81920]; // 80 KB chunks
            long downloaded = 0;

            await using var src  = await response.Content.ReadAsStreamAsync();
            await using var dest = File.Create(destPath);

            while (true)
            {
                var read = await src.ReadAsync(buffer);
                if (read == 0) break;
                await dest.WriteAsync(buffer.AsMemory(0, read));
                downloaded += read;

                if (total > 0)
                {
                    DownloadProgress = (int)(downloaded * 100 / total);
                    DownloadProgressChanged?.Invoke(this, DownloadProgress);
                }
            }

            DownloadProgress = 100;
            DownloadProgressChanged?.Invoke(this, 100);

            Log.Information("[Update] Download complete. Launching installer: {Path}", destPath);

            Process.Start(new ProcessStartInfo
            {
                FileName        = destPath,
                UseShellExecute = true,
            });

            // Shut down the app so the installer can overwrite the running EXE
            await Task.Delay(500);
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Update] Download/install failed");
            IsDownloading    = false;
            DownloadProgress = 0;
            throw;
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static Version CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var s = tag.TrimStart('v');
        if (!Version.TryParse(s, out var parsed))
        {
            version = new Version(0, 0, 0);
            return false;
        }
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
