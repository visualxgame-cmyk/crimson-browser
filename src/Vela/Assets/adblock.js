/*
 * Vela - filtre cosmetice si eliminarea reclamelor YouTube.
 *
 * Ruleaza inaintea scripturilor paginii, pe fiecare document (vezi
 * AdBlocker.InjectionScript si AddScriptToExecuteOnDocumentCreatedAsync).
 * Nu depinde de extensii. CSS-ul ascunde containerele de reclame ramase dupa
 * blocarea din retea, iar pe YouTube elimina anunturile din raspunsul playerului
 * inainte ca playerul sa le vada (aceeasi abordare ca scriptletele din uBlock).
 */
(() => {
    'use strict';

    // Authentication/consent documents must retain their own DOM and native APIs.
    // Network blocking remains enabled; these pages have no player to filter.
    const host = location.hostname;
    if (velaSignInHosts.includes(host)) return;

    // ---------------------------------------------------------------
    // 1. Ascunde containerele de reclame (toate site-urile)
    // ---------------------------------------------------------------
    const AD_CSS = `
ins.adsbygoogle,
.adsbygoogle,
[data-ad-client],
[data-ad-slot],
[id^="google_ads_"],
[id^="div-gpt-ad"],
iframe[src*="doubleclick.net"],
iframe[src*="googlesyndication.com"],
iframe[src*="googleadservices.com"],
iframe[src*="amazon-adsystem.com"],
iframe[src*="adnxs.com"],
iframe[src*="taboola.com"],
iframe[src*="outbrain.com"] { display: none !important; }

.ad-banner, .ad-billboard, .ad-box, .ad-container, .ad-holder, .ad-placeholder,
.ad-slot, .ad-slot-container, .ad-unit, .ad-wrapper,
.ads-banner, .ads-box, .ads-container, .ads-holder, .ads-slot, .ads-wrapper,
.advert, .adverts, .advertisement, .advertisements, .advertising,
.sponsored-ad, .sponsored-content, .sponsored-links, .sponsored-post,
.promoted-content, .promoted-post,
.outbrain, .OUTBRAIN, .taboola, .trc_related_container,
.adthrive, .adthrive-ad, .adthrive-content, .adthrive-footer,
.ezoic-ad, [class^="ezoic-ad"] { display: none !important; }

#ad, #ads, #advert, #advertisement, #advertising,
#ad-banner, #ad-billboard, #ad-box, #ad-container, #ad-holder, #ad-slot, #ad-unit, #ad-wrapper,
#ads-banner, #ads-box, #ads-container, #ads-slot, #ads-wrapper,
#banner-ad, #banner-ads, #banner_ad, #div-gpt-ad, #google_ads, #google_ads_frame,
#google_ads_iframe, #player-ads, #story-ad,
#taboola-below-article, #taboola-below-article-thumbnails { display: none !important; }

/* YouTube */
#masthead-ad,
ytd-ad-slot-renderer,
ytd-display-ad-renderer,
ytd-promoted-sparkles-web-renderer,
ytd-promoted-video-renderer,
ytd-compact-promoted-video-renderer,
ytd-compact-promoted-item-renderer,
ytd-in-feed-ad-layout-renderer,
ytd-banner-promo-renderer,
ytd-statement-banner-renderer,
ytd-ad-inline-playback-renderer,
ytd-engagement-panel-section-list-renderer[target-id="engagement-panel-ads"],
ytd-rich-item-renderer:has(ytd-ad-slot-renderer),
ytd-rich-section-renderer:has(ytd-statement-banner-renderer) { display: none !important; }

.ytp-ad-overlay-container,
.ytp-ad-overlay-slot,
.ytp-ad-image-overlay,
.ytp-ad-text-overlay,
.ytp-ad-progress-list,
.ytp-ad-module,
.ytp-ad-player-overlay,
.ytp-ad-player-overlay-layout,
.ytp-ad-player-overlay-instream-info { display: none !important; }

ytd-enforcement-message-view-model,
tp-yt-paper-dialog:has(ytd-enforcement-message-view-model) { display: none !important; }
`;

    let applied = false;

    const applyCosmetic = () => {
        if (applied) return;
        if (!document.documentElement) { setTimeout(applyCosmetic, 25); return; }
        applied = true;

        // Constructable stylesheets are not blocked by a page's CSP.
        try {
            const sheet = new CSSStyleSheet();
            sheet.replaceSync(AD_CSS);
            document.adoptedStyleSheets = document.adoptedStyleSheets.concat(sheet);
            return;
        } catch (e) {
            // Fall through to a classic <style> element.
        }

        try {
            const style = document.createElement('style');
            style.textContent = AD_CSS;
            (document.head || document.documentElement).appendChild(style);
        } catch (e) {
            // Nothing else to try.
        }
    };

    applyCosmetic();
    document.addEventListener('DOMContentLoaded', applyCosmetic, { once: true });

    // ---------------------------------------------------------------
    // 2. YouTube: scoate anunturile din datele playerului
    // ---------------------------------------------------------------
    if (!/(^|\.)youtube(-nocookie)?\.com$/.test(host) && host !== 'youtu.be') return;

    const AD_KEYS = new Set([
        'adPlacements',
        'adSlots',
        'playerAds',
        'adBreakHeartbeatParams',
        'adParams',
        'adSignalsInfo',
        'adTimeUpdateMs',
        'adBreakServiceRenderer',
        'adBreakParams'
    ]);

    const prune = (value, depth) => {
        if (!value || typeof value !== 'object' || depth > 20) return value;

        if (Array.isArray(value)) {
            for (let i = 0; i < value.length; i++) prune(value[i], depth + 1);
            return value;
        }

        for (const key of Object.keys(value)) {
            if (AD_KEYS.has(key)) {
                delete value[key];
            } else {
                prune(value[key], depth + 1);
            }
        }

        return value;
    };

    const hasAdData = (text) =>
        typeof text === 'string' &&
        (text.indexOf('adPlacements') !== -1 ||
         text.indexOf('adSlots') !== -1 ||
         text.indexOf('playerAds') !== -1 ||
         text.indexOf('adBreakHeartbeatParams') !== -1);

    // XHR responses and inline JSON parsed by the page.
    const realParse = JSON.parse;
    JSON.parse = function (text, reviver) {
        const result = realParse.call(this, text, reviver);
        try {
            if (!reviver && hasAdData(text)) prune(result, 0);
        } catch (e) {
            // Never break JSON parsing because of the filter.
        }
        return result;
    };

    // fetch responses read with response.json() (YouTube uses this for /youtubei).
    try {
        const realJson = Response.prototype.json;
        Response.prototype.json = function () {
            const url = this.url || '';
            const wanted = url.indexOf('/youtubei/v1/player') !== -1 ||
                           url.indexOf('/youtubei/v1/next') !== -1;
            return realJson.call(this).then((value) => {
                if (wanted) {
                    try { prune(value, 0); } catch (e) { /* ignore */ }
                }
                return value;
            });
        };
    } catch (e) {
        // Response may be missing in some frames.
    }

    // Player response embedded directly in the watch page.
    try {
        let playerResponse;
        Object.defineProperty(window, 'ytInitialPlayerResponse', {
            configurable: true,
            enumerable: true,
            get() { return playerResponse; },
            set(value) {
                try { playerResponse = prune(value, 0); }
                catch (e) { playerResponse = value; }
            }
        });
    } catch (e) {
        // Property already defined by the page: JSON.parse hook still applies.
    }
})();
