#!/usr/bin/env node
/**
 * Playwright page-load measurement, adapted from the ad-hoc /applications/deploy/jellyfin-perf/
 * run.js proven this session, generalized for the perf harness: no hardcoded server id/session
 * tokens/localhost target — auth and base URL are passed in (see seed.js), so this runs against
 * any freshly-seeded container.
 *
 * CPU throttle is pinned to Chrome DevTools' own "Low-end mobile" preset (4x slowdown) rather
 * than an arbitrary multiplier, so timing numbers stay comparable across separate runs/machines,
 * not just within one same-run baseline/candidate comparison — a real, named reference point
 * instead of "however fast this box happens to be today".
 *
 * Usage: node measure.js <baseUrl> <accessToken> <userId> <serverId>
 * Prints { page, navTime, lcp, fcp, ttfb, cls, totalRequests }[] as JSON on stdout.
 */

const { chromium } = require('playwright');

const CPU_THROTTLE_RATE = 4; // Chrome DevTools "Low-end mobile" preset.
const NUM_RUNS = 3;

const PAGES = [
  { name: 'home', path: '#!/home.html', waitFor: '.homeSectionsContainer, .home-section, .verticalSection', timeout: 20000 },
  { name: 'movies', path: '#!/movies.html', waitFor: '.itemsContainer, .libraryPage', timeout: 15000 },
];

const WEB_VITALS_SCRIPT = `
  window.__perfMetrics = { lcp: null, cls: 0, fcp: null };
  new PerformanceObserver(list => {
    const entries = list.getEntries();
    if (entries.length) window.__perfMetrics.lcp = entries[entries.length - 1].startTime;
  }).observe({ type: 'largest-contentful-paint', buffered: true });
  new PerformanceObserver(list => {
    for (const entry of list.getEntries()) if (!entry.hadRecentInput) window.__perfMetrics.cls += entry.value;
  }).observe({ type: 'layout-shift', buffered: true });
  new PerformanceObserver(list => {
    for (const entry of list.getEntries()) if (entry.name === 'first-contentful-paint') window.__perfMetrics.fcp = entry.startTime;
  }).observe({ type: 'paint', buffered: true });
`;

function buildStorageState(baseUrl, token, userId, serverId) {
  const creds = {
    Servers: [
      {
        ManualAddress: baseUrl,
        manualAddressOnly: true,
        Name: 'perf-layer3',
        // Must be the container's real server id — jellyfin-web's client-side connection
        // handshake compares this against the live server's actual id before rendering
        // authenticated content; a placeholder left the SPA stuck pre-render indefinitely.
        Id: serverId,
        LocalAddress: baseUrl,
        AccessToken: token,
        UserId: userId,
      },
    ],
  };
  return {
    cookies: [],
    origins: [
      {
        origin: baseUrl,
        localStorage: [
          { name: 'jellyfin_credentials', value: JSON.stringify(creds) },
          { name: '_deviceId2', value: 'perf-layer3-measure' },
        ],
      },
    ],
  };
}

function avg(arr) {
  return arr.length ? arr.reduce((a, b) => a + b, 0) / arr.length : null;
}

async function measurePage(browser, baseUrl, pageCfg, storageState) {
  const context = await browser.newContext({ viewport: { width: 1920, height: 1080 }, storageState });
  await context.addInitScript(WEB_VITALS_SCRIPT);
  const page = await context.newPage();
  const cdp = await context.newCDPSession(page);
  await cdp.send('Emulation.setCPUThrottlingRate', { rate: CPU_THROTTLE_RATE });

  let requestCount = 0;
  page.on('request', (req) => {
    const url = req.url();
    if (url.startsWith(baseUrl) && !url.includes('/web/')) requestCount += 1;
  });

  const navStart = Date.now();
  await page.goto(`${baseUrl}/web/index.html${pageCfg.path}`, { waitUntil: 'domcontentloaded', timeout: pageCfg.timeout });
  try {
    await page.waitForSelector(pageCfg.waitFor, { timeout: pageCfg.timeout });
  } catch {
    // Still report whatever metrics we got — a missing selector is itself informative upstream.
  }
  await page.waitForTimeout(500);
  const navTime = Date.now() - navStart;

  const metrics = await page.evaluate(() => window.__perfMetrics || {});
  const ttfb = await page.evaluate(() => {
    const nav = performance.getEntriesByType('navigation')[0];
    return nav ? Math.round(nav.responseStart) : null;
  });

  await context.close();

  return {
    page: pageCfg.name,
    navTime,
    lcp: metrics.lcp !== null && metrics.lcp !== undefined ? Math.round(metrics.lcp) : null,
    fcp: metrics.fcp !== null && metrics.fcp !== undefined ? Math.round(metrics.fcp) : null,
    ttfb,
    cls: metrics.cls ? +metrics.cls.toFixed(4) : 0,
    totalRequests: requestCount,
  };
}

(async () => {
  const [baseUrl, accessToken, userId, serverId] = process.argv.slice(2);
  if (!baseUrl || !accessToken || !userId || !serverId) {
    console.error('Usage: node measure.js <baseUrl> <accessToken> <userId> <serverId>');
    process.exit(1);
  }

  const storageState = buildStorageState(baseUrl, accessToken, userId, serverId);
  const browser = await chromium.launch({ headless: true });

  const results = [];
  for (const pageCfg of PAGES) {
    const runs = [];
    for (let i = 0; i < NUM_RUNS; i += 1) {
      runs.push(await measurePage(browser, baseUrl, pageCfg, storageState));
    }
    results.push({
      page: pageCfg.name,
      navTime: Math.round(avg(runs.map((r) => r.navTime))),
      lcp: avg(runs.filter((r) => r.lcp !== null).map((r) => r.lcp)),
      fcp: avg(runs.filter((r) => r.fcp !== null).map((r) => r.fcp)),
      ttfb: avg(runs.filter((r) => r.ttfb !== null).map((r) => r.ttfb)),
      cls: +avg(runs.map((r) => r.cls)).toFixed(4),
      totalRequests: Math.round(avg(runs.map((r) => r.totalRequests))),
      runs: NUM_RUNS,
    });
  }

  await browser.close();
  process.stdout.write(JSON.stringify(results));
})().catch((err) => {
  console.error(err.stack || err.message);
  process.exit(1);
});
