using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace CrimsonBrowser.Services;

internal sealed record UpdateInfo(Version Version, string ReleaseUrl, string ReleaseName);
internal enum UpdateCheckStatus { NoPublishedRelease, Current, UpdateAvailable }
internal sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update = null);

/// <summary>Checks GitHub Releases only on an explicit user request; no startup polling.</summary>
internal static class UpdateService
{
    private const string Repository = "visualxgame-cmyk/crimson-browser";
    private static readonly HttpClient Client = CreateClient();

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version is { } version
            ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0))
            : new Version(1, 0, 0);

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Repository}/releases/latest");
        using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new UpdateCheckResult(UpdateCheckStatus.NoPublishedRelease);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        string releaseUrl = root.GetProperty("html_url").GetString() ?? string.Empty;
        string releaseName = root.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? tag : tag;

        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? version) ||
            !Uri.TryCreate(releaseUrl, UriKind.Absolute, out Uri? verifiedUrl) ||
            !string.Equals(verifiedUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return new UpdateCheckResult(UpdateCheckStatus.NoPublishedRelease);

        return version > CurrentVersion
            ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, new UpdateInfo(version, verifiedUrl.AbsoluteUri, releaseName))
            : new UpdateCheckResult(UpdateCheckStatus.Current);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"CrimsonBrowser/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
