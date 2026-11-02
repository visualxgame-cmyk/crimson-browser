using System.IO;
using System.Text.Json;
using System.Threading;

namespace Vela.Services;

internal sealed record SitePreference(double Zoom = 1, bool ShieldEnabled = true);
internal static class SitePreferencesStore
{
    static SitePreferencesStore() { }
    private static readonly string FilePath = Path.Combine(AppPaths.Data, "site-preferences.json");
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, SitePreference> Items = Load();
    private static readonly Timer SaveTimer = new(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    public static SitePreference Get(string? origin) { lock (Gate) return origin is not null && Items.TryGetValue(origin, out var value) ? value : new(); }
    public static string[] DisabledOrigins { get { lock (Gate) return Items.Where(p => !p.Value.ShieldEnabled).Select(p => p.Key).ToArray(); } }
    internal static Dictionary<string, SitePreference> Snapshot { get { lock (Gate) return new(Items); } }
    internal static void ValidateSnapshot(Dictionary<string, SitePreference> items)
    {
        if (items.Count > 20000 || items.Any(p => SiteDataService.Origin(p.Key) != p.Key || p.Value is null ||
            !double.IsFinite(p.Value.Zoom) || p.Value.Zoom is < .25 or > 5)) throw new InvalidDataException("Preferințele pe site din backup nu sunt valide.");
    }
    internal static void Restore(Dictionary<string, SitePreference> items)
    {
        ValidateSnapshot(items);
        lock (Gate) { AtomicFile.Write(FilePath, JsonSerializer.Serialize(items)); Items.Clear(); foreach (var pair in items) Items.Add(pair.Key, pair.Value); }
    }
    public static void SetZoom(string origin, double zoom) => Set(origin, Get(origin) with { Zoom = Math.Clamp(zoom, .25, 5) });
    public static void SetShield(string origin, bool enabled) => Set(origin, Get(origin) with { ShieldEnabled = enabled });
    private static void Set(string origin, SitePreference preference)
    {
        origin = SiteDataService.Origin(origin) ?? throw new ArgumentException("Site invalid.");
        lock (Gate)
        {
            if (preference == new SitePreference()) Items.Remove(origin);
            else Items[origin] = preference;
            SaveTimer.Change(400, Timeout.Infinite);
        }
    }
    public static void Flush()
    {
        lock (Gate)
        {
            try { AtomicFile.Write(FilePath, JsonSerializer.Serialize(Items)); }
            catch (Exception ex) { Log.Write("Save site preferences: " + ex.Message); }
        }
    }
    private static Dictionary<string, SitePreference> Load()
    {
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var loaded = JsonSerializer.Deserialize<Dictionary<string, SitePreference>>(File.ReadAllText(path));
                if (loaded is null) continue;
                return loaded.Where(p => SiteDataService.Origin(p.Key) is not null && p.Value is not null &&
                    double.IsFinite(p.Value.Zoom) && p.Value.Zoom is >= .25 and <= 5).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { Log.Write("Read site preferences: " + ex.Message); }
        }
        return new(StringComparer.OrdinalIgnoreCase);
    }
}
