// Service worker do shell PWA (SPEC-117 — port do padrão OWUIC).
// Dois caches com estratégias distintas:
// - ohn-fw-* (cache-first): assets de _framework fingerprinted pelo
//   MapStaticAssets (name.{hash}.ext) e o espelho /framework-assets/{stem}/{ext}
//   (SPEC-94) — a URL carrega o hash do build, conteúdo velho nunca é
//   servido para um nome novo. Evicção FIFO limita o crescimento.
// - ohn-shell-* (network-first): documento/navegações e cadeia mutável
//   em URLs estáveis (index.html, js/*, css/*, i18n/*, assets/*, manifest,
//   loaders literais de _framework). Cache é só fallback offline.
// - API / hubs SignalR: nunca cacheia — erros de rede propagam reais.
const CACHE_SHELL = 'ohn-shell-v1';
const CACHE_FW = 'ohn-fw-v1';
const KNOWN_CACHES = new Set([CACHE_SHELL, CACHE_FW]);

// Cap FIFO de entradas no cache de framework: cada deploy adiciona um
// build inteiro de nomes fingerprinted; sem limite o cache cresce sem parar.
const MAX_FW_ENTRIES = 240;

// Aliases literais (não-fingerprinted) de _framework servidos como
// documento — mutáveis entre builds, ficam no caminho network-first.
const MUTABLE_FRAMEWORK_BOOT = new Set([
  '/_framework/blazor.webassembly.js',
  '/_framework/dotnet.js',
  '/_framework/dotnet.boot.js',
]);

self.addEventListener('install', () => self.skipWaiting());

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(keys.filter((k) => !KNOWN_CACHES.has(k)).map((k) => caches.delete(k)))
      )
      .then(() => self.clients.claim())
  );
});

function isImmutableFrameworkAsset(pathname) {
  if (pathname.startsWith('/framework-assets/')) return true;
  return pathname.startsWith('/_framework/') && !MUTABLE_FRAMEWORK_BOOT.has(pathname);
}

async function cacheFirst(request) {
  const cache = await caches.open(CACHE_FW);
  const hit = await cache.match(request);
  if (hit) return hit;
  const response = await fetch(request);
  if (response.ok) {
    const copy = response.clone();
    cache.put(request, copy).then(() => evictOldEntries(cache)).catch(() => { /* quota cheia */ });
  }
  return response;
}

function evictOldEntries(cache) {
  return cache.keys().then((keys) => {
    const excess = keys.length - MAX_FW_ENTRIES;
    if (excess <= 0) return;
    return Promise.all(keys.slice(0, excess).map((k) => cache.delete(k)));
  });
}

async function networkFirst(request) {
  try {
    const response = await fetch(request);
    if (response.ok) {
      // Os clones têm que ser criados ANTES de resolver o response —
      // dentro do await do caches.open o browser já começou a ler o
      // body e clone() lança "Response body is already used".
      const copy = response.clone();
      const shellCopy = request.mode === 'navigate' ? response.clone() : null;
      caches.open(CACHE_SHELL).then((cache) => {
        const puts = [cache.put(request, copy)];
        if (shellCopy) puts.push(cache.put('/', shellCopy));
        return Promise.all(puts);
      }).catch(() => { /* quota cheia / cache fechado */ });
    }
    return response;
  } catch {
    const cache = await caches.open(CACHE_SHELL);
    const hit = await cache.match(request);
    if (hit) return hit;
    // Falha de rede num asset: resolve com erro em vez de rejeitar —
    // evita "Uncaught (in promise) TypeError: Failed to fetch" no console.
    return request.mode === 'navigate'
      ? cache.match('/')
      : new Response(null, { status: 504, statusText: 'Gateway Timeout' });
  }
}

self.addEventListener('fetch', (event) => {
  const { request } = event;
  const url = new URL(request.url);

  if (request.method !== 'GET' || url.origin !== self.location.origin) return;
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/hubs')) return;

  if (isImmutableFrameworkAsset(url.pathname)) {
    event.respondWith(
      cacheFirst(request).catch(
        () => new Response(null, { status: 504, statusText: 'Gateway Timeout' })
      )
    );
    return;
  }

  event.respondWith(networkFirst(request));
});
