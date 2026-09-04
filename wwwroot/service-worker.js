const CACHE_NAME = 'mylog-cache-v12';
const urlsToCache = [
  './',
  './css/site.css',
  './js/main.js',
  './js/utils.js',
  './js/state.js',
  './js/viewer.js',
  './js/browser.js',
  './js/format.js',
  './js/hub.js',
  './js/cluster.js',
  './js/remote.js',
  './js/analytics.js',
  './js/timeFilter.js',
  './js/flowTracker.js',
  './js/bookmarks.js',
  './js/mobileUx.js',
  './js/signalr.min.js',
  './manifest.json',
  './icon-32.png',
  './icon-192.png',
  './icon-512.png'
];

self.addEventListener('install', event => {
  event.waitUntil(
    caches.open(CACHE_NAME)
      .then(cache => {
        return Promise.allSettled(urlsToCache.map(url => cache.add(url)));
      })
  );
  self.skipWaiting();
});

self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys().then(cacheNames => {
      return Promise.all(
        cacheNames.filter(name => name !== CACHE_NAME)
          .map(name => caches.delete(name))
      );
    })
  );
  self.clients.claim();
});

self.addEventListener('fetch', event => {
  if (event.request.method !== 'GET') return;
  
  // For API calls and hubs, bypass service worker cache
  if (event.request.url.includes('/api/') || event.request.url.includes('/hubs/')) {
    event.respondWith(fetch(event.request));
    return;
  }

  // Network-first strategy
  event.respondWith(
    fetch(event.request)
      .then(response => {
        if (response && response.status === 200 && response.type === 'basic') {
          const responseToCache = response.clone();
          caches.open(CACHE_NAME).then(cache => cache.put(event.request, responseToCache));
        }
        return response;
      })
      .catch(() => caches.match(event.request))
  );
});