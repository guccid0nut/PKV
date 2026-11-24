// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

(function () {
    "use strict";

    function formatUtcHms(isoOrDate) {
        const d = isoOrDate ? new Date(isoOrDate) : new Date();
        if (Number.isNaN(d.getTime())) {
            return new Date().toISOString().substring(11, 19) + " UTC";
        }
        return d.toISOString().substring(11, 19) + " UTC";
    }

    function createRow(label, count, options) {
        const opts = options || {};
        const row = document.createElement("div");
        row.className = "flex items-center justify-between py-1.5 border-b border-slate-800/70 last:border-b-0 text-xs sm:text-sm gap-3";

        const left = document.createElement("span");
        if (opts.highlightAllLocations) {
            left.className = "font-bold text-emerald-400 truncate";
        } else if (opts.muted) {
            left.className = "text-slate-400 truncate";
        } else {
            left.className = "text-slate-200 truncate";
        }
        left.textContent = String(label || "");

        const right = document.createElement("span");
        const numeric = Number(count);
        const isUnavailable = Boolean(opts.unavailable);
        if (isUnavailable) {
            right.className = "font-mono font-semibold text-slate-500 tabular-nums shrink-0";
            right.textContent = "—";
        } else if (opts.highlightAllLocations || numeric > 0) {
            right.className = "font-mono font-bold text-emerald-400 tabular-nums shrink-0";
            right.textContent = String(Number.isFinite(numeric) ? numeric : 0);
        } else {
            right.className = "font-mono font-semibold text-slate-400 tabular-nums shrink-0";
            right.textContent = "0";
        }

        row.appendChild(left);
        row.appendChild(right);
        return row;
    }

    function createEmptyNote(text) {
        const el = document.createElement("div");
        el.className = "py-2 text-xs text-slate-500 italic";
        el.textContent = text;
        return el;
    }

    function buildTmmQueueCard(queue, isUnavailable) {
        const card = document.createElement("div");
        card.className = "p-4 rounded-xl bg-slate-950/60 border border-emerald-500/15 flex flex-col gap-2.5";

        const rawTitle = String((queue && queue.title) || "Matchmaking Queue");
        const pipeIdx = rawTitle.indexOf("|");
        const primaryTitle = pipeIdx >= 0 ? rawTitle.slice(0, pipeIdx).trim() : rawTitle;
        const subtitle = pipeIdx >= 0 ? rawTitle.slice(pipeIdx + 1).trim() : "";

        const header = document.createElement("div");
        header.className = "flex items-start justify-between gap-2 pb-1.5 border-b border-slate-800/80";

        const titleWrap = document.createElement("div");
        titleWrap.className = "min-w-0";

        const h4 = document.createElement("div");
        h4.className = "text-sm font-bold text-white leading-tight truncate";
        h4.textContent = primaryTitle;
        titleWrap.appendChild(h4);

        if (subtitle) {
            const sub = document.createElement("div");
            sub.className = "text-[11px] font-semibold text-emerald-400/90 leading-tight mt-0.5 truncate";
            sub.textContent = subtitle;
            titleWrap.appendChild(sub);
        }

        header.appendChild(titleWrap);

        const totalQueued = Number((queue && queue.totalQueued) || 0);
        if (!isUnavailable && totalQueued > 0) {
            const pill = document.createElement("span");
            pill.className = "px-2 py-0.5 rounded-md bg-emerald-500/15 border border-emerald-500/30 text-emerald-300 text-[11px] font-mono font-bold shrink-0";
            pill.textContent = String(totalQueued);
            header.appendChild(pill);
        }

        card.appendChild(header);

        const list = document.createElement("div");
        list.className = "flex flex-col";
        const regions = (queue && Array.isArray(queue.regions)) ? queue.regions : [];
        if (regions.length === 0) {
            list.appendChild(createEmptyNote(isUnavailable ? "Syncing queue pools…" : "No queue pools reported"));
        } else {
            regions.forEach(function (entry) {
                list.appendChild(createRow(
                    entry && entry.region,
                    entry && entry.count,
                    { muted: !isUnavailable && Number(entry && entry.count) === 0, unavailable: isUnavailable }
                ));
            });
        }
        card.appendChild(list);
        return card;
    }

    function applyStatusBadge(rootEl, status, updatedAtUtc) {
        const dotEl = rootEl.querySelector("[data-pop-status-dot]");
        const statusEl = rootEl.querySelector("[data-pop-status-badge]");
        const timeEl = rootEl.querySelector("[data-pop-updated-time]");

        const normalized = String(status || "unavailable").toLowerCase();
        if (dotEl) {
            dotEl.className = normalized === "live"
                ? "w-2.5 h-2.5 rounded-full bg-emerald-400 animate-pulse shrink-0"
                : "w-2.5 h-2.5 rounded-full bg-amber-400 animate-pulse shrink-0";
        }
        if (statusEl) {
            if (normalized === "live") {
                statusEl.className = "px-2.5 py-0.5 rounded-full text-[11px] font-semibold bg-emerald-500/10 text-emerald-300 border border-emerald-500/30";
                statusEl.textContent = "LIVE";
            } else if (normalized === "stale") {
                statusEl.className = "px-2.5 py-0.5 rounded-full text-[11px] font-semibold bg-amber-500/15 text-amber-300 border border-amber-500/30";
                statusEl.textContent = "STALE SNAPSHOT";
            } else {
                statusEl.className = "px-2.5 py-0.5 rounded-full text-[11px] font-semibold bg-amber-500/15 text-amber-300 border border-amber-500/30";
                statusEl.textContent = "LIVE TELEMETRY SYNCING";
            }
        }
        if (timeEl) {
            timeEl.textContent = normalized === "unavailable"
                ? "Awaiting snapshot"
                : formatUtcHms(updatedAtUtc);
        }
    }

    function render(rootEl, payload) {
        if (!rootEl) return;

        const data = payload || { status: "unavailable" };
        const status = String(data.status || "live").toLowerCase();
        const isUnavailable = status === "unavailable";

        rootEl.dataset.popStatus = status;
        applyStatusBadge(rootEl, status, data.updatedAtUtc);

        const onlineEl = rootEl.querySelector("[data-pop-online-count]");
        if (onlineEl) {
            onlineEl.textContent = isUnavailable
                ? "—"
                : String(Math.max(0, Number(data.onlinePlayers || 0)));
            onlineEl.className = isUnavailable
                ? "px-2.5 py-0.5 rounded-lg bg-amber-500/15 border border-amber-500/30 text-amber-300 font-mono text-sm font-extrabold tabular-nums"
                : "px-2.5 py-0.5 rounded-lg bg-emerald-500/20 border border-emerald-400/40 text-emerald-300 font-mono text-sm font-extrabold tabular-nums";
        }

        const gamesListEl = rootEl.querySelector("[data-pop-games-list]");
        if (gamesListEl) {
            const nodes = [];
            if (isUnavailable) {
                nodes.push(createRow("All locations", 0, { highlightAllLocations: true, unavailable: true }));
                nodes.push(createEmptyNote("Live game telemetry temporarily unavailable"));
            } else {
                const games = Array.isArray(data.gamesInProgressByRegion) ? data.gamesInProgressByRegion : [];
                if (games.length === 0) {
                    nodes.push(createRow("All locations", Number(data.gamesInProgressTotal || 0), { highlightAllLocations: true }));
                } else {
                    games.forEach(function (entry, idx) {
                        const isAll = idx === 0 || String((entry && entry.region) || "").toLowerCase() === "all locations";
                        nodes.push(createRow(entry && entry.region, entry && entry.count, {
                            highlightAllLocations: isAll,
                            muted: !isAll && Number(entry && entry.count) === 0
                        }));
                    });
                }
            }
            gamesListEl.replaceChildren.apply(gamesListEl, nodes);
        }

        const serversListEl = rootEl.querySelector("[data-pop-servers-list]");
        if (serversListEl) {
            const nodes = [];
            if (isUnavailable) {
                nodes.push(createEmptyNote("Live server availability temporarily unavailable"));
            } else {
                const servers = Array.isArray(data.serversAvailableByRegion) ? data.serversAvailableByRegion : [];
                if (servers.length === 0) {
                    nodes.push(createEmptyNote("No idle servers reported"));
                } else {
                    servers.forEach(function (entry) {
                        nodes.push(createRow(entry && entry.region, entry && entry.count, {
                            muted: Number(entry && entry.count) === 0
                        }));
                    });
                }
            }
            serversListEl.replaceChildren.apply(serversListEl, nodes);
        }

        const tmmGridEl = rootEl.querySelector("[data-pop-tmm-grid]");
        if (tmmGridEl) {
            const queues = Array.isArray(data.tmmQueues) ? data.tmmQueues : [];
            const nodes = [];
            if (queues.length === 0) {
                nodes.push(createEmptyNote(isUnavailable ? "Syncing matchmaking queues…" : "No active TMM queues reported"));
            } else {
                queues.forEach(function (q) {
                    nodes.push(buildTmmQueueCard(q, isUnavailable));
                });
            }
            tmmGridEl.replaceChildren.apply(tmmGridEl, nodes);
        }
    }

    function startPolling(rootEl, options) {
        if (!rootEl) return { stop: function () {} };
        const opts = options || {};
        const endpoint = opts.endpoint || "/api/telemetry/population";
        const baseIntervalMs = Math.max(5000, Number(opts.intervalMs || 30000));
        const jitterMs = Math.max(0, Number(opts.jitterMs || 2000));

        let stopped = false;
        let timerId = null;
        let lastFetchMs = 0;

        function nextDelayMs() {
            if (jitterMs <= 0) return baseIntervalMs;
            const offset = Math.floor((Math.random() * 2 - 1) * jitterMs);
            return Math.max(5000, baseIntervalMs + offset);
        }

        function scheduleNext() {
            if (stopped || document.hidden) return;
            clearTimeout(timerId);
            timerId = setTimeout(fetchOnce, nextDelayMs());
        }

        async function fetchOnce() {
            if (stopped || document.hidden) return;
            lastFetchMs = Date.now();
            try {
                const res = await fetch(endpoint, {
                    method: "GET",
                    headers: { "Accept": "application/json" },
                    credentials: "same-origin"
                });
                if (!res.ok) {
                    console.warn("[LivePopulationCard] HTTP " + res.status + " from " + endpoint);
                    render(rootEl, { status: "unavailable" });
                } else {
                    const data = await res.json();
                    render(rootEl, data);
                }
            } catch (err) {
                console.warn("[LivePopulationCard] Fetch error:", err);
                render(rootEl, { status: "unavailable" });
            } finally {
                scheduleNext();
            }
        }

        function onVisibilityChange() {
            if (stopped) return;
            if (document.hidden) {
                clearTimeout(timerId);
                timerId = null;
            } else if (Date.now() - lastFetchMs >= Math.min(10000, baseIntervalMs)) {
                fetchOnce();
            } else {
                scheduleNext();
            }
        }

        document.addEventListener("visibilitychange", onVisibilityChange);
        fetchOnce();

        return {
            stop: function () {
                stopped = true;
                clearTimeout(timerId);
                timerId = null;
                document.removeEventListener("visibilitychange", onVisibilityChange);
            }
        };
    }

    window.KongorPopulationCard = {
        render: render,
        startPolling: startPolling,
        formatUtcHms: formatUtcHms
    };

    document.addEventListener("DOMContentLoaded", function () {
        document.querySelectorAll('[data-population-card="auto"]').forEach(function (el) {
            const pollMs = Number(el.dataset.pollMs || 30000);
            const endpoint = el.dataset.endpoint || "/api/telemetry/population";
            startPolling(el, { endpoint: endpoint, intervalMs: pollMs, jitterMs: 2000 });
        });
    });
})();
