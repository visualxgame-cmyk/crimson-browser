// Only booleans leave the page. Field values, passwords and editor contents stay in the renderer.
(() => {
  if (location.hostname === 'vela.internal') return;
  const authenticationDocument = velaSignInHosts.includes(location.hostname);
  const baseline = new WeakMap();
  const edited = new Set();
  const token = globalThis.crypto?.randomUUID?.() || Math.random().toString(36).slice(2) + Date.now();
  let dirty = false;
  let navigationPrompt = false;
  const credentialTokens = new Set(['username', 'current-password', 'new-password', 'one-time-code', 'webauthn']);
  function credentialField(element) {
    return element instanceof HTMLInputElement && (element.type === 'password' ||
      (element.getAttribute('autocomplete') || '').toLowerCase().split(/\s+/).some(part => credentialTokens.has(part)));
  }
  // SPA authentication steps remove their old fields without submitting a new document.
  // Observe removals only while edits exist; idle pages have no DOM observer work.
  const removedFields = new MutationObserver(() => {
    for (const element of edited) if (!element.isConnected) { edited.delete(element); baseline.delete(element); }
    publish();
  });
  function editable(target) {
    if (!(target instanceof Element)) return null;
    const element = target.closest('input,textarea,select,[contenteditable]');
    if (!element || element.disabled || element.readOnly) return null;
    if (element instanceof HTMLInputElement && ['button', 'submit', 'reset', 'image', 'hidden'].includes(element.type)) return null;
    if (element.hasAttribute('contenteditable') && !element.isContentEditable) return null;
    return element;
  }
  function value(element) {
    if (element instanceof HTMLInputElement) {
      if (['checkbox', 'radio'].includes(element.type)) return element.checked;
      if (element.type === 'file') return element.files.length;
      return element.value;
    }
    if (element instanceof HTMLSelectElement) return Array.from(element.selectedOptions, o => o.index).join(',');
    // Rich editors can contain a large DOM; marking an edit needs no content snapshot.
    return element instanceof HTMLTextAreaElement ? element.value : true;
  }
  function initial(element) {
    if (element instanceof HTMLInputElement) {
      if (['checkbox', 'radio'].includes(element.type)) return element.defaultChecked;
      if (element.type === 'file') return 0;
      return element.defaultValue;
    }
    if (element instanceof HTMLTextAreaElement) return element.defaultValue;
    if (element instanceof HTMLSelectElement) {
      const defaults = Array.from(element.options).filter(o => o.defaultSelected).map(o => o.index);
      return defaults.length ? defaults.join(',') : element.multiple || !element.options.length ? '' : '0';
    }
    return false;
  }
  function beforeUnload(event) {
    if (!navigationPrompt) return;
    event.preventDefault();
    event.returnValue = '';
  }
  function publish() {
    const next = edited.size > 0;
    const prompt = !authenticationDocument && Array.from(edited).some(element => !credentialField(element));
    if (prompt !== navigationPrompt) {
      navigationPrompt = prompt;
      if (prompt) addEventListener('beforeunload', beforeUnload);
      else removeEventListener('beforeunload', beforeUnload);
    }
    if (next === dirty) return;
    dirty = next;
    if (dirty) removedFields.observe(document, { childList: true, subtree: true });
    else removedFields.disconnect();
    chrome.webview.postMessage({ type: 'vela-form-state', token, dirty });
  }
  document.addEventListener('focusin', event => {
    const element = editable(event.target);
    if (element && !baseline.has(element)) baseline.set(element, element.isContentEditable ? false : value(element));
  }, true);
  function changed(event) {
    const element = editable(event.target);
    if (!element) return;
    if (!baseline.has(element)) baseline.set(element, initial(element));
    if (value(element) === baseline.get(element)) edited.delete(element);
    else edited.add(element);
    publish();
  }
  document.addEventListener('input', changed, true);
  document.addEventListener('change', changed, true);
  function clearForm(event) {
    queueMicrotask(() => {
      if (event.defaultPrevented) return;
      for (const element of edited) {
        if (element.form === event.target || event.target.contains(element)) {
          edited.delete(element);
          baseline.delete(element);
        }
      }
      publish();
    });
  }
  document.addEventListener('submit', clearForm, true);
  document.addEventListener('reset', clearForm, true);
  chrome.webview.postMessage({ type: 'vela-form-ready', token, dirty: false });
  const peers = new Set(), tracks = new Set(), playing = new Set();
  let active = false;
  function publishActivity() {
    const next = peers.size > 0 || tracks.size > 0 || playing.size > 0;
    if (next === active) return;
    active = next;
    chrome.webview.postMessage({ type: 'vela-activity-state', token, active });
  }
  // Observe negotiated connections without replacing the RTCPeerConnection constructor.
  if (globalThis.RTCPeerConnection) {
    const observed = new WeakSet();
    function observe(peer) {
      if (observed.has(peer)) return;
      observed.add(peer);
      const update = () => {
        const inUse = !['failed', 'closed'].includes(peer.connectionState) &&
          (['connecting', 'connected', 'disconnected'].includes(peer.connectionState) ||
           peer.iceConnectionState === 'checking' || !['stable', 'closed'].includes(peer.signalingState));
        if (inUse) peers.add(peer); else peers.delete(peer);
        publishActivity();
      };
      for (const event of ['connectionstatechange', 'iceconnectionstatechange', 'signalingstatechange']) peer.addEventListener(event, update);
    }
    const prototype = RTCPeerConnection.prototype;
    for (const name of ['setLocalDescription', 'setRemoteDescription']) {
      const original = prototype[name];
      prototype[name] = function (...args) { observe(this); return Reflect.apply(original, this, args); };
    }
    const close = prototype.close;
    prototype.close = function (...args) {
      const result = Reflect.apply(close, this, args); peers.delete(this); publishActivity(); return result;
    };
  }
  function watchTrack(track) {
    if (track.readyState !== 'live' || tracks.has(track)) return;
    tracks.add(track);
    track.addEventListener('ended', () => { tracks.delete(track); publishActivity(); }, { once: true });
  }
  if (globalThis.MediaStreamTrack) {
    // Calling stop() does not fire ended. Clones can keep the capture device active.
    const prototype = MediaStreamTrack.prototype, stop = prototype.stop, clone = prototype.clone;
    prototype.stop = function (...args) { const result = Reflect.apply(stop, this, args); tracks.delete(this); publishActivity(); return result; };
    prototype.clone = function (...args) { const result = Reflect.apply(clone, this, args); if (tracks.has(this)) { watchTrack(result); publishActivity(); } return result; };
  }
  for (const name of ['getUserMedia', 'getDisplayMedia']) {
    const devices = navigator.mediaDevices, original = devices?.[name];
    if (!original) continue;
    devices[name] = function (...args) {
      return Reflect.apply(original, this, args).then(stream => { stream.getTracks().forEach(watchTrack); publishActivity(); return stream; });
    };
  }
  function mediaChanged(event) {
    const media = event.target;
    if (!(media instanceof HTMLMediaElement)) return;
    if (!media.paused && !media.ended && !media.error && media.readyState > 0) playing.add(media); else playing.delete(media);
    publishActivity();
  }
  for (const event of ['playing', 'pause', 'ended', 'emptied', 'abort', 'error']) document.addEventListener(event, mediaChanged, true);
})();
