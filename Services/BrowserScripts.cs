namespace CrimsonBrowser.Services;

internal static class BrowserScripts
{
    public static string ConfigureAdProtection(bool enabled) =>
        "window.__crimsonAdProtectionEnabled=" + (enabled ? "true" : "false") + ";\n" + AdBlock;
    public const string AdBlock = """
            (function () {
                var authHost = (location.hostname || '').toLowerCase();
                if (authHost === 'accounts.google.com' || authHost === 'accounts.youtube.com' ||
                    ((authHost === 'youtube.com' || authHost.endsWith('.youtube.com')) &&
                     location.pathname.toLowerCase().startsWith('/signin'))) return;
                if (window.__crimsonSetAdProtection) {
                    window.__crimsonSetAdProtection(window.__crimsonAdProtectionEnabled !== false);
                    return;
                }
                var enabled = window.__crimsonAdProtectionEnabled !== false;
                window.__crimsonAdShield = true;

                function isYouTube() {
                    var host = (location.hostname || '').toLowerCase();
                    return host === 'youtube.com' ||
                           host.endsWith('.youtube.com') ||
                           host.endsWith('.youtube-nocookie.com');
                }

                // Filter known ad metadata in initial data and player responses. Keep
                // playback/error information intact and preserve page-owned accessors.
                if (isYouTube()) {
                    var adKeys = [
                        'adPlacements',
                        'playerAds',
                        'adSlots',
                        'adBreakHeartbeatParams',
                        'adParams',
                        'playerAdParams',
                        // YouTube's player only stops playback and rewrites the status into
                        // the ad-block enforcement wall when this renderer is present. Removing
                        // it from the payload disarms the client-side enforcement itself.
                        'bkaEnforcementMessageViewModel'
                    ];
                    // Videos whose responses carried the enforcement renderer. The server may
                    // withhold the media at the same time, so these are candidates for the
                    // alternate playback-context recovery even without the enforcement UI.
                    var enforcedVideos = new Set();

                    function stripAds(root) {
                        if (!enabled || !root || typeof root !== 'object') return;
                        var queue = [root];
                        var seen = new WeakSet();
                        var visited = 0;
                        var enforced = false;

                        while (queue.length && visited < 20000) {
                            visited++;
                            var node = queue.pop();
                            if (!node || typeof node !== 'object' || seen.has(node)) continue;
                            seen.add(node);

                            if (!Array.isArray(node)) {
                                for (var i = 0; i < adKeys.length; i++) {
                                    var key = adKeys[i];
                                    if (!(key in node)) continue;

                                    if (key === 'bkaEnforcementMessageViewModel') {
                                        enforced = true;
                                        try { delete node[key]; } catch (e) { }
                                    } else if (key === 'adBreakHeartbeatParams' ||
                                        key === 'adParams' ||
                                        key === 'playerAdParams') {
                                        try { delete node[key]; } catch (e) { }
                                    } else if (Array.isArray(node[key])) {
                                        node[key] = [];
                                    } else {
                                        try { node[key] = undefined; } catch (e) { }
                                    }
                                }
                            }

                            for (var k in node) {
                                if (!Object.prototype.hasOwnProperty.call(node, k)) continue;
                                var value = node[k];
                                if (value && typeof value === 'object') queue.push(value);
                            }
                        }

                        if (enforced) {
                            var enforcedId = root.videoDetails?.videoId ||
                                root.playerResponse?.videoDetails?.videoId;
                            if (!enforcedId && Array.isArray(root)) {
                                for (var r = 0; r < root.length && !enforcedId; r++) {
                                    var item = root[r];
                                    enforcedId = item?.playerResponse?.videoDetails?.videoId ||
                                        item?.videoDetails?.videoId;
                                }
                            }
                            if (enforcedId) enforcedVideos.add(enforcedId);
                        }

                        // YouTube can also return the generic player error screen on its own,
                        // without the enforcement renderer, while still including playable
                        // streams. The player then shows "An error occurred" over a video that
                        // is actually playing. Drop that shell whenever the media is present;
                        // withhold it otherwise so the playback recovery keeps its signal.
                        var playerPayload = root.playabilityStatus ? root : root.playerResponse;
                        var playerStatus = playerPayload?.playabilityStatus;
                        if (playerStatus && hasPlayableStream(playerPayload) &&
                            (enforced || isGenericPlayerError(playerStatus))) {
                            if (playerStatus.status === 'ERROR' || playerStatus.status === 'UNPLAYABLE')
                                playerStatus.status = 'OK';
                            try { delete playerStatus.errorScreen; } catch (e) { }
                            try { delete playerStatus.reason; } catch (e) { }
                        }
                    }

                    // The first player response is assigned to a global instead of being parsed
                    // by JSON.parse, so intercept the assignment as well.
                    function guardInitialData(name) {
                        var current;
                        try {
                            // CEF's context notification can arrive after page scripts. Preserve
                            // already initialized data and any accessors owned by the site.
                            current = window[name];
                            var descriptor = Object.getOwnPropertyDescriptor(window, name);
                            stripAds(current);
                            if (descriptor && (!descriptor.configurable || descriptor.get || descriptor.set)) return;
                            Object.defineProperty(window, name, {
                                configurable: true,
                                enumerable: true,
                                get: function () { return current; },
                                set: function (value) {
                                    try { stripAds(value); } catch (e) { }
                                    current = value;
                                }
                            });
                        } catch (e) { }
                    }

                    ['ytInitialPlayerResponse', 'ytInitialData', 'ytInitialGuideData',
                     'ytInitialReelWatchSequenceResponse', 'ytInitialPlayerResponsePromise']
                        .forEach(guardInitialData);

                    try {
                        var nativeJsonParse = JSON.parse;
                        JSON.parse = function (text, reviver) {
                            var result = nativeJsonParse(text, reviver);
                            if (enabled && typeof text === 'string' &&
                                (text.indexOf('adPlacements') !== -1 ||
                                 text.indexOf('playerAds') !== -1 ||
                                 text.indexOf('adSlots') !== -1 ||
                                 text.indexOf('adBreakHeartbeatParams') !== -1 ||
                                 text.indexOf('bkaEnforcementMessageViewModel') !== -1 ||
                                 text.indexOf('playerErrorMessageRenderer') !== -1)) {
                                try { stripAds(result); } catch (e) { }
                            }
                            return result;
                        };
                    } catch (e) { }

                    // Use YouTube's WEB channel playback context only after its specific
                    // ad-block enforcement response. Ordinary playback, login, live chat,
                    // comments and other origins keep their original request bodies.
                    var playbackRecoveries = new Map();
                    var playbackModes = ['channel', 'lactmilli', 'yahi', 'inline'];
                    var nativeParse = JSON.parse;
                    var nativeStringify = JSON.stringify;
                    function preparePlayerRequest(request) {
                        if (!enabled || !request || typeof request !== 'object' ||
                            request.context?.client?.clientName !== 'WEB' ||
                            !request.playbackContext?.contentPlaybackContext || location.pathname.startsWith('/shorts/')) return request;
                        var recovery = playbackRecoveries.get(request.videoId);
                        if (!recovery || recovery.attempt < 0) return request;
                        var mode = playbackModes[recovery.attempt];
                        var client = Object.assign({}, request.context.client);
                        var playback = Object.assign({}, request.playbackContext.contentPlaybackContext);
                        var result = Object.assign({}, request, {
                            context: Object.assign({}, request.context, {client:client}),
                            playbackContext: Object.assign({}, request.playbackContext, {contentPlaybackContext:playback})
                        });
                        if (mode === 'channel') client.clientScreen = 'CHANNEL';
                        else if (mode === 'lactmilli') result.params = '8AUB';
                        else if (mode === 'yahi') result.params = 'YAHI';
                        else if (mode === 'inline') playback.isInlinePlaybackNoAd = true;
                        if (typeof client.userAgent === 'string') {
                            client.userAgent = client.userAgent.replace(/; (?:channel|lactmilli|yahi|inline)\b/g, '')
                                .replace(/(Mozilla\/5\.0 \([^)]+)/, '$1; ' + mode);
                        }
                        if (mode !== 'yahi') playback.lactMilliseconds = String(Date.now());
                        playback.referer = (playback.referer || location.href).replace(/(?:#reloadxhr)?$/, '#reloadxhr');
                        return result;
                    }
                    // Some YouTube transports capture fetch/XHR before CEF's context
                    // notification. Adapt serialization too, only after a proven enforcement
                    // response and only for that video's WEB playback request.
                    function recoveryStringify(value, replacer, space) {
                        return nativeStringify.call(this, preparePlayerRequest(value), replacer, space);
                    }
                    function installRecoverySerialization() {
                        if (JSON.stringify === nativeStringify) JSON.stringify = recoveryStringify;
                    }
                    function playerEndpoint(value) {
                        try {
                            var url = new URL(value, location.href);
                            return url.origin === location.origin && url.pathname === '/youtubei/v1/player';
                        } catch (e) { return false; }
                    }
                    function playerBody(body) {
                        if (!enabled || playbackRecoveries.size === 0 || typeof body !== 'string') return body;
                        try {
                            var request = nativeParse(body);
                            var modified = preparePlayerRequest(request);
                            return modified === request ? body : nativeStringify(modified);
                        } catch (e) { return body; }
                    }
                    // Response.json does not call the page's JSON.parse override.
                    // Keep the native Response itself (headers, URL, streaming and cloning).
                    function playerResponse(response) {
                        var json = response.json;
                        var clone = response.clone;
                        response.json = function() {
                            return json.call(this).then(function(value) { stripAds(value); return value; });
                        };
                        response.clone = function() { return playerResponse(clone.call(this)); };
                        return response;
                    }
                    var nativeFetch = window.fetch;
                    if (nativeFetch) window.fetch = async function(input, init) {
                        var endpoint = playerEndpoint(typeof input === 'string' || input instanceof URL ? input : input?.url);
                        if (endpoint && init && typeof init.body === 'string') {
                            var body = playerBody(init.body);
                            if (body !== init.body) init = Object.assign({}, init, {body:body});
                        } else if (enabled && endpoint && playbackRecoveries.size > 0 && input instanceof Request &&
                            input.method === 'POST' && !(init && init.body != null)) {
                            var original = await input.clone().text();
                            var modified = playerBody(original);
                            if (modified !== original) input = new Request(input, {body:modified});
                        }
                        var response = await nativeFetch.call(this, input, init);
                        return enabled && endpoint ? playerResponse(response) : response;
                    };
                    var playerRequests = new WeakSet();
                    var nativeOpen = XMLHttpRequest.prototype.open;
                    var nativeSend = XMLHttpRequest.prototype.send;
                    XMLHttpRequest.prototype.open = function(method, url) {
                        playerRequests.delete(this);
                        if (String(method).toUpperCase() === 'POST' && playerEndpoint(url)) playerRequests.add(this);
                        return nativeOpen.apply(this, arguments);
                    };
                    XMLHttpRequest.prototype.send = function(body) {
                        return nativeSend.call(this, playerRequests.has(this) ? playerBody(body) : body);
                    };
                }

                var cosmeticCss = [
                    'ins.adsbygoogle',
                    '.adsbygoogle',
                    '[id^="google_ads"]',
                    '[id^="div-gpt-ad"]',
                    '[class*="banner-ad"]',
                    '[class*="ad-container"]',
                    '[class*="ad-slot"]',
                    '[class*="ad-banner"]',
                    '[class*="sponsored"]',
                    '.ad-slot',
                    '.ad-banner',
                    '#player-ads',
                    '#masthead-ad',
                    'ytd-ad-slot-renderer',
                    'ytd-in-feed-ad-layout-renderer',
                    'ytd-display-ad-renderer',
                    'ytd-promoted-sparkles-web-renderer',
                    'ytd-action-companion-ad-renderer',
                    'ytd-companion-slot-renderer',
                    'ytd-player-legacy-desktop-watch-ads-renderer',
                    '.ytp-ad-module',
                    '.ytp-ad-overlay-container',
                    '.ytp-ad-overlay-slot',
                    '.ytp-ad-player-overlay',
                    '.ytp-ad-player-overlay-layout',
                    '.ytp-ad-image-overlay',
                    '.ytp-ad-text-overlay',
                    '.ytp-ad-preview-container',
                    '.ytp-ad-badge',
                    '.ytp-ad-progress-list',
                    '.ytp-ad-survey',
                    '.video-ads'
                ].join(',') + '{ display: none !important; }';

                function injectCss() {
                    var parent = document.head || document.documentElement;
                    if (!parent) return false;
                    if (document.getElementById('crimson-ad-blocker-css')) return true;
                    var style = document.createElement('style');
                    style.id = 'crimson-ad-blocker-css';
                    style.textContent = cosmeticCss;
                    style.disabled = !enabled;
                    parent.appendChild(style);
                    return true;
                }

                var clickedButtons = new WeakSet();
                function clickFirst(selectors) {
                    for (var i = 0; i < selectors.length; i++) {
                        var button = document.querySelector(selectors[i]);
                        if (button && !button.disabled && !clickedButtons.has(button)) {
                            clickedButtons.add(button);
                            try { button.click(); return true; } catch (e) { }
                        }
                    }
                    return false;
                }

                function player() {
                    return document.getElementById('movie_player');
                }

                function adShowing() {
                    var current = player();
                    if (current && current.classList.contains('ad-showing')) return true;
                    return !!document.querySelector('.html5-video-player.ad-showing');
                }

                function hasPlayableStream(response) {
                    var streaming = response?.streamingData;
                    return !!(streaming && (
                        streaming.formats?.length ||
                        streaming.adaptiveFormats?.length ||
                        streaming.serverAbrStreamingUrl ||
                        streaming.dashManifestUrl ||
                        streaming.hlsManifestUrl));
                }

                // A plain playerErrorMessageRenderer is the "An error occurred" shell that
                // YouTube may attach even when the media is still playable. CAPTCHA screens use
                // a different renderer and must stay visible.
                function isGenericPlayerError(status) {
                    var screen = status && status.errorScreen;
                    if (!screen || !screen.playerErrorMessageRenderer) return false;
                    try { return JSON.stringify(screen).indexOf('playerCaptchaViewModel') === -1; }
                    catch (e) { return true; }
                }

                function recoverBlockedPlayback() {
                    var current = player();
                    if (!enabled || !current || typeof current.getPlayerResponse !== 'function' ||
                        typeof current.loadVideoById !== 'function') return;
                    var response = current.getPlayerResponse();
                    var status = response?.playabilityStatus;
                    var id = response?.videoDetails?.videoId || new URL(location.href).searchParams.get('v');
                    if (!id || !status || location.pathname.startsWith('/shorts/')) return;
                    var recovery = playbackRecoveries.get(id);
                    var error = status.errorScreen ? nativeStringify(status.errorScreen) : '';
                    if (error.includes('playerCaptchaViewModel')) return;
                    var video = current.querySelector('video');
                    // The server can refuse playback while the only enforcement marker is the
                    // renderer we already stripped. In that case the player shows a generic
                    // error, a missing stream or a media-element failure, so recover from those
                    // signals instead of waiting for the enforcement screen.
                    var enforcedFailure = enforcedVideos.has(id) && (
                        status.status === 'ERROR' || status.status === 'UNPLAYABLE' ||
                        !hasPlayableStream(response) || !!(video && video.error));
                    // Search the serialized error screen instead of one fixed nesting level
                    // so the check survives YouTube moving the command into
                    // primaryButton.buttonRenderer.command or a parallel command list.
                    // WEB_PAGE_TYPE_UNKNOWN with the playback-help link is the older
                    // enforcement signature; a plain 3037019 link alone stays untouched.
                    var adBlockMessage = enforcedFailure ||
                        error.includes('openAdAllowlistInstructionCommand') ||
                        (status.status === 'ERROR' && error.includes('WEB_PAGE_TYPE_UNKNOWN') &&
                         error.includes('https://support.google.com/youtube/answer/3037019'));
                    // 3037019 is generic playback help, not an ad-block identifier. Only
                    // continue that response when this video already had a verified ad-block
                    // enforcement command. Account/age/private-video errors remain visible.
                    var compatibilityFailure = recovery && status.status === 'UNPLAYABLE' &&
                        error.includes('https://support.google.com/youtube/answer/3037019');
                    if (!adBlockMessage && !compatibilityFailure) return;
                    if (!recovery) {
                        // Retain no response payloads; a long SPA session stays bounded.
                        if (playbackRecoveries.size >= 64) playbackRecoveries.delete(playbackRecoveries.keys().next().value);
                        recovery = {attempt:-1, seen:new WeakSet()};
                        playbackRecoveries.set(id, recovery);
                    }
                    if (recovery.seen.has(response) || recovery.attempt + 1 >= playbackModes.length) return;
                    recovery.seen.add(response);
                    recovery.attempt++;
                    installRecoverySerialization();
                    var start = response.playerConfig?.playbackStartConfig?.startSeconds || 0;
                    current.loadVideoById(id, start);
                }
                // When the account-level enforcement response arrives first, YouTube builds its
                // watch-page error shell (poster background plus "An error occurred" overlay).
                // Our playback recovery can then load the same video successfully, but that
                // stale shell stays on top of the now-playing media. Remove it only while the
                // player reports playable streams and the video is actually advancing.
                function clearStaleErrorShell() {
                    if (!enabled || !isYouTube()) return;
                    var current = player();
                    var video = current && current.querySelector('video');
                    if (!video || video.paused || video.error || video.readyState < 2 || video.videoWidth <= 0) return;
                    var response = null;
                    try {
                        response = typeof current.getPlayerResponse === 'function' ? current.getPlayerResponse() : null;
                    } catch (e) { }
                    if (!response || !hasPlayableStream(response)) return;
                    var renderer = document.querySelector('yt-player-error-message-renderer');
                    if (!renderer) return;
                    var shell = renderer.closest('yt-playability-error-supported-renderers') || renderer.parentElement;
                    if (shell && shell.style.display !== 'none') shell.style.display = 'none';
                    if (renderer.style.display !== 'none') renderer.style.display = 'none';
                    document.querySelectorAll('.player-container-background').forEach(function (background) {
                        if (background.style.display !== 'none') background.style.display = 'none';
                    });
                }

                var wasShowingAd = false;
                function skipAd() {
                    if (!enabled || !isYouTube()) return;
                    recoverBlockedPlayback();
                    clearStaleErrorShell();
                    if (!adShowing()) {
                        if (wasShowingAd) clickedButtons = new WeakSet();
                        wasShowingAd = false;
                        return;
                    }
                    wasShowingAd = true;
                    // Use available player controls; do not falsify media time, volume or
                    // playback rate. Those changes also affect server-side playback checks.
                    clickFirst([
                        '.ytp-ad-skip-button',
                        '.ytp-ad-skip-button-modern',
                        '.ytp-skip-ad-button',
                        '.ytp-ad-skip-button-container button',
                        'button.ytp-ad-skip-button',
                        '.ytp-ad-overlay-close-button'
                    ]);
                }

                var scheduled = false;
                function kick() {
                    if (!enabled || scheduled) return;
                    scheduled = true;
                    queueMicrotask(function() { scheduled = false; skipAd(); });
                }

                // The player class flips between content and ad mode; react to the flip at once.
                var observedPlayer = null;
                var playerObserver = new MutationObserver(kick);
                function observePlayer() {
                    if (!enabled || !isYouTube()) return;
                    var current = player();
                    if (current === observedPlayer) return;
                    playerObserver.disconnect();
                    observedPlayer = current;
                    if (current) {
                        playerObserver.observe(current, {
                            childList: true,
                            subtree: true,
                            attributes: true,
                            attributeFilter: ['class']
                        });
                        kick();
                    }
                }

                // React to player transitions; no interval or frame-by-frame polling.
                ['loadedmetadata', 'play', 'error'].forEach(function (name) {
                    document.addEventListener(name, function (event) {
                        var target = event.target;
                        if (target && target.tagName === 'VIDEO') {
                            if (name === 'loadedmetadata') clickedButtons = new WeakSet();
                            kick();
                        }
                    }, true);
                });

                document.addEventListener('yt-navigate-finish', function () {
                    observePlayer();
                    kick();
                }, true);
                document.addEventListener('yt-page-data-updated', function () {
                    observePlayer();
                    kick();
                }, true);

                var documentObserver = new MutationObserver(function() {
                    if (!observedPlayer || !observedPlayer.isConnected) observePlayer();
                });
                window.__crimsonSetAdProtection = function(value) {
                    enabled = !!value;
                    window.__crimsonAdProtectionEnabled = enabled;
                    injectCss();
                    var style = document.getElementById('crimson-ad-blocker-css');
                    if (style) style.disabled = !enabled;
                    if (isYouTube()) {
                        if (!enabled && JSON.stringify === recoveryStringify) JSON.stringify = nativeStringify;
                        else if (enabled && playbackRecoveries.size > 0) installRecoverySerialization();
                    }
                    playerObserver.disconnect();
                    documentObserver.disconnect();
                    observedPlayer = null;
                    if (!enabled || !isYouTube()) return;
                    documentObserver.observe(document, {childList:true, subtree:true});
                    observePlayer();
                    kick();
                };
                document.addEventListener('DOMContentLoaded', function() {
                    window.__crimsonSetAdProtection(enabled);
                }, {once:true});
                window.__crimsonSetAdProtection(enabled);
            })();
            """;
}
