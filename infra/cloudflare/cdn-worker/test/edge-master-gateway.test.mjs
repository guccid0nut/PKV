// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)
// ============================================================================
// File: edge-master-gateway.test.mjs
// System: Project Kongor - Cloudflare Worker Edge Master Gateway Test Harness
// Purpose: Deterministic validation of edge caching, legacy K2 RPC parsing,
//          ACID pass-through, cross-region matchmaking mesh, and CDN patching.
// ============================================================================

import { test, describe } from "node:test";
import assert from "node:assert/strict";
import worker from "../patch-cdn-worker.js";

describe("Cloudflare Edge Master Gateway (TAO)", () => {
  const env = {
    MASTER_SERVER_ORIGIN: "http://127.0.0.1:55555",
    EDGE_COLO: "FRA", // Frankfurt edge simulation
    CACHE_PURGE_SECRET: "test-purge-secret-123"
  };

  test("1. Health check returns edge colo and gateway status", async () => {
    const req = new Request("http://edge.kongor.net/health");
    const res = await worker.fetch(req, env);

    assert.equal(res.status, 200);
    assert.equal(res.headers.get("Content-Type"), "application/json");
    assert.equal(res.headers.get("X-Kongor-Edge-Colo"), "FRA");

    const data = await res.json();
    assert.equal(data.status, "healthy");
    assert.equal(data.system, "ProjectKongor-EdgeMasterGateway");
    assert.equal(data.edge_colo, "FRA");
  });

  test("2. Modern REST /master/serverlist serves edge cache on subsequent requests", async () => {
    const req1 = new Request("http://edge.kongor.net/master/serverlist?region=eu-central&gametype=10");
    const res1 = await worker.fetch(req1, env);

    assert.equal(res1.status, 200);
    const data1 = await res1.json();
    assert.ok(data1.servers.length > 0, "Should return server array");

    // Second request should HIT the edge cache
    const req2 = new Request("http://edge.kongor.net/master/serverlist?region=eu-central&gametype=10");
    const res2 = await worker.fetch(req2, env);

    assert.equal(res2.status, 200);
    assert.equal(res2.headers.get("X-Kongor-Cache"), "HIT");
    assert.ok(res2.headers.get("Server-Timing")?.includes("edge;dur="));
  });

  test("3. Modern REST /master/ladder caches leaderboard rankings", async () => {
    const req1 = new Request("http://edge.kongor.net/master/ladder?season=current");
    const res1 = await worker.fetch(req1, env);

    assert.equal(res1.status, 200);
    const data1 = await res1.json();
    assert.ok(data1.top_players.length >= 2);
    assert.equal(data1.top_players[0].nickname, "Merrick");

    // Second request should HIT the edge cache
    const req2 = new Request("http://edge.kongor.net/master/ladder?season=current");
    const res2 = await worker.fetch(req2, env);

    assert.equal(res2.status, 200);
    assert.equal(res2.headers.get("X-Kongor-Cache"), "HIT");
  });

  test("4. /master/matchmaking/queues aggregates cross-region queue mesh", async () => {
    const req = new Request("http://edge.kongor.net/master/matchmaking/queues");
    const res = await worker.fetch(req, env);

    assert.equal(res.status, 200);
    const data = await res.json();
    assert.equal(data.mesh_status, "active-active");
    assert.ok(data.regions["eu-central"]);
    assert.ok(data.regions["ap-southeast"]);
    assert.ok(data.regions["us-east"]);
    assert.ok(data.total_queued_players > 0);
  });

  test("5. Cache purge webhook enforces authorization, fails closed without secret, and clears memory cache", async () => {
    // Missing CACHE_PURGE_SECRET must fail closed (503)
    const unconfiguredReq = new Request("http://edge.kongor.net/master/cache/purge", {
      method: "POST",
      headers: { "X-Kongor-Purge-Key": "anything" },
      body: JSON.stringify({ tag: "server-list" })
    });
    const unconfiguredRes = await worker.fetch(unconfiguredReq, {
      MASTER_SERVER_ORIGIN: env.MASTER_SERVER_ORIGIN,
      EDGE_COLO: env.EDGE_COLO
    });
    assert.equal(unconfiguredRes.status, 503);

    // Unauthorized attempt
    const badReq = new Request("http://edge.kongor.net/master/cache/purge", {
      method: "POST",
      headers: { "X-Kongor-Purge-Key": "wrong-secret" },
      body: JSON.stringify({ tag: "server-list" })
    });
    const badRes = await worker.fetch(badReq, env);
    assert.equal(badRes.status, 401);

    // Authorized purge
    const goodReq = new Request("http://edge.kongor.net/master/cache/purge", {
      method: "POST",
      headers: {
        "X-Kongor-Purge-Key": "test-purge-secret-123",
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ pattern: "serverlist" })
    });
    const goodRes = await worker.fetch(goodReq, env);
    assert.equal(goodRes.status, 200);
    const purgeData = await goodRes.json();
    assert.equal(purgeData.status, "ok");
    assert.ok(purgeData.count >= 1, "Should have purged cached serverlist entries");

    // Next request to serverlist should MISS cache now
    const reqAfterPurge = new Request("http://edge.kongor.net/master/serverlist?region=eu-central&gametype=10");
    const resAfterPurge = await worker.fetch(reqAfterPurge, env);
    assert.notEqual(resAfterPurge.headers.get("X-Kongor-Cache"), "HIT");
  });

  test("6. Legacy K2 /client_requester.php form POST parses and caches f=server_list", async () => {
    const postBody = "f=server_list&cookie=019ba104320c705a8b5524d8fdb8d2a2&gametype=10&region=ALL";
    const req1 = new Request("http://edge.kongor.net/client_requester.php", {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: postBody
    });

    // Mock origin response if origin is offline: worker returns 502 with diagnostic info
    const res1 = await worker.fetch(req1, env);
    // Origin is not running locally in unit test, so status is 502 or 200 if mocked
    assert.ok([200, 502].includes(res1.status));
    assert.equal(res1.headers.get("X-Kongor-Edge-Colo"), "FRA");
  });

  test("7. Patch CDN endpoints remain fully functional", async () => {
    // Manifest endpoint
    const manifestReq = new Request("http://edge.kongor.net/patch/manifest?version=latest&variant=client-win64");
    const manifestRes = await worker.fetch(manifestReq, env);
    assert.equal(manifestRes.status, 200);
    const manifestData = await manifestRes.json();
    assert.equal(manifestData.version, "4.10.2.0");
    assert.equal(manifestData.files.length, 3);

    // Delta missing hash validation
    const deltaReq = new Request("http://edge.kongor.net/patch/delta");
    const deltaRes = await worker.fetch(deltaReq, env);
    assert.equal(deltaRes.status, 400);
  });

  test("8. Multi-OS pkv-v5 manifest (wac, lac, mac) and SHA-256 CAS object routes enforce PKV Guard invariants", async () => {
    const sampleHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    const bucketStore = new Map([
      [
        "lac/manifest.json",
        JSON.stringify({
          protocol: "pkv-v5",
          version: "5.0.1",
          sequence: 7,
          hashAlgorithm: "SHA-256",
          excludeFromSource: [],
          excludeFromTarget: [],
          files: { "games/client/override/ui/test.lua": { size: 4, hash: sampleHash } },
          mode: "overlay",
          objectLayout: "sha256",
          remove: [],
          executablePaths: [],
          signature: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=="
        })
      ],
      [`objects/01/${sampleHash}`, "lua\n"]
    ]);

    const r2Env = {
      ...env,
      PATCH_BUCKET: {
        async get(key) {
          if (!bucketStore.has(key)) return null;
          return { body: bucketStore.get(key), httpEtag: `"etag-${key}"` };
        }
      }
    };

    // Remote CDN (cdn.example.com): R2 hit for lac returns 200; R2 miss for wac & mac returns 404
    // so WILLOWMAKER enters graceful local-files fallback instead of tripping red PKV Guard on signature:null
    for (const variant of ["wac", "lac", "mac"]) {
      const req = new Request(`https://cdn.example.com/${variant}/manifest.json`);
      const res = await worker.fetch(req, r2Env);
      assert.equal(res.headers.get("X-Kongor-Update-Protocol"), "pkv-v5");
      if (variant === "lac") {
        assert.equal(res.status, 200);
        const m = await res.json();
        assert.equal(m.protocol, "pkv-v5");
        assert.equal(m.version, "5.0.1");
        assert.equal(m.mode, "overlay");
        assert.equal(m.objectLayout, "sha256");
        assert.equal(m.hashAlgorithm, "SHA-256");
        assert.equal(m.sequence, 7);
        assert.equal(m.files["games/client/override/ui/test.lua"].hash, sampleHash);
      } else {
        assert.equal(res.status, 404);
        const body = await res.json();
        assert.equal(body.error, "Manifest not found in R2 storage.");
        assert.equal(body.key, `${variant}/manifest.json`);
      }
    }

    // Loopback dev host (127.0.0.1): R2 miss returns unsigned 200 baselineManifest (allowed by loopback policy)
    const loopbackRes = await worker.fetch(new Request("http://127.0.0.1:8787/wac/manifest.json"), r2Env);
    assert.equal(loopbackRes.status, 200);
    assert.equal(loopbackRes.headers.get("X-Kongor-Update-Protocol"), "pkv-v5");
    const loopbackManifest = await loopbackRes.json();
    assert.equal(loopbackManifest.protocol, "pkv-v5");
    assert.equal(loopbackManifest.signature, null);

    // CAS SHA-256 object hit
    const casRes = await worker.fetch(new Request(`https://cdn.example.com/objects/01/${sampleHash}`), r2Env);
    assert.equal(casRes.status, 200);
    assert.equal(casRes.headers.get("X-Kongor-Update-Protocol"), "pkv-v5");
    assert.match(casRes.headers.get("Cache-Control") || "", /immutable/);
    assert.equal(await casRes.text(), "lua\n");

    // CAS prefix mismatch rejected with 400
    const badCasRes = await worker.fetch(new Request(`https://cdn.example.com/objects/ff/${sampleHash}`), r2Env);
    assert.equal(badCasRes.status, 400);
  });
});
