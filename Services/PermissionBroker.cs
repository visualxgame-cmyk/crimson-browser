using System.Windows;
using CrimsonBrowser.Models;
using Microsoft.Web.WebView2.Core;

namespace CrimsonBrowser.Services;

/// <summary>
/// Turns WebView2 permission requests into Crimson-themed prompts instead of the
/// stock Chromium bubble, remembers saved profile decisions and serialises prompts
/// so only one dialog is shown at a time.
/// </summary>
internal sealed class PermissionBroker
{
    private sealed class Pending
    {
        public required BrowserTab Tab { get; init; }
        public required CoreWebView2PermissionRequestedEventArgs Args { get; init; }
        public required CoreWebView2Deferral Deferral { get; init; }
        public required string Origin { get; init; }
        public required CoreWebView2PermissionKind Kind { get; init; }
    }

    private readonly Window _owner;
    private readonly bool _persistChoices;
    private readonly Queue<Pending> _queue = new();
    private readonly Dictionary<(string Origin, CoreWebView2PermissionKind Kind), CoreWebView2PermissionState> _sessionChoices = new();
    private bool _pumping;
    private bool _closed;

    public PermissionBroker(Window owner, bool persistChoices)
    {
        _owner = owner;
        _persistChoices = persistChoices;
        owner.Closed += (_, _) =>
        {
            _closed = true;
            while (_queue.Count > 0)
                Complete(_queue.Dequeue(), CoreWebView2PermissionState.Deny, save: false);
        };
    }

    public void Handle(BrowserTab tab, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.Handled = true;

        CoreWebView2Deferral? deferral = null;
        try { deferral = args.GetDeferral(); }
        catch { }

        if (deferral is null)
        {
            try { args.State = CoreWebView2PermissionState.Deny; } catch { }
            return;
        }

        string? origin = Origin(args.Uri);
        if (origin is null || _closed)
        {
            Complete(new Pending
            {
                Tab      = tab,
                Args     = args,
                Deferral = deferral,
                Origin   = origin ?? string.Empty,
                Kind     = args.PermissionKind
            }, CoreWebView2PermissionState.Deny, save: false);
            return;
        }

        _queue.Enqueue(new Pending
        {
            Tab      = tab,
            Args     = args,
            Deferral = deferral,
            Origin   = origin,
            Kind     = args.PermissionKind
        });

        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        if (_pumping) return;
        _pumping = true;

        try
        {
            while (_queue.Count > 0)
            {
                Pending pending = _queue.Dequeue();

                if (_closed)
                {
                    Complete(pending, CoreWebView2PermissionState.Deny, save: false);
                    continue;
                }

                if (_sessionChoices.TryGetValue((pending.Origin, pending.Kind), out var sessionChoice))
                {
                    Complete(pending, sessionChoice, save: false);
                    continue;
                }

                CoreWebView2PermissionState saved = CoreWebView2PermissionState.Default;
                if (_persistChoices && pending.Tab.WebView.CoreWebView2 is { } core)
                    saved = await LookupSavedAsync(core, pending.Origin, pending.Kind);

                if (saved != CoreWebView2PermissionState.Default)
                {
                    Complete(pending, saved, save: false);
                    continue;
                }

                PermissionDecision decision = CrimsonPermissionDialog.Show(
                    _owner, pending.Origin, Describe(pending.Kind), _persistChoices);

                CoreWebView2PermissionState state = decision.Allowed
                    ? CoreWebView2PermissionState.Allow
                    : CoreWebView2PermissionState.Deny;

                if (decision.Remember)
                    _sessionChoices[(pending.Origin, pending.Kind)] = state;

                Complete(pending, state, save: decision.Remember && _persistChoices);
            }
        }
        catch
        {
            while (_queue.Count > 0)
                Complete(_queue.Dequeue(), CoreWebView2PermissionState.Deny, save: false);
        }
        finally
        {
            _pumping = false;
        }
    }

    private static async Task<CoreWebView2PermissionState> LookupSavedAsync(
        CoreWebView2 core, string origin, CoreWebView2PermissionKind kind)
    {
        try
        {
            var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync();
            foreach (var setting in settings)
            {
                if (setting.PermissionKind == kind &&
                    string.Equals(Origin(setting.PermissionOrigin), origin, StringComparison.OrdinalIgnoreCase))
                {
                    return setting.PermissionState;
                }
            }
        }
        catch { }

        return CoreWebView2PermissionState.Default;
    }

    private static void Complete(Pending pending, CoreWebView2PermissionState state, bool save)
    {
        try { pending.Args.SavesInProfile = save; } catch { }
        try { pending.Args.State = state; } catch { }
        try { pending.Deferral.Dispose(); } catch { }
    }

    private static string? Origin(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string Describe(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Microphone                  => LocalizationService.Instance.Get("PermissionMicrophone"),
        CoreWebView2PermissionKind.Camera                      => LocalizationService.Instance.Get("PermissionCamera"),
        CoreWebView2PermissionKind.Geolocation                 => LocalizationService.Instance.Get("PermissionLocation"),
        CoreWebView2PermissionKind.Notifications               => LocalizationService.Instance.Get("PermissionNotifications"),
        CoreWebView2PermissionKind.ClipboardRead               => LocalizationService.Instance.Get("PermissionClipboard"),
        CoreWebView2PermissionKind.OtherSensors                => LocalizationService.Instance.Get("PermissionSensors"),
        CoreWebView2PermissionKind.MultipleAutomaticDownloads  => LocalizationService.Instance.Get("PermissionDownloads"),
        CoreWebView2PermissionKind.FileReadWrite               => LocalizationService.Instance.Get("PermissionFiles"),
        CoreWebView2PermissionKind.Autoplay                    => LocalizationService.Instance.Get("PermissionAutoplay"),
        CoreWebView2PermissionKind.LocalFonts                  => LocalizationService.Instance.Get("PermissionFonts"),
        CoreWebView2PermissionKind.MidiSystemExclusiveMessages => LocalizationService.Instance.Get("PermissionMidi"),
        CoreWebView2PermissionKind.WindowManagement            => LocalizationService.Instance.Get("PermissionWindow"),
        CoreWebView2PermissionKind.PersistentStorage           => LocalizationService.Instance.Get("PermissionStorage"),
        _                                                      => LocalizationService.Instance.Get("PermissionGeneric")
    };
}
