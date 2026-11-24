// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend, Rate, Counter } from 'k6/metrics';

// Custom Telemetry Metrics (Wave 3B Stateless Scale & YARP Edge Gateway)
const gatewayHealthLatency = new Trend('kongor_gateway_health_latency_ms');
const matchHistoryLatency = new Trend('kongor_match_history_latency_ms');
const leaderboardLatency = new Trend('kongor_leaderboard_latency_ms');
const errorRate = new Rate('kongor_error_rate');
const rateLimitedTotal = new Counter('kongor_edge_429_total');
const edgeBurstUnexpected5xx = new Counter('kongor_edge_burst_5xx_total');

// Target Routing Architecture:
//   - GATEWAY_URL (:5080) receives unthrottled /healthz probes AND optional edge 429 burst traffic.
//   - WEB_API_URL (:5200) receives high-rate API/Dapper soak traffic directly by default so normal
//     soak scenarios do not self-sabotage into 99%+ 429s against EdgeGateway's default 240 req/min
//     per-IP rate limiter. Override KONGOR_WEB_API_URL=http://127.0.0.1:5080 only when EdgeGateway's
//     PermitLimit is raised or disabled for proxy throughput benchmarking.
const GATEWAY_URL = __ENV.KONGOR_EDGE_GATEWAY_URL || __ENV.KONGOR_TARGET_URL || 'http://127.0.0.1:5080';
const WEB_API_URL = __ENV.KONGOR_WEB_API_URL || 'http://127.0.0.1:5200';
const RUN_RATE_LIMIT_BURST = (__ENV.KONGOR_TEST_EDGE_429 || 'false').toLowerCase() === 'true';

const HEALTH_PEAK_VUS = Number.parseInt(__ENV.KONGOR_SOAK_VUS_HEALTH || '50', 10);
const HISTORY_VUS = Number.parseInt(__ENV.KONGOR_SOAK_VUS_HISTORY || '40', 10);
const LEADERBOARD_VUS = Number.parseInt(__ENV.KONGOR_SOAK_VUS_LEADERBOARD || '30', 10);

export const options = {
  scenarios: {
    // Scenario 1: Edge Gateway + Upstream Health & Multi-Pod Liveness Probe (/healthz is unthrottled)
    gateway_liveness: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '10s', target: Math.max(10, Math.floor(HEALTH_PEAK_VUS / 2)) },
        { duration: '20s', target: HEALTH_PEAK_VUS },
        { duration: '10s', target: 0 },
      ],
      gracefulRampDown: '5s',
      exec: 'scenarioGatewayHealth',
    },

    // Scenario 2: Dapper Read Projection Player Match History API (/api/stats/player/{id}/history?limit=20)
    match_history_projection: {
      executor: 'constant-vus',
      vus: HISTORY_VUS,
      duration: '40s',
      startTime: '5s',
      gracefulStop: '5s',
      exec: 'scenarioMatchHistoryProjection',
    },

    // Scenario 3: Paginated Competitive Leaderboard Read Traffic
    leaderboard_reads: {
      executor: 'constant-vus',
      vus: LEADERBOARD_VUS,
      duration: '40s',
      startTime: '5s',
      gracefulStop: '5s',
      exec: 'scenarioLeaderboardReads',
    },

    // Optional Scenario 4: Verify YARP Edge Rate Limiter returns 429 + Retry-After under flood
    // (Must exceed default 240 req/min per-IP limit -> 320 iterations across 20 VUs)
    ...(RUN_RATE_LIMIT_BURST
      ? {
          edge_rate_limit_burst: {
            executor: 'shared-iterations',
            vus: 20,
            iterations: 320,
            startTime: '5s',
            exec: 'scenarioEdgeRateLimitBurst',
          },
        }
      : {}),
  },

  thresholds: {
    http_req_duration: ['p(95)<150'],
    kongor_gateway_health_latency_ms: ['p(95)<50', 'p(99)<100'],
    kongor_match_history_latency_ms: ['p(95)<100', 'p(99)<200'],
    kongor_leaderboard_latency_ms: ['p(95)<120', 'p(99)<250'],
    kongor_error_rate: ['rate<0.01'],
    ...(RUN_RATE_LIMIT_BURST
      ? {
          kongor_edge_429_total: ['count>0'],
          kongor_edge_burst_5xx_total: ['count==0'],
        }
      : {}),
  },
};

export function scenarioGatewayHealth() {
  const res = http.get(`${GATEWAY_URL}/healthz`, {
    headers: { 'X-Kongor-LoadTest': 'k6-wave3b' },
    tags: { name: 'GatewayHealthz' },
  });
  gatewayHealthLatency.add(res.timings.duration);

  const ok = check(res, {
    'healthz status is 200': (r) => r.status === 200,
  });
  errorRate.add(ok ? 0 : 1);
  sleep(0.25);
}

export function scenarioMatchHistoryProjection() {
  const accountId = 1 + (__VU % 100);
  const res = http.get(`${WEB_API_URL}/api/stats/player/${accountId}/history?limit=20`, {
    headers: {
      Accept: 'application/json',
      'X-Kongor-Client-Version': '5.0.2',
    },
    tags: { name: 'PlayerMatchHistoryProjection' },
  });
  matchHistoryLatency.add(res.timings.duration);

  const ok = check(res, {
    'history status is 200': (r) => r.status === 200,
    'history returns json array': (r) => Boolean(r.body) && r.body.trim().startsWith('['),
  });
  errorRate.add(ok ? 0 : 1);
  sleep(0.5);
}

export function scenarioLeaderboardReads() {
  const page = 1 + (__ITER % 5);
  const res = http.get(`${WEB_API_URL}/api/stats/leaderboard?page=${page}&pageSize=50`, {
    headers: { Accept: 'application/json' },
    tags: { name: 'LeaderboardPage' },
  });
  leaderboardLatency.add(res.timings.duration);

  const ok = check(res, {
    'leaderboard status is 200': (r) => r.status === 200,
  });
  errorRate.add(ok ? 0 : 1);
  sleep(0.5);
}

export function scenarioEdgeRateLimitBurst() {
  const res = http.get(`${GATEWAY_URL}/api/stats/leaderboard?page=1&pageSize=10`, {
    headers: { 'X-Kongor-BurstTest': 'true' },
    tags: { name: 'EdgeRateLimitBurst' },
  });

  if (res.status >= 500) {
    edgeBurstUnexpected5xx.add(1);
    errorRate.add(1);
    console.error(`[EdgeBurst] Unexpected upstream/gateway 5xx: status=${res.status} body=${String(res.body || '').slice(0, 200)}`);
  }

  const expectedStatus = check(res, {
    'burst response is 200 or 429 (never 5xx)': (r) => r.status === 200 || r.status === 429,
  });
  if (!expectedStatus && res.status < 500) {
    errorRate.add(1);
  }

  if (res.status === 429) {
    rateLimitedTotal.add(1);
    const hasRetryAfter = check(res, {
      '429 includes Retry-After header': (r) => Boolean(r.headers['Retry-After'] || r.headers['retry-after']),
    });
    if (!hasRetryAfter) {
      errorRate.add(1);
    }
  }
}
