using System.Net;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vela.Services;

internal sealed record UpdateResult(Version LatestVersion, string ReleaseUrl, bool IsNewer);

internal static class UpdateService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Regex Segment = new("^[a-zA-Z0-9_.-]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static string NormalizeRepository(string input)
    {
        input = input.Trim().TrimEnd('/');
        if (input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
                throw new ArgumentException("Folosește adresa unui repository public github.com.");
            input = uri.AbsolutePath.Trim('/');
        }
        var parts = input.Split('/');
        if (parts.Length < 2 || parts.Length > 4 || parts.Skip(2).Any(p => p is not ("releases" or "latest")))
            throw new ArgumentException("Scrie utilizator/repository sau adresa sa GitHub.");
        var owner = parts[0];
        var repo = parts[1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        if (owner.Length is < 1 or > 39 || repo.Length is < 1 or > 100 ||
            !Segment.IsMatch(owner) || !Segment.IsMatch(repo) || owner is "." or ".." || repo is "." or "..")
            throw new ArgumentException("Numele repository-ului GitHub nu este valid.");
        return owner + "/" + repo;
    }

    public static async Task<UpdateResult> CheckAsync(string repository, HttpClient? client = null, CancellationToken cancellation = default)
    {
        repository = NormalizeRepository(repository);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + repository + "/releases/latest");
        request.Headers.UserAgent.ParseAdd("Vela/" + AppConstants.Version);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await (client ?? Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("Repository-ul nu există, nu este public sau nu are încă o versiune publicată.");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("GitHub a limitat temporar verificările. Încearcă mai târziu.");
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(512 * 1024, cancellation);
        using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        return ParseRelease(repository, release.RootElement);
    }

    internal static UpdateResult ParseRelease(string repository, JsonElement release)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
            throw new InvalidDataException("Răspunsul nu descrie o versiune stabilă publică.");
        var tag = release.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var version)) throw new InvalidDataException("Eticheta versiunii trebuie să aibă forma v1.2.3.");
        var url = release.GetProperty("html_url").GetString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
            uri.UserInfo.Length != 0 || !uri.IsDefaultPort || !uri.AbsolutePath.StartsWith("/" + repository + "/releases/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Adresa versiunii nu aparține repository-ului configurat.");
        var installed = Version.Parse(AppConstants.Version);
        // Version treats a missing revision as -1; normalize equivalent 1.2 / 1.2.0 tags.
        Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        return new(version, uri.AbsoluteUri, Normalize(version) > Normalize(installed));
    }
}
