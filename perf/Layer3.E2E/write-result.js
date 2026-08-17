#!/usr/bin/env node
/**
 * Builds the standardized perf-result.json (perf/schema/perf-result.schema.json) from a
 * baseline and candidate measure.js run — mirrors perf/Common/PerfResultWriter.cs's shape so
 * perf/compare/compare.py works uniformly across all three layers, even though Layer 3 computes
 * its own baseline-vs-candidate ratios inline (same host, same run) rather than needing a
 * separate cross-run diff.
 *
 * Every metric here is either kind:"ratio" (candidate/baseline, host-independent by
 * construction, gating) or kind:"timing" (raw ms, gating:false — real-browser wall clock is
 * genuinely noisy even same-host/same-run, informational only; see perf/README.md).
 *
 * Usage: node write-result.js <baselineResultsJson> <candidateResultsJson> <gitRef> <sha> <outPath>
 *   baselineResultsJson/candidateResultsJson: paths to measure.js's stdout JSON, saved to files.
 */

const fs = require('fs');

// Any increase in request count for identical seeded content is a real, host-independent signal
// (not timing noise) — a small tolerance absorbs incidental non-determinism (e.g. image lazy-load
// ordering), not genuine regressions.
const REQUEST_COUNT_TOLERANCE_RATIO = 1.1;

const [baselinePath, candidatePath, gitRef, sha, outPath] = process.argv.slice(2);

const baseline = JSON.parse(fs.readFileSync(baselinePath, 'utf8'));
const candidate = JSON.parse(fs.readFileSync(candidatePath, 'utf8'));

function ratio(b, c) {
  if (b === null || b === undefined || b === 0) return null;
  return c / b;
}

const results = [];

for (const cPage of candidate) {
  const bPage = baseline.find((p) => p.page === cPage.page);
  if (!bPage) continue;

  for (const metric of ['navTime', 'lcp', 'fcp', 'ttfb']) {
    results.push({
      id: `e2e-${cPage.page}-${metric}`,
      description: `${cPage.page} page ${metric}, under a pinned Chrome DevTools "Low-end mobile" (4x) CPU throttle`,
      kind: 'timing',
      status: 'pass',
      gating: false,
      metrics: {
        baselineMs: bPage[metric],
        candidateMs: cPage[metric],
        ratio: ratio(bPage[metric], cPage[metric]),
      },
    });
  }

  const requestRatio = ratio(bPage.totalRequests, cPage.totalRequests);
  const requestStatus = requestRatio === null ? 'not_applicable' : requestRatio <= REQUEST_COUNT_TOLERANCE_RATIO ? 'pass' : 'fail';
  results.push({
    id: `e2e-${cPage.page}-request-count`,
    description: `${cPage.page} page API request count doesn't regress vs baseline for identical seeded content — host-independent, unlike timing`,
    kind: 'ratio',
    status: requestStatus,
    gating: true,
    metrics: {
      baselineRequests: bPage.totalRequests,
      candidateRequests: cPage.totalRequests,
      ratio: requestRatio,
      toleranceRatio: REQUEST_COUNT_TOLERANCE_RATIO,
    },
  });
}

const out = {
  schemaVersion: '1.0',
  run: {
    ref: gitRef,
    sha: sha === 'null' || !sha ? null : sha,
    timestamp: new Date().toISOString(),
    layer: 'layer3-e2e',
    host: { note: 'informational only, never used for pass/fail — see perf/README.md' },
  },
  results,
};

fs.writeFileSync(outPath, JSON.stringify(out, null, 2));
const failing = results.filter((r) => r.gating && r.status === 'fail');
console.log(`Wrote ${outPath} — ${results.length} results, ${failing.length} gating failure(s).`);
if (failing.length > 0) process.exitCode = 1;
