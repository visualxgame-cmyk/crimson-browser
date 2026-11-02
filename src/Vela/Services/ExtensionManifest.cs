using System.Text.Json;
using System.IO;

namespace Vela.Services;

internal sealed record ExtensionManifestInfo(string Name, string Version, IReadOnlyList<string> Access)
{
    internal string AccessSummary => Access.Count == 0 ? "Nu declară permisiuni suplimentare." : string.Join("\n", Access.Select(value => "• " + value));
}

internal static class ExtensionManifest
{
    private const int MaximumManifestBytes = 256 * 1024;
    private const int MaximumEntries = 48;

    internal static ExtensionManifestInfo Read(string folder)
    {
        var manifestPath = Path.Combine(Path.GetFullPath(folder), "manifest.json");
        var file = new FileInfo(manifestPath);
        if (!file.Exists) throw new InvalidDataException("Folderul ales nu conține manifest.json.");
        if (file.Length > MaximumManifestBytes) throw new InvalidDataException("manifest.json este prea mare.");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("manifest.json trebuie să conțină un obiect JSON.");
        var root = document.RootElement;
        var name = Text(root, "name") ?? Path.GetFileName(Path.GetFullPath(folder));
        var version = Text(root, "version") ?? "versiune nedeclarată";
        var access = new List<string>();
        AddStrings(root, "permissions", "Permisiune", access);
        AddStrings(root, "optional_permissions", "Permisiune opțională", access);
        AddStrings(root, "host_permissions", "Acces la site", access);
        if (root.TryGetProperty("content_scripts", out var contentScripts) && contentScripts.ValueKind == JsonValueKind.Array)
            foreach (var script in contentScripts.EnumerateArray())
                if (script.ValueKind == JsonValueKind.Object) AddStrings(script, "matches", "Rulează pe", access);
        return new ExtensionManifestInfo(name, version, access.Distinct(StringComparer.Ordinal).Take(MaximumEntries).ToArray());
    }

    private static string? Text(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 and <= 200 } text ? text : null;

    private static void AddStrings(JsonElement root, string property, string label, List<string> output)
    {
        if (!root.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return;
        foreach (var value in values.EnumerateArray())
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 300 } text && output.Count < MaximumEntries)
                output.Add(label + ": " + text);
    }
}
