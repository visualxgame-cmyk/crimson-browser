using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace CrimsonBrowser.Services;

internal sealed record UpdateInfo(
    Version Version,
    string ReleaseUrl,
    string ReleaseName,
    string InstallerUrl,
    string ChecksumUrl);
internal enum UpdateCheckStatus { NoPublishedRelease, Current, UpdateAvailable }
internal sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update = null);

/// <summary>Checks GitHub Releases, downloads and verifies updates, and launches the installer.</summary>
internal static class UpdateService
{
    private const string Repository = "visualxgame-cmyk/crimson-browser";
    private static readonly HttpClient Client = CreateClient(TimeSpan.FromSeconds(12));
    private static readonly HttpClient DownloadClient = CreateClient(TimeSpan.FromMinutes(10));

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

        if (version <= CurrentVersion)
            return new UpdateCheckResult(UpdateCheckStatus.Current);

        string installerName = $"CrimsonBrowser-Setup-{version}.exe";
        string checksumName = installerName + ".sha256";
        string? installerUrl = null;
        string? checksumUrl = null;

        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string assetName = asset.TryGetProperty("name", out JsonElement assetNameElement)
                    ? assetNameElement.GetString() ?? string.Empty
                    : string.Empty;
                string assetUrl = asset.TryGetProperty("browser_download_url", out JsonElement assetUrlElement)
                    ? assetUrlElement.GetString() ?? string.Empty
                    : string.Empty;

                if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out Uri? assetUri) ||
                    !string.Equals(assetUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.Equals(assetName, installerName, StringComparison.OrdinalIgnoreCase))
                    installerUrl = assetUri.AbsoluteUri;
                else if (string.Equals(assetName, checksumName, StringComparison.OrdinalIgnoreCase))
                    checksumUrl = assetUri.AbsoluteUri;
            }
        }

        if (installerUrl is null || checksumUrl is null)
            return new UpdateCheckResult(UpdateCheckStatus.NoPublishedRelease);

        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable,
            new UpdateInfo(version, verifiedUrl.AbsoluteUri, releaseName, installerUrl, checksumUrl));
    }

    /// <summary>Downloads a checksum-verified installer, starts it silently, then exits the browser.</summary>
    public static async Task DownloadAndInstallAsync(UpdateInfo update, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string expectedHash = await DownloadExpectedHashAsync(update.ChecksumUrl, cancellationToken);
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrimsonBrowser", "Updates");
        Directory.CreateDirectory(folder);

        string installerPath = Path.Combine(folder, $"CrimsonBrowser-Setup-{update.Version}.exe");
        string temporaryPath = installerPath + ".download";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, update.InstallerUrl);
            using HttpResponseMessage response = await DownloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;

            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (total is > 0) progress?.Report((double)received / total.Value);
            }

            string actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expectedHash), Convert.FromHexString(actualHash)))
            {
                throw new InvalidDataException("The downloaded update did not match its published SHA-256 checksum.");
            }

            // Windows cannot rename or execute the download while FileShare.None holds it open.
            // Dispose also flushes buffered writes before the installer is launched.
            await output.DisposeAsync();
            File.Move(temporaryPath, installerPath, overwrite: true);
            // The independent helper waits for this process to exit before setup touches
            // installed files. Keep the helper outside the directory being updated.
            string helperPath = Path.Combine(folder, "Install-Update.ps1");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Resources", "Install-Update.ps1"), helperPath, true);
            string powershellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var startInfo = new ProcessStartInfo(powershellPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                         "-File", helperPath, "-BrowserProcessId", Environment.ProcessId.ToString(),
                         "-InstallerPath", installerPath, "-InstallDirectory", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) })
                startInfo.ArgumentList.Add(argument);
            using Process helper = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the update installer helper.");
            Application.Current.Shutdown();
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task<string> DownloadExpectedHashAsync(string checksumUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, checksumUrl);
        using HttpResponseMessage response = await DownloadClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        Match match = Regex.Match(text, @"\b[a-fA-F0-9]{64}\b");
        if (!match.Success) throw new InvalidDataException("The published update checksum is invalid.");
        return match.Value.ToUpperInvariant();
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"CrimsonBrowser/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
