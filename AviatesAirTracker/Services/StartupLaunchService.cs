using Microsoft.Win32;
using Serilog;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AviatesAirTracker.Services;

// ============================================================
// STARTUP LAUNCH
//
// "Start with Windows" adds the tracker to HKCU's Run key. "Start with the simulator" adds a
// Launch.Addon entry to each installed MSFS's exe.xml, the file the sim reads at startup to
// launch companion apps. Both pass --background, so the tracker starts quietly in the tray and
// a second launch while it's already running just hands over to the running copy.
//
// exe.xml is shared with every other add-on the pilot has installed, so it's edited with care:
// an existing file that can't be parsed is left untouched (and reported) rather than replaced,
// the original is copied to exe.xml.aviates-backup the first time, and the write is atomic.
// ============================================================

public sealed class StartupLaunchService
{
    public const string BackgroundArg = "--background";
    public const string AddonName = "Aviates Air Tracker";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AviatesAirTracker";

    static StartupLaunchService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static string ExePath => Environment.ProcessPath ?? "";

    // ── Windows ─────────────────────────────────────────────────

    public bool SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
                key.SetValue(RunValueName, $"\"{ExePath}\" {BackgroundArg}");
            else if (key.GetValue(RunValueName) != null)
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            Log.Information("[Startup] Start with Windows {State}", enabled ? "enabled" : "disabled");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Startup] Could not update the Run key");
            return false;
        }
    }

    // ── Simulator ───────────────────────────────────────────────

    /// <summary>exe.xml for each simulator installed for this Windows user (Store and Steam
    /// installs of MSFS 2020 and 2024); a sim counts as installed when its folder exists.</summary>
    public static IReadOnlyList<(string Sim, string ExeXmlPath)> InstalledSimulators()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new (string Sim, string Folder)[]
        {
            ("MSFS 2020", Path.Combine(local, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache")),
            ("MSFS 2020 (Steam)", Path.Combine(roaming, "Microsoft Flight Simulator")),
            ("MSFS 2024", Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache")),
            ("MSFS 2024 (Steam)", Path.Combine(roaming, "Microsoft Flight Simulator 2024")),
        };
        return candidates
            .Where(c => Directory.Exists(c.Folder))
            .Select(c => (c.Sim, Path.Combine(c.Folder, "exe.xml")))
            .ToList();
    }

    /// <summary>Adds or removes the tracker in every installed simulator's exe.xml. Returns the
    /// simulators updated and any that couldn't be (with the reason).</summary>
    public (List<string> Updated, List<string> Failed) SetStartWithSimulator(bool enabled)
    {
        var updated = new List<string>();
        var failed = new List<string>();
        foreach (var (sim, path) in InstalledSimulators())
        {
            try
            {
                XDocument? doc = null;
                if (File.Exists(path))
                {
                    try { doc = XDocument.Load(path); }
                    catch (XmlException ex)
                    {
                        failed.Add($"{sim}: its exe.xml is damaged, so it was left as it is");
                        Log.Warning(ex, "[Startup] {Path} could not be parsed -- not modified", path);
                        continue;
                    }
                }
                else if (!enabled)
                {
                    continue;
                }

                var changed = enabled ? WithAddon(ref doc, ExePath) : WithoutAddon(doc!);
                if (!changed) { updated.Add(sim); continue; }

                if (File.Exists(path) && !File.Exists(path + ".aviates-backup"))
                    File.Copy(path, path + ".aviates-backup");
                Save(doc!, path);
                updated.Add(sim);
                Log.Information("[Startup] {Sim} exe.xml {Action}", sim, enabled ? "now launches the tracker" : "no longer launches the tracker");
            }
            catch (Exception ex)
            {
                failed.Add($"{sim}: {ex.Message}");
                Log.Warning(ex, "[Startup] Could not update {Path}", path);
            }
        }
        return (updated, failed);
    }

    /// <summary>Re-points both entries at this exe (a reinstall to another folder would leave
    /// them launching a path that no longer exists).</summary>
    public void SyncOnStartup(bool startWithWindows, bool startWithSimulator)
    {
        if (startWithWindows) SetStartWithWindows(true);
        if (startWithSimulator) SetStartWithSimulator(true);
    }

    // ── exe.xml editing (pure, unit-tested) ─────────────────────

    /// <summary>Adds the tracker's Launch.Addon, or updates it in place. Creates the document
    /// when there is none. Returns whether anything changed.</summary>
    internal static bool WithAddon(ref XDocument? doc, string exePath)
    {
        doc ??= NewDocument();
        var root = doc.Root ?? throw new XmlException("exe.xml has no root element");
        var existing = root.Elements("Launch.Addon").FirstOrDefault(IsOurs);

        if (existing != null &&
            (string?)existing.Element("Path") == exePath &&
            (string?)existing.Element("CommandLine") == BackgroundArg &&
            ((string?)existing.Element("Disabled"))?.Equals("False", StringComparison.OrdinalIgnoreCase) == true)
            return false;

        var addon = new XElement("Launch.Addon",
            new XElement("Name", AddonName),
            new XElement("Disabled", "False"),
            new XElement("Path", exePath),
            new XElement("CommandLine", BackgroundArg));

        if (existing != null) existing.ReplaceWith(addon);
        else root.Add(addon);
        return true;
    }

    /// <summary>Removes the tracker's Launch.Addon, leaving every other add-on untouched.</summary>
    internal static bool WithoutAddon(XDocument doc)
    {
        var ours = doc.Root?.Elements("Launch.Addon").Where(IsOurs).ToList() ?? [];
        foreach (var e in ours) e.Remove();
        return ours.Count > 0;
    }

    private static bool IsOurs(XElement addon) =>
        string.Equals(((string?)addon.Element("Name"))?.Trim(), AddonName, StringComparison.OrdinalIgnoreCase);

    internal static XDocument NewDocument() => new(
        new XDeclaration("1.0", "Windows-1252", null),
        new XElement("SimBase.Document",
            new XAttribute("Type", "SimConnect"),
            new XAttribute("version", "1,0"),
            new XElement("Descr", "Auto launch external applications on MSFS start"),
            new XElement("Filename", "exe.xml"),
            new XElement("Disabled", "False"),
            new XElement("Launch.ManualLoad", "False")));

    private static void Save(XDocument doc, string path)
    {
        var encoding = Encoding.GetEncoding(doc.Declaration?.Encoding ?? "Windows-1252");
        var temp = path + ".tmp";
        using (var writer = XmlWriter.Create(temp, new XmlWriterSettings { Indent = true, Encoding = encoding }))
            doc.Save(writer);
        File.Move(temp, path, overwrite: true);
    }
}
