(() => {
  function openBackground(event) {
    if (!event.isTrusted || event.defaultPrevented || event.shiftKey || event.altKey) return;
    if (!(event.type === 'auxclick' && event.button === 1) &&
        !(event.type === 'click' && event.button === 0 && (event.ctrlKey || event.metaKey))) return;
    const anchor = event.composedPath().find(node => node instanceof HTMLAnchorElement);
    if (!anchor || anchor.hasAttribute('download')) return;
    const url = new URL(anchor.href, document.baseURI);
    if (!['http:', 'https:', 'file:'].includes(url.protocol)) return;
    event.preventDefault();
    window.chrome?.webview?.postMessage({ type: 'vela-background-link', url: url.href });
  }
  document.addEventListener('click', openBackground);
  document.addEventListener('auxclick', openBackground);
})();
