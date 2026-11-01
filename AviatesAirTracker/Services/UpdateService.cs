using AviatesAirTracker.Platform;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Microsoft.UI.Xaml;

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
    string? ReleasePageUrl,
    // GitHub's own SHA-256 of the uploaded installer (the asset's "digest", lower-case hex) and
    // its size. The download must match both before it is run.
    string  Sha256 = "",
    long    SizeBytes = 0
);

/// <summary>Where a mandatory update install has got to, for the shell's blocking overlay.</summary>
public enum UpdateStage { Downloading, Verifying, Installing }

public class UpdateService : IDisposable
{
    private const string GITHUB_OWNER = "z0mb1xcat";
    private const string GITHUB_REPO  = "AviatesAirTracker";
    private const string API_URL      = $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases/latest";

    private readonly HttpClient _http;
    private readonly IUiDispatcher _ui;
    private bool _disposed;

    public UpdateInfo? AvailableUpdate  { get; private set; }
    public bool        IsChecking       { get; private set; }
    public bool        IsDownloading    { get; private set; }
    public int         DownloadProgress { get; private set; }

    public event EventHandler<UpdateInfo>? UpdateFound;
    public event EventHandler<int>?        DownloadProgressChanged;
    // Raised as an install starts and moves between stages, and if it fails (the app then keeps
    // running and tries again at the next check). Raised on whatever thread is installing.
    public event EventHandler<(UpdateInfo Info, UpdateStage Stage)>? InstallStageChanged;
    public event EventHandler<string>?                               InstallFailed;

    public UpdateService(IUiDispatcher ui)
    {
        _ui = ui;
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
            var digest       = installerAsset["digest"]?.ToString() ?? "";
            var size         = installerAsset["size"]?.Value<long>() ?? 0;

            // Only ever install from GitHub, over HTTPS, with a published SHA-256 to check the
            // download against. A release without one is skipped rather than trusted.
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var dlUri) ||
                dlUri.Scheme != Uri.UriSchemeHttps ||
                !dlUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("[Update] Release {Tag} has no verifiable installer (digest '{Digest}', url {Url}) -- skipped",
                    tagName, digest, downloadUrl);
                return;
            }

            var info = new UpdateInfo(
                Version:       tagName.TrimStart('v'),
                DownloadUrl:   downloadUrl,
                ReleaseNotes:  releaseNotes,
                ReleasePageUrl: releaseUrl,
                Sha256:        digest["sha256:".Length..].ToLowerInvariant(),
                SizeBytes:     size
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
        InstallStageChanged?.Invoke(this, (info, UpdateStage.Downloading));

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

            // Scoped so the file is closed before the installer is launched below: File.Create
            // opens it with FileShare.None, and while that handle was still open (it used to
            // live until the end of the method) Windows refused to execute the file.
            await using (var src  = await response.Content.ReadAsStreamAsync())
            await using (var dest = File.Create(destPath))
            {
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
            }

            if (total > 0 && downloaded != total)
                throw new IOException($"Update download incomplete ({downloaded} of {total} bytes)");

            DownloadProgress = 100;
            DownloadProgressChanged?.Invoke(this, 100);

            InstallStageChanged?.Invoke(this, (info, UpdateStage.Verifying));
            if (!await VerifyInstallerAsync(destPath, info))
            {
                TryDelete(destPath);
                throw new IOException("The downloaded update didn't match the published checksum and was discarded.");
            }
            InstallStageChanged?.Invoke(this, (info, UpdateStage.Installing));

            Log.Information("[Update] Download complete. Launching installer: {Path}", destPath);

            // /VERYSILENT: no wizard UI
            // /SUPPRESSMSGBOXES: no dialogs
            // /NORESTART: never reboot Windows
            // The installer's [Run] section relaunches the app automatically after install.
            Process.Start(new ProcessStartInfo
            {
                FileName        = destPath,
                Arguments       = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true,
            });

            // Shut down the app so the installer can overwrite the running EXE. Mirrors
            // App.xaml.cs's fatal-startup-failure path: Shutdown() must run before Exit()
            // or the EFB/SimConnect/Discord teardown and the log flush never happen, right
            // when the installer is about to need the process gone.
            await Task.Delay(500);
            _ui.Invoke(() =>
            {
                (Microsoft.UI.Xaml.Application.Current as App)?.Shutdown();
                Microsoft.UI.Xaml.Application.Current.Exit();
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Update] Download/install failed");
            IsDownloading    = false;
            DownloadProgress = 0;
            InstallFailed?.Invoke(this, ex.Message);
            throw;
        }
    }

    private static async Task<bool> VerifyInstallerAsync(string path, UpdateInfo info)
    {
        var length = new FileInfo(path).Length;
        if (info.SizeBytes > 0 && length != info.SizeBytes)
        {
            Log.Error("[Update] Installer size {Actual} != published {Expected}", length, info.SizeBytes);
            return false;
        }

        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream)).ToLowerInvariant();
        if (!string.Equals(hash, info.Sha256, StringComparison.Ordinal))
        {
            Log.Error("[Update] Installer SHA-256 {Actual} != published {Expected}", hash, info.Sha256);
            return false;
        }

        Log.Information("[Update] Installer verified (SHA-256 {Hash})", hash);
        return true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* temp file; best effort */ }
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
