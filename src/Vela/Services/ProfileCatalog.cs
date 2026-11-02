using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vela.Services;

internal sealed record BrowserProfile(string Id, string Name);
internal static class ProfileContext
{
    internal static string Id { get; private set; } = "default";
    internal static string Name { get; private set; } = "Principal";
    internal static string RootFor(string baseRoot) => Id == "default" ? baseRoot : Path.Combine(baseRoot, "UserProfiles", Id);
    internal static void Initialize(IEnumerable<string> args, string baseRoot)
    {
        var choices = args.Where(a => a.StartsWith("--profile=", StringComparison.Ordinal)).ToArray();
        if (choices.Length > 1) throw new ArgumentException("Alege un singur profil.");
        var id = choices.Length == 0 ? "default" : choices[0][10..];
        if (id != "default" && !Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Identificator de profil invalid.");
        var selected = new ProfileCatalog(baseRoot).All.SingleOrDefault(p => p.Id == id) ?? throw new ArgumentException("Profilul nu mai există.");
        Id = selected.Id; Name = selected.Name;
    }
    internal static void RefreshName(string name) => Name = name;
}

internal sealed class ProfileCatalog(string baseRoot)
{
    private string FilePath => Path.Combine(baseRoot, "profiles.json");
    internal IReadOnlyList<BrowserProfile> All
    {
        get
        {
            foreach (var path in new[] { FilePath, FilePath + ".bak" })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Catalogul de profiluri este prea mare.");
                    var profiles = JsonSerializer.Deserialize<List<BrowserProfile>>(File.ReadAllText(path));
                    if (profiles is null || profiles.Count > 16 || profiles.Any(p => p is null || !ValidId(p.Id) || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 32)) continue;
                    return new[] { profiles.FirstOrDefault(p => p.Id == "default") ?? new("default", "Principal") }.Concat(profiles.Where(p => p.Id != "default")).DistinctBy(p => p.Id).ToArray();
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { Log.Write("Read profile catalog: " + ex.Message); }
            }
            return [new("default", "Principal")];
        }
    }
    private static bool ValidId(string id) => id == "default" || Guid.TryParseExact(id, "N", out _);
    internal BrowserProfile Save(string? id, string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 32 || name.Any(char.IsControl)) throw new ArgumentException("Numele profilului trebuie să aibă 1–32 de caractere.");
        var mutexKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(baseRoot).ToUpperInvariant())))[..24];
        using var mutex = new Mutex(false, @"Local\Vela.Profiles." + mutexKey);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Catalogul este folosit de altă fereastră. Încearcă din nou.");
        try
        {
            var items = All.ToList();
            if (items.Any(p => p.Id != id && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Există deja un profil cu acest nume.");
            var index = id is null ? -1 : items.FindIndex(p => p.Id == id);
            if (id is not null && index < 0) throw new ArgumentException("Profilul nu mai există.");
            if (index < 0 && items.Count >= 16) throw new InvalidOperationException("Poți crea maximum 16 profiluri.");
            var profile = new BrowserProfile(id ?? Guid.NewGuid().ToString("N"), name);
            if (index < 0) items.Add(profile); else items[index] = profile;
            AtomicFile.Write(FilePath, JsonSerializer.Serialize(items));
            return profile;
        }
        finally { mutex.ReleaseMutex(); }
    }
    internal void Open(string id)
    {
        if (!All.Any(p => p.Id == id)) throw new ArgumentException("Profilul nu mai există.");
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Executabilul nu este disponibil.")) { UseShellExecute = false };
        start.ArgumentList.Add("--profile=" + id);
        Process.Start(start);
    }
}
