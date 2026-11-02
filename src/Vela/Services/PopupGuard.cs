namespace Vela.Services;

/// <summary>Allows ordinary login popups while putting a small, event-driven cap on popup bursts.</summary>
internal sealed class PopupGuard
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);
    private const int MaximumAutomaticPopups = 2;
    private readonly Queue<DateTime> _automaticPopups = [];

    internal bool Allow(bool userInitiated, DateTime utcNow)
    {
        if (userInitiated) return true;
        while (_automaticPopups.Count > 0 && utcNow - _automaticPopups.Peek() > Window) _automaticPopups.Dequeue();
        if (_automaticPopups.Count >= MaximumAutomaticPopups) return false;
        _automaticPopups.Enqueue(utcNow);
        return true;
    }
}
