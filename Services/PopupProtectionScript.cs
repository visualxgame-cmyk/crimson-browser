namespace CrimsonBrowser.Services;

internal static class PopupProtectionScript
{
    public const string Source = """
        (() => {
            if (window.__crimsonSetPopupProtection) return;
            let enabled = false;
            let trustedLink = null;
            const nativeOpen = window.open;
            const authTarget = url => url.protocol === 'https:' &&
                (['accounts.google.com', 'accounts.youtube.com', 'login.microsoftonline.com', 'login.live.com'].includes(url.hostname) ||
                 ((url.hostname === 'youtube.com' || url.hostname.endsWith('.youtube.com')) && url.pathname.toLowerCase().startsWith('/signin')));
            const blocked = () => { try { CefSharp.PostMessage('crimson-popup-blocked'); } catch (_) {} };
            function overlayLink(link, event, target) {
                if (target.origin === location.origin) return false;
                if (link.querySelector('video,audio')) return true;
                if (!event || !Number.isFinite(event.clientX) || !Number.isFinite(event.clientY)) return false;
                // Inspect only the clicked point. No polling or document-wide media scan.
                return document.elementsFromPoint(event.clientX, event.clientY).some(element =>
                    element.tagName === 'VIDEO' || element.tagName === 'AUDIO' ||
                    (element.tagName === 'IFRAME' && (element.hasAttribute('allowfullscreen') ||
                        /(?:autoplay|fullscreen)/i.test(element.getAttribute('allow') || ''))));
            }
            window.open = function(url, target, features) {
                if (!enabled) return nativeOpen.apply(this, arguments);
                let destination;
                try { destination = new URL(url || 'about:blank', location.href); } catch (_) { blocked(); return null; }
                const event = window.event;
                const element = event && event.target;
                const link = element && element.closest && element.closest('a[href],area[href]');
                const active = navigator.userActivation && navigator.userActivation.isActive;
                const realLink = link && active && new URL(link.href, location.href).href === destination.href &&
                    !overlayLink(link, event, destination);
                if (realLink || (active && authTarget(destination))) return nativeOpen.apply(this, arguments);
                blocked();
                return null;
            };
            function protectPlayerClick(event) {
                if (!enabled || !event.isTrusted) return;
                trustedLink = null;
                const element = event.target;
                const link = element && element.closest && element.closest('a[href],area[href]');
                if (!link) return;
                let target;
                try { target = new URL(link.href, location.href); } catch (_) { return; }
                if (!authTarget(target) && overlayLink(link, event, target)) {
                    // Cancel the advertising link, leaving the player's own click handlers intact.
                    event.preventDefault();
                    blocked();
                }
                else trustedLink = target.href;
            }
            document.addEventListener('click', protectPlayerClick, true);
            document.addEventListener('auxclick', protectPlayerClick, true);
            window.__crimsonSetPopupProtection = value => { enabled = !!value; trustedLink = null; };
            window.__crimsonAllowPopup = url => {
                // Chromium may consume user activation before the native verification runs.
                // CEF checks activation; this one-shot token checks the clicked destination.
                const allowed = trustedLink === url;
                trustedLink = null;
                return !!allowed;
            };
        })();
        """;
}
