// Minimal service worker: gör att appen öppnas även utan nät (då syns historiken).
const CACHE = 'minichat-v2';
const FILES = ['./', './index.html', './manifest.json', './icon-180.png', './icon-512.png', './sounds/ding.wav', './sounds/knock.wav', './sounds/prutt.wav', './sounds/skratt.wav'];
self.addEventListener('install', e => {
  e.waitUntil(caches.open(CACHE).then(c => Promise.all(FILES.map(f => c.add(f).catch(() => null)))).then(() => self.skipWaiting()));
});
self.addEventListener('activate', e => {
  e.waitUntil(caches.keys().then(ks => Promise.all(ks.filter(k => k !== CACHE).map(k => caches.delete(k)))).then(() => self.clients.claim()));
});
self.addEventListener('fetch', e => {
  const url = new URL(e.request.url);
  if (url.origin !== location.origin) return; // ntfy och annat går alltid direkt
  e.respondWith(fetch(e.request).then(r => { if (r.ok) { const copy = r.clone(); caches.open(CACHE).then(c => c.put(e.request, copy)); } return r; }).catch(() => caches.match(e.request)));
});