using CefSharp;
using CrimsonBrowser.Models;
using CrimsonBrowser.Services;

internal static class YouTubeShieldChecks
{
    public static async Task Run(BrowserTab tab)
    {
        await Evaluate(tab, """
            window.__crimsonSetAdProtection(false);
            delete window.__crimsonSetAdProtection;
            Object.defineProperty(window,'ytInitialData',{configurable:true,writable:true,value:{marker:'keep-existing',adSlots:[1],
                auxiliaryUi:{messageRenderers:{bkaEnforcementMessageViewModel:{title:{content:'wall'}}}}}});
            window.testAccessor=()=>({marker:'keep-accessor'});
            Object.defineProperty(window,'ytInitialPlayerResponse',{configurable:true,get:window.testAccessor});
            window.nativeStringify=JSON.stringify;
            window.intervalCalls=0;
            window.setInterval=()=>{window.intervalCalls++;return 0};
            window.sentRequests=[];
            window.fetch=async function(input,init){
                const body=init?.body ?? (input instanceof Request ? await input.clone().text() : null);
                sentRequests.push({url:typeof input==='string'?input:input.url,body});
                return new Response(window.fixtureResponseBody || '{"adSlots":[1],"auxiliaryUi":{"messageRenderers":{"bkaEnforcementMessageViewModel":{"title":"wall"}}},"errorScreen":{"enforcementMessageViewModel":{"primaryButton":{"onTap":{"innertubeCommand":{"openAdAllowlistInstructionCommand":{}}}}}},"streamingData":{"marker":"keep-stream"}}',{
                    status:200,headers:{'X-Fixture':'keep-header','Content-Type':'application/json'}});
            };
            XMLHttpRequest.prototype.open=function(method,url){this.fixtureUrl=url};
            XMLHttpRequest.prototype.send=function(body){sentRequests.push({url:this.fixtureUrl,body})};
            window.fixtureBody=()=>nativeStringify({videoId:'fixture-video',context:{client:{clientName:'WEB',clientScreen:'WATCH',userAgent:navigator.userAgent}},
                playbackContext:{contentPlaybackContext:{signatureTimestamp:123}},attestationRequest:{marker:'keep-proof'}});
            true;
            """, "prepare isolated YouTube networking fixture");
        await Evaluate(tab, BrowserScripts.ConfigureAdProtection(true) + "\ntrue;", "install updated YouTube filtering");
        await Evaluate(tab, """
            ytInitialData.marker==='keep-existing' && ytInitialData.adSlots.length===0 &&
            ytInitialData.auxiliaryUi.messageRenderers.bkaEnforcementMessageViewModel===undefined &&
            Object.getOwnPropertyDescriptor(window,'ytInitialPlayerResponse').get===window.testAccessor &&
            JSON.stringify===nativeStringify && intervalCalls===0
            """, "preserve existing page data, accessors and JSON serialization without polling timers");

        await EvaluatePromise(tab, """
            return (async()=>{
                const response=await fetch('/youtubei/v1/player',{method:'POST',body:fixtureBody()});
                const copy=response.clone();
                const data=await response.json();
                const cloned=await copy.json();
                return data.adSlots.length===0 && cloned.adSlots.length===0 && data.streamingData.marker==='keep-stream' &&
                    data.auxiliaryUi.messageRenderers.bkaEnforcementMessageViewModel===undefined &&
                    data.errorScreen.enforcementMessageViewModel.primaryButton.onTap.innertubeCommand.openAdAllowlistInstructionCommand &&
                    response.status===200 && response.headers.get('X-Fixture')==='keep-header' &&
                    JSON.parse(sentRequests.at(-1).body).context.client.clientScreen==='WATCH';
            })();
            """, "filter native fetch JSON and clones while preserving response metadata and ordinary playback");
        await Evaluate(tab, """
            window.fixtureResponse={videoDetails:{videoId:'fixture-video'},playerConfig:{playbackStartConfig:{startSeconds:42}},
                playabilityStatus:{status:'ERROR',errorScreen:{enforcementMessageViewModel:{
                    title:{content:'Localized enforcement title'},primaryButton:{onTap:{parallelCommand:{commands:[
                    {innertubeCommand:{openAdAllowlistInstructionCommand:{}}}]}}}}}}};
            window.recoveryCount=0;
            window.fixturePlayer=document.createElement('div');
            fixturePlayer.id='movie_player';
            fixturePlayer.getPlayerResponse=()=>fixtureResponse;
            fixturePlayer.loadVideoById=(id,start)=>{window.recoveryCount++;window.recoveredPosition=start;window.recoveredId=id};
            fixturePlayer.innerHTML='<video></video><button class="ytp-ad-skip-button" onclick="window.skips=(window.skips||0)+1;this.textContent=\'Skipped\'">Skip</button>';
            const video=fixturePlayer.querySelector('video');
            video.playbackRate=1.5;video.volume=0.35;video.muted=false;
            document.body.append(fixturePlayer);
            true;
            """, "create localized ad-block enforcement fixture");
        await EvaluatePromise(tab, """
            return (async()=>{
                document.dispatchEvent(new Event('yt-page-data-updated'));
                await Promise.resolve();
                document.dispatchEvent(new Event('yt-page-data-updated'));
                await Promise.resolve();
                return recoveryCount===1 && recoveredId==='fixture-video' && recoveredPosition===42;
            })();
            """, "recover the specific enforcement error once per video and retain the playback position");
        await EvaluatePromise(tab, """
            return (async()=>{
                await fetch('/youtubei/v1/player',{method:'POST',body:fixtureBody()});
                const recovered=JSON.parse(sentRequests.at(-1).body);
                await fetch(new Request(location.origin+'/youtubei/v1/player',{method:'POST',body:fixtureBody()}));
                const requestInput=JSON.parse(sentRequests.at(-1).body);
                const xhr=new XMLHttpRequest();xhr.open('POST',location.origin+'/youtubei/v1/player');xhr.send(fixtureBody());
                const xhrBody=JSON.parse(sentRequests.at(-1).body);
                await fetch('/youtubei/v1/live_chat/get_live_chat',{method:'POST',body:JSON.stringify({videoId:'fixture-video',
                    context:{client:{clientName:'WEB',clientScreen:'WATCH'}},continuation:'fixture'})});
                const chat=JSON.parse(sentRequests.at(-1).body);
                const other=JSON.parse(fixtureBody());other.videoId='live-video';
                await fetch('/youtubei/v1/player',{method:'POST',body:JSON.stringify(other)});
                const live=JSON.parse(sentRequests.at(-1).body);
                return recovered.context.client.clientScreen==='CHANNEL' && requestInput.context.client.clientScreen==='CHANNEL' &&
                    xhrBody.context.client.clientScreen==='CHANNEL' && recovered.attestationRequest.marker==='keep-proof' &&
                    recovered.playbackContext.contentPlaybackContext.signatureTimestamp===123 &&
                    chat.context.client.clientScreen==='WATCH' && live.context.client.clientScreen==='WATCH';
            })();
            """, "scope channel recovery to the blocked video across fetch, Request and XHR without altering live chat or verification data");
        await EvaluatePromise(tab, """
            return (async()=>{
                const original=JSON.parse(fixtureBody());
                const encoded=JSON.parse(JSON.stringify(original));
                if(encoded.context.client.clientScreen!=='CHANNEL' || original.context.client.clientScreen!=='WATCH' ||
                    encoded.playbackContext.contentPlaybackContext.referer.indexOf('#reloadxhr')<0 ||
                    !encoded.playbackContext.contentPlaybackContext.lactMilliseconds) return false;
                fixtureResponse={videoDetails:{videoId:'fixture-video'},playabilityStatus:{status:'UNPLAYABLE',errorScreen:{
                    playerInterstitialRenderer:{content:{interstitialViewModel:{description:{content:'Generic playback failure'},
                    support:'https://support.google.com/youtube/answer/3037019'}}}}}};
                document.dispatchEvent(new Event('yt-page-data-updated'));await Promise.resolve();
                const lact=JSON.parse(JSON.stringify(original));
                fixtureResponse=structuredClone(fixtureResponse);
                document.dispatchEvent(new Event('yt-page-data-updated'));await Promise.resolve();
                const yahi=JSON.parse(JSON.stringify(original));
                fixtureResponse=structuredClone(fixtureResponse);
                document.dispatchEvent(new Event('yt-page-data-updated'));await Promise.resolve();
                const inline=JSON.parse(JSON.stringify(original));
                fixtureResponse=structuredClone(fixtureResponse);
                document.dispatchEvent(new Event('yt-page-data-updated'));await Promise.resolve();
                return recoveryCount===4 && lact.params==='8AUB' && yahi.params==='YAHI' &&
                    inline.playbackContext.contentPlaybackContext.isInlinePlaybackNoAd===true &&
                    fixtureResponse.playabilityStatus.status==='UNPLAYABLE' && original.context.client.clientScreen==='WATCH';
            })();
            """, "adapt captured serializers and advance bounded playback contexts only on new server responses");
        await EvaluatePromise(tab, """
            return (async()=>{
                window.fixtureResponseBody=JSON.stringify({videoDetails:{videoId:'streamed-enforcement-video'},
                    playabilityStatus:{status:'ERROR',reason:'An error occurred',errorScreen:{playerErrorMessageRenderer:{}}},
                    auxiliaryUi:{messageRenderers:{bkaEnforcementMessageViewModel:{title:{content:'wall'}}}},
                    streamingData:{adaptiveFormats:[{mimeType:'video/webm; codecs="vp9"'}]}});
                const response=await fetch('/youtubei/v1/player',{method:'POST',body:fixtureBody()});
                fixtureResponse=await response.json();
                window.recoveryCount=0;
                document.dispatchEvent(new Event('yt-page-data-updated'));
                await Promise.resolve();
                return fixtureResponse.playabilityStatus.status==='OK' &&
                    fixtureResponse.playabilityStatus.errorScreen===undefined &&
                    fixtureResponse.playabilityStatus.reason===undefined &&
                    fixtureResponse.auxiliaryUi.messageRenderers.bkaEnforcementMessageViewModel===undefined &&
                    fixtureResponse.streamingData.adaptiveFormats.length===1 && recoveryCount===0;
            })();
            """, "clear the enforcement error shell when the response still carries playable streams");
        await EvaluatePromise(tab, """
            return (async()=>{
                window.fixtureResponseBody=JSON.stringify({videoDetails:{videoId:'generic-shell-video'},
                    playabilityStatus:{status:'ERROR',reason:'An error occurred',errorScreen:{playerErrorMessageRenderer:{}}},
                    streamingData:{adaptiveFormats:[{mimeType:'video/webm; codecs="vp9"'}]}});
                const response=await fetch('/youtubei/v1/player',{method:'POST',body:fixtureBody()});
                fixtureResponse=await response.json();
                window.recoveryCount=0;
                document.dispatchEvent(new Event('yt-page-data-updated'));
                await Promise.resolve();
                return fixtureResponse.playabilityStatus.status==='OK' &&
                    fixtureResponse.playabilityStatus.errorScreen===undefined &&
                    fixtureResponse.playabilityStatus.reason===undefined && recoveryCount===0;
            })();
            """, "clear a standalone generic player error shell when the response still carries playable streams");
        await EvaluatePromise(tab, """
            return (async()=>{
                const shell=document.createElement('yt-playability-error-supported-renderers');
                const holder=document.createElement('div');
                const renderer=document.createElement('yt-player-error-message-renderer');
                holder.append(renderer);shell.append(holder);
                const background=document.createElement('div');background.className='player-container-background';
                document.body.append(shell,background);
                const video=fixturePlayer.querySelector('video');
                Object.defineProperty(video,'paused',{configurable:true,value:false});
                Object.defineProperty(video,'readyState',{configurable:true,value:4});
                Object.defineProperty(video,'videoWidth',{configurable:true,value:1280});
                Object.defineProperty(video,'error',{configurable:true,value:null});
                fixtureResponse={videoDetails:{videoId:'playing-shell-video'},playabilityStatus:{status:'OK'},
                    streamingData:{adaptiveFormats:[{mimeType:'video/webm; codecs="vp9"'}]}};
                document.dispatchEvent(new Event('yt-page-data-updated'));
                await Promise.resolve();
                const hidden=shell.style.display==='none' && renderer.style.display==='none' &&
                    background.style.display==='none';
                shell.remove();background.remove();
                for(const name of ['paused','readyState','videoWidth','error']) delete video[name];
                return hidden;
            })();
            """, "remove the stale error shell and poster background once a recovered video is visibly playing");
        await Evaluate(tab, "window.fixtureResponseBody=null;true;", "reset streamed enforcement fixture");
        await EvaluatePromise(tab, """
            return (async()=>{
                window.fixtureResponseBody=JSON.stringify({videoDetails:{videoId:'generic-error-video'},
                    playabilityStatus:{status:'ERROR'},auxiliaryUi:{messageRenderers:{
                    bkaEnforcementMessageViewModel:{title:{content:'wall'}}}}});
                const response=await fetch('/youtubei/v1/player',{method:'POST',body:fixtureBody()});
                fixtureResponse=await response.json();
                window.recoveryCount=0;
                document.dispatchEvent(new Event('yt-page-data-updated'));
                await Promise.resolve();
                return fixtureResponse.auxiliaryUi.messageRenderers.bkaEnforcementMessageViewModel===undefined &&
                    recoveryCount===1 && recoveredId==='generic-error-video';
            })();
            """, "recover a stripped enforcement response even when the player only reports a generic error");
        await Evaluate(tab, "window.fixtureResponseBody=null;true;", "reset fixture response body");
        await EvaluatePromise(tab, """
            return (async()=>{
                const before=recoveryCount;
                fixtureResponse={videoDetails:{videoId:'untouched-video'},playabilityStatus:{status:'UNPLAYABLE',errorScreen:{
                    support:'https://support.google.com/youtube/answer/3037019'}}};
                document.dispatchEvent(new Event('yt-page-data-updated'));await Promise.resolve();
                fixtureResponse.videoDetails.videoId='captcha-video';
                fixtureResponse.playabilityStatus.errorScreen.playerCaptchaViewModel={};
                document.dispatchEvent(new Event('yt-page-data-updated'));await Promise.resolve();
                fixtureResponse={videoDetails:{videoId:'private-video'},playabilityStatus:{status:'UNPLAYABLE',errorScreen:{reason:'Private video'}}};
                document.dispatchEvent(new Event('yt-page-data-updated'));await Promise.resolve();
                fixturePlayer.className='ad-showing';
                document.dispatchEvent(new Event('yt-page-data-updated'));await Promise.resolve();
                const video=fixturePlayer.querySelector('video');
                await Promise.resolve();
                return recoveryCount===before && video.playbackRate===1.5 && Math.abs(video.volume-0.35)<0.001 && !video.muted && skips===1;
            })();
            """, "retain CAPTCHA and account errors and user media settings while using available skip controls");
        await Evaluate(tab, BrowserScripts.ConfigureAdProtection(false) + "\ntrue;", "disable filtering on an already open document");
        await EvaluatePromise(tab, """
            return (async()=>{
                const response=await fetch('/youtubei/v1/player',{method:'POST',body:fixtureBody()});
                const data=await response.json();
                return data.adSlots.length===1 && JSON.parse(sentRequests.at(-1).body).context.client.clientScreen==='WATCH' &&
                    document.getElementById('crimson-ad-blocker-css').disabled && typeof window.__crimsonSetPopupProtection==='function';
            })();
            """, "honor live ad-filter toggles without disabling popup protection");
        await Evaluate(tab, BrowserScripts.ConfigureAdProtection(true) + "\n!document.getElementById('crimson-ad-blocker-css').disabled;",
            "re-enable filtering without reloading the document");
    }

    private static async Task Evaluate(BrowserTab tab, string source, string description)
        => Check(await tab.WebView.EvaluateScriptAsync(source), description);
    private static async Task EvaluatePromise(BrowserTab tab, string source, string description)
        => Check(await tab.WebView.EvaluateScriptAsPromiseAsync(source, TimeSpan.FromSeconds(5)), description);
    private static void Check(JavascriptResponse result, string description)
    {
        if (!result.Success || result.Result is not true)
            throw new InvalidOperationException(description + ": " + (result.Message ?? result.Result?.ToString()));
        Console.WriteLine("PASS: " + description);
    }
}
