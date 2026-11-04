using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace CrimsonBrowser.Services;

/// <summary>
/// Keeps a single Crimson process per user. A second launch forwards its URL (or just an
/// "activate" request) to the running instance over a named pipe and then exits.
/// </summary>
internal sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\CrimsonBrowser.SingleInstance";
    private const string PipeName = "CrimsonBrowser.SingleInstance.Pipe";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public bool IsFirstInstance { get; }

    public event Action<string>? MessageReceived;

    public SingleInstanceService()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        IsFirstInstance = createdNew;

        if (createdNew)
            _ = Task.Run(ServerLoopAsync);
    }

    private async Task ServerLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(server);
                string? message = await reader.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(message))
                    MessageReceived?.Invoke(message);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // A stale client should not kill the listener.
                try { await Task.Delay(200, _cts.Token); } catch { break; }
            }
        }
    }

    public static bool Send(string message, int timeoutMilliseconds = 2000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMilliseconds);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(message);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
        _cts.Dispose();
    }
}
