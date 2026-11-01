using Serilog;
using System.IO;
using System.Text.Json;

namespace AviatesAirTracker.Core.Data;

// ============================================================
// The file behind one JSON-persisted repository.
//
// Writes go to a temp file that then replaces the real one, so a crash, power cut or the
// shutdown watchdog's Kill() mid-write can never leave a half-written file. They are also
// serialized, and the snapshot is taken inside the write lock, so two saves racing from
// different threads can't land an older list on top of a newer one.
//
// A file that exists but can't be parsed is copied aside before anything else happens: the
// repository starts empty in that case, and its next save would otherwise overwrite the only
// copy of the pilot's history.
// ============================================================

internal sealed class JsonFileStore
{
    private readonly object _writeLock = new();
    private readonly string _logName;

    public string FilePath { get; }

    public JsonFileStore(string filePath, string logName)
    {
        FilePath = filePath;
        _logName = logName;
    }

    /// <summary>A store for <paramref name="fileName"/> in %AppData%\AviatesAirTracker.</summary>
    public static JsonFileStore InAppData(string fileName, string logName) => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AviatesAirTracker", fileName),
        logName);

    public T? Read<T>(JsonSerializerOptions? options = null) where T : class
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(FilePath), options);
        }
        catch (Exception ex)
        {
            var aside = $"{FilePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(FilePath, aside, overwrite: false); } catch { /* best effort */ }
            Log.Error(ex, "[{Store}] {Path} could not be read -- kept a copy at {Aside}", _logName, FilePath, aside);
            return null;
        }
    }

    /// <summary>
    /// Persists whatever <paramref name="takeSnapshot"/> returns. The callback runs inside the
    /// write lock and must take the repository's own lock itself; never call this while holding
    /// that lock (lock order is write lock, then repository lock).
    /// </summary>
    public void Write<T>(Func<T> takeSnapshot, JsonSerializerOptions? options = null)
    {
        lock (_writeLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(takeSnapshot(), options);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[{Store}] Failed to save {Path}", _logName, FilePath);
            }
        }
    }
}
