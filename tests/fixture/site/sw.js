let state = null;
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));
self.addEventListener('message', e => {
  if (e.data && e.data.set !== undefined) state = e.data.set;
  if (e.data && e.data.get && e.ports[0]) e.ports[0].postMessage(state);
});
// Worker-originated request so the network observer can see worker traffic and its User-Agent.
self.addEventListener('activate', () => fetch('/worker-ping', { cache: 'no-store' }).catch(() => {}));
