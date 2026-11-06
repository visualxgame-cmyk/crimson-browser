namespace CrimsonBrowser.Services;

internal static class BrowserScripts
{
    public const string AdBlock = """
            (function () {
                var authHost = (location.hostname || '').toLowerCase();
                if (authHost === 'accounts.google.com' || authHost === 'accounts.youtube.com' ||
                    ((authHost === 'youtube.com' || authHost.endsWith('.youtube.com')) &&
                     location.pathname.toLowerCase().startsWith('/signin'))) return;
                if (window.__crimsonAdShield) return;
                window.__crimsonAdShield = true;

                function isYouTube() {
                    var host = (location.hostname || '').toLowerCase();
                    return host === 'youtube.com' ||
                           host.endsWith('.youtube.com') ||
                           host.endsWith('.youtube-nocookie.com');
                }

                // YouTube embeds its ad schedule in the initial page data and in youtubei/v1/player
                // responses. Removing that metadata before the player reads it prevents any ad
                // break from being scheduled at all, so content starts immediately. The CSS hiding
                // below stays as a fallback for ads that slip through in the future.
                if (isYouTube()) {
                    var adKeys = [
                        'adPlacements',
                        'playerAds',
                        'adSlots',
                        'adBreakHeartbeatParams',
                        'adParams',
                        'playerAdParams'
                    ];

                    function stripAds(root) {
                        if (!root || typeof root !== 'object') return;
                        var queue = [root];
                        var seen = new WeakSet();
                        var visited = 0;

                        while (queue.length && visited < 20000) {
                            visited++;
                            var node = queue.pop();
                            if (!node || typeof node !== 'object' || seen.has(node)) continue;
                            seen.add(node);

                            if (!Array.isArray(node)) {
                                for (var i = 0; i < adKeys.length; i++) {
                                    var key = adKeys[i];
                                    if (!(key in node)) continue;

                                    if (key === 'adBreakHeartbeatParams' ||
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
                            if (typeof text === 'string' &&
                                (text.indexOf('adPlacements') !== -1 ||
                                 text.indexOf('playerAds') !== -1 ||
                                 text.indexOf('adSlots') !== -1 ||
                                 text.indexOf('adBreakHeartbeatParams') !== -1)) {
                                try { stripAds(result); } catch (e) { }
                            }
                            return result;
                        };
                    } catch (e) { }
                }

                // Ads share the same <video> element as real content, so hiding it only while the
                // player carries the ad-showing class keeps the ad from ever being painted while
                // letting the content frame reappear instantly when the class is removed.
                var videoHideCss =
                    '#movie_player.ad-showing video,' +
                    '.html5-video-player.ad-showing video.html5-main-video,' +
                    '#movie_player.ad-showing .video-ads' +
                    '{ visibility: hidden !important; opacity: 0 !important; }';

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
                    style.textContent = videoHideCss + cosmeticCss;
                    parent.appendChild(style);
                    return true;
                }

                function clickFirst(selectors) {
                    for (var i = 0; i < selectors.length; i++) {
                        var button = document.querySelector(selectors[i]);
                        if (button) {
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

                function videoElement() {
                    return document.querySelector('#movie_player video.html5-main-video') ||
                           document.querySelector('video.html5-main-video') ||
                           document.querySelector('video');
                }

                // Save the user's media state once per ad break so mute/volume/speed survive it.
                function stashMediaState(video) {
                    if (video.__crimsonAdState) return;
                    video.__crimsonAdState = {
                        muted: video.muted,
                        volume: video.volume,
                        rate: video.playbackRate
                    };
                }

                function restoreMediaState(video) {
                    var state = video.__crimsonAdState;
                    if (!state) return;
                    delete video.__crimsonAdState;
                    try { video.muted = state.muted; } catch (e) { }
                    try { video.volume = state.volume; } catch (e) { }
                    try { video.playbackRate = state.rate; } catch (e) { }
                }

                function skipAd() {
                    injectCss();

                    if (!isYouTube()) return;

                    var video = videoElement();
                    var showing = adShowing();

                    if (!showing) {
                        if (video) restoreMediaState(video);
                        return;
                    }

                    if (video) {
                        stashMediaState(video);
                        try { video.muted = true; } catch (e) { }

                        // Seekable ads end immediately. Unskippable ones cannot be seeked past,
                        // but they run muted and invisibly at 16x, so the wait is imperceptible.
                        try { if (video.playbackRate < 16) video.playbackRate = 16; } catch (e) { }
                        try {
                            var duration = video.duration;
                            if (isFinite(duration) && duration > 0) {
                                var target = Math.max(0, duration - 0.1);
                                if (video.currentTime < target) video.currentTime = target;
                            }
                        } catch (e) { }
                    }

                    // Dismiss any skip/close button that YouTube has already rendered.
                    clickFirst([
                        '.ytp-ad-skip-button',
                        '.ytp-ad-skip-button-modern',
                        '.ytp-skip-ad-button',
                        '.ytp-ad-skip-button-container button',
                        'button.ytp-ad-skip-button',
                        '.ytp-ad-survey-answer-button',
                        '.ytp-ad-overlay-close-button'
                    ]);
                }

                var rafId = 0;
                function watchAdFrames() {
                    rafId = 0;
                    skipAd();
                    if (adShowing()) rafId = window.requestAnimationFrame(watchAdFrames);
                }

                function kick() {
                    skipAd();
                    if (!rafId && adShowing()) rafId = window.requestAnimationFrame(watchAdFrames);
                }

                // The player class flips between content and ad mode; react to the flip at once.
                var observedPlayer = null;
                function observePlayer() {
                    var current = player();
                    if (!current || current === observedPlayer) return;
                    observedPlayer = current;
                    try {
                        new MutationObserver(kick).observe(current, {
                            attributes: true,
                            attributeFilter: ['class']
                        });
                    } catch (e) { }
                }

                // Media events fire before a new ad frame is painted, so handle them synchronously.
                ['loadstart', 'loadedmetadata', 'durationchange', 'play', 'playing',
                 'timeupdate', 'ratechange', 'volumechange', 'emptied'].forEach(function (name) {
                    document.addEventListener(name, function (event) {
                        var target = event.target;
                        if (target && target.tagName === 'VIDEO') kick();
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

                injectCss();
                observePlayer();
                kick();

                try {
                    new MutationObserver(function () {
                        injectCss();
                        observePlayer();
                    }).observe(document.documentElement, { childList: true, subtree: true });
                } catch (e) { }

                // Safety net for pages that create or replace the player after load.
                window.setInterval(function () {
                    injectCss();
                    observePlayer();
                    kick();
                }, 250);
            })();
            """;
}
