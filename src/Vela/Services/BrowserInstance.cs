using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vela.Services;

/// <summary>One process per profile. The pipe waits asynchronously and only accepts this Windows user.</summary>
internal sealed class BrowserInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    public bool IsPrimary { get; }

    public BrowserInstance(string profileKey)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileKey.ToUpperInvariant())))[..24];
        _pipeName = "Vela." + key;
        _mutex = new Mutex(true, @"Local\" + _pipeName, out var created);
        IsPrimary = created;
    }

    public static string[] NavigationTargets(IEnumerable<string> args)
    {
        var targets = new List<string>();
        foreach (var raw in args.Take(32))
        {
            var value = raw.Trim();
            if (value.Length is 0 or > 8192 || value.StartsWith('-')) continue;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0)
                targets.Add(uri.AbsoluteUri);
            else if (Path.IsPathFullyQualified(value) && File.Exists(value) && Path.GetExtension(value).ToLowerInvariant() is ".html" or ".htm")
                targets.Add(new Uri(Path.GetFullPath(value)).AbsoluteUri);
            else if (Uri.TryCreate(value, UriKind.Absolute, out var file) && file.IsFile && file.IsLoopback &&
                Path.GetExtension(file.LocalPath).ToLowerInvariant() is ".html" or ".htm" && File.Exists(file.LocalPath))
                targets.Add(file.AbsoluteUri);
        }
        return targets.Distinct(StringComparer.Ordinal).ToArray();
    }

    public void Listen(Action<string[]> received)
    {
        if (!IsPrimary || _listener is not null) throw new InvalidOperationException("Only the primary browser can listen once.");
        _listener = ListenAsync(received);
    }

    private async Task ListenAsync(Action<string[]> received)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var header = new byte[4];
                await server.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                var size = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (size is < 2 or > 65536) continue;
                var payload = new byte[size];
                await server.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
                var args = JsonSerializer.Deserialize<string[]>(payload);
                if (args is not null) received(NavigationTargets(args));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException)
            { Log.Write("Browser activation pipe: " + ex.Message); }
        }
    }

    public async Task SendAsync(string[] urls)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToUtf8Bytes(urls);
        if (payload.Length > 65536) throw new InvalidDataException("Prea multe adrese pentru o singură lansare.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await client.WriteAsync(header, timeout.Token).ConfigureAwait(false);
        await client.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        await client.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
        // The listener disposes its own stream after cancellation; no UI-thread blocking wait.
    }
}
