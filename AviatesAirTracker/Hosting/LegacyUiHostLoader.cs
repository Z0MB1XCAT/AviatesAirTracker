using System.IO;
using System.Reflection;
using Serilog;

namespace AviatesAirTracker.Hosting;

/// <summary>Locates and instantiates the <see cref="ILegacyUiHost"/> implementation.</summary>
public static class LegacyUiHostLoader
{
    private const string LegacyAssemblyName = "AviatesAirTracker.LegacyWeb";

    public static ILegacyUiHost Load()
    {
        // Assembly.Load(string) resolves by simple name against the app's TPA/deps.json
        // list only — it does NOT fall back to probing AppContext.BaseDirectory for a DLL
        // that just happens to sit next to the exe. Since the app never references this
        // library at compile time (that's the whole point of the seam), it is never in the
        // app's deps.json, so Assembly.Load throws FileNotFoundException even though
        // AviatesAirTracker.LegacyWeb.dll is physically present (copied there by the
        // library's CopyToAppOutput target). Assembly.LoadFrom, given the explicit path,
        // loads it directly instead of relying on TPA-based probing.
        var path = Path.Combine(AppContext.BaseDirectory, $"{LegacyAssemblyName}.dll");
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"{LegacyAssemblyName}.dll not found at {path}. It should have been copied " +
                "there by AviatesAirTracker.LegacyWeb's CopyToAppOutput build target — " +
                "rebuild the solution (not just the app project).");

        var assembly = Assembly.LoadFrom(path);

        var implementation = assembly.GetTypes().FirstOrDefault(
            t => typeof(ILegacyUiHost).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            ?? throw new InvalidOperationException(
                $"{LegacyAssemblyName} contains no ILegacyUiHost implementation.");

        Log.Information("[Startup] Legacy UI host type: {Type}", implementation.FullName);

        return (ILegacyUiHost)Activator.CreateInstance(implementation)!;
    }
}
