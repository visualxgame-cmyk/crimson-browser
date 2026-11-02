using System.IO;
using System.Net.Http;
using System.Text;

namespace Vela.Services;

internal static class BlocklistUpdates
{
    internal const string Source = "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts";
    internal static string FilePath => Path.Combine(AppPaths.Filters, "managed-remote.txt");
    private const int MaximumBytes = 12 * 1024 * 1024;
    internal static string Validate(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaximumBytes || content.Contains("<html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Răspunsul nu este o listă hosts validă.");
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 4096) throw new InvalidDataException("Lista conține o linie prea lungă.");
            var parts = line.Split('#')[0].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] is not ("0.0.0.0" or "127.0.0.1" or "::")) continue;
            foreach (var host in parts.Skip(1))
                if (host.Contains('.') && host.Length <= 253 && Uri.CheckHostName(host) == UriHostNameType.Dns &&
                    !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) hosts.Add(host.ToLowerInvariant());
            if (hosts.Count > 250000) throw new InvalidDataException("Lista depășește 250.000 de domenii.");
        }
        if (hosts.Count < 1000) throw new InvalidDataException("Actualizarea este incompletă: mai puțin de 1.000 de domenii valide. Lista existentă a fost păstrată.");
        return "# StevenBlack/hosts — " + Source + "\n# Updated " + DateTimeOffset.UtcNow.ToString("O") + "\n" + string.Join('\n', hosts.Order()) + "\n";
    }
    internal static async Task<int> UpdateAsync(HttpClient? transport = null)
    {
        using var owned = transport is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(45) } : null;
        var client = transport ?? owned!;
        using var response = await client.GetAsync(Source, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { Scheme: "https", Host: "raw.githubusercontent.com" })
            throw new InvalidDataException("Serverul a redirecționat actualizarea către o sursă necunoscută.");
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("Lista este prea mare.");
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            if (memory.Length + read > MaximumBytes) throw new InvalidDataException("Lista este prea mare.");
            memory.Write(buffer, 0, read);
        }
        var validated = Validate(Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length));
        AtomicFile.Write(FilePath, validated);
        return validated.Count(c => c == '\n') - 2;
    }
    internal static void Rollback()
    {
        if (!File.Exists(FilePath)) throw new InvalidOperationException("Nu există o actualizare de anulat.");
        if (File.Exists(FilePath + ".bak"))
        {
            var previous = File.ReadAllText(FilePath + ".bak");
            // Previous managed files contain plain domains, produced only after full validation.
            if (!previous.StartsWith("# StevenBlack/hosts", StringComparison.Ordinal)) throw new InvalidDataException("Copia listei nu este validă.");
            AtomicFile.Write(FilePath, previous);
        }
        else File.Delete(FilePath); // The immutable bundled list becomes active again.
    }
}
