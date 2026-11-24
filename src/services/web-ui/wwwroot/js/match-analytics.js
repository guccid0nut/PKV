// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * DAWNBRINGER Interactive Match Analytics & Spatial Heatmap Engine
 * Self-contained HTML5 Canvas & SVG visualizer for Gold/XP swings, combat heatmaps, and player radar metrics.
 */

(function () {
    'use strict';

    window.KongorMatchAnalytics = {
        timelineData: null,
        heatmapData: null,
        telemetryData: null,
        activeChartMode: 'gold', // 'gold', 'xp', 'cumulative'
        activeHeatmapFilter: 'all', // 'all', 'legion', 'hellbourne', 'wards'
        selectedRadarPlayer1: null,
        selectedRadarPlayer2: null,

        init: function (timeline, heatmap, telemetry) {
            this.timelineData = timeline;
            this.heatmapData = heatmap;
            this.telemetryData = telemetry;

            if (this.telemetryData && this.telemetryData.length >= 2) {
                this.selectedRadarPlayer1 = this.telemetryData[0].AccountId;
                // Find highest GPM/KDA from opposite team for default comparison
                const team2Players = this.telemetryData.filter(p => p.Team === 2);
                this.selectedRadarPlayer2 = team2Players.length > 0 ? team2Players[0].AccountId : this.telemetryData[1].AccountId;
            }

            this.initTimelineChart();
            this.initHeatmapCanvas();
            this.initRadarChart();
            this.initDamageBars();
            this.initEventListeners();
        },

        // =========================================================================
        // 1. DYNAMIC GOLD & XP SWING AREA CHART
        // =========================================================================
        initTimelineChart: function () {
            const canvas = document.getElementById('match-swing-chart-canvas');
            if (!canvas || !this.timelineData) return;

            const ctx = canvas.getContext('2d');
            const container = canvas.parentElement;

            const render = () => {
                const width = container.clientWidth || 800;
                const height = 280;
                const dpr = window.devicePixelRatio || 1;

                canvas.width = width * dpr;
                canvas.height = height * dpr;
                canvas.style.width = width + 'px';
                canvas.style.height = height + 'px';
                ctx.scale(dpr, dpr);

                ctx.clearRect(0, 0, width, height);

                const timestamps = this.timelineData.MinuteTimestamps || [];
                if (timestamps.length === 0) return;

                let values = [];
                let label = 'Gold Advantage';
                if (this.activeChartMode === 'xp') {
                    values = this.timelineData.LegionXpAdvantage || [];
                    label = 'XP Advantage';
                } else if (this.activeChartMode === 'cumulative') {
                    this.renderCumulativeCurves(ctx, width, height);
                    return;
                } else {
                    values = this.timelineData.LegionGoldAdvantage || [];
                }

                const padding = {top: 30, right: 35, bottom: 40, left: 55};
                const chartW = width - padding.left - padding.right;
                const chartH = height - padding.top - padding.bottom;

                let maxAbs = 2000;
                values.forEach(v => {
                    if (Math.abs(v) > maxAbs) maxAbs = Math.abs(v);
                });
                maxAbs = Math.ceil(maxAbs / 1000) * 1000; // Round to nearest 1k

                const zeroY = padding.top + chartH / 2;

                // Draw Grid Lines
                ctx.strokeStyle = 'rgba(255, 255, 255, 0.08)';
                ctx.lineWidth = 1;
                ctx.setLineDash([4, 4]);

                // Grid + Labels (+Max, 0, -Max)
                const gridSteps = [-maxAbs, -maxAbs / 2, 0, maxAbs / 2, maxAbs];
                ctx.font = '10px Inter, sans-serif';
                ctx.fillStyle = 'rgba(148, 163, 184, 0.7)';
                ctx.textAlign = 'right';

                gridSteps.forEach(val => {
                    const y = zeroY - (val / maxAbs) * (chartH / 2);
                    ctx.beginPath();
                    ctx.moveTo(padding.left, y);
                    ctx.lineTo(width - padding.right, y);
                    ctx.stroke();

                    const sign = val > 0 ? '+' : '';
                    const labelText = val === 0 ? 'EVEN' : `${sign}${(val / 1000).toFixed(1)}k`;
                    ctx.fillText(labelText, padding.left - 8, y + 3);
                });

                // Zero Center Baseline (Solid Glowing)
                ctx.setLineDash([]);
                ctx.strokeStyle = 'rgba(255, 255, 255, 0.25)';
                ctx.lineWidth = 1.5;
                ctx.beginPath();
                ctx.moveTo(padding.left, zeroY);
                ctx.lineTo(width - padding.right, zeroY);
                ctx.stroke();

                // Draw Timeline Path
                const stepX = chartW / Math.max(1, timestamps.length - 1);
                const points = values.map((v, i) => ({
                    x: padding.left + i * stepX,
                    y: zeroY - (v / maxAbs) * (chartH / 2),
                    val: v,
                    min: timestamps[i]
                }));

                // Split into Legion (Above Zero) and Hellbourne (Below Zero) Area Fills
                const gradLegion = ctx.createLinearGradient(0, padding.top, 0, zeroY);
                gradLegion.addColorStop(0, 'rgba(16, 185, 129, 0.45)');
                gradLegion.addColorStop(1, 'rgba(16, 185, 129, 0.02)');

                const gradHellbourne = ctx.createLinearGradient(0, zeroY, 0, height - padding.bottom);
                gradHellbourne.addColorStop(0, 'rgba(244, 63, 94, 0.02)');
                gradHellbourne.addColorStop(1, 'rgba(244, 63, 94, 0.45)');

                // Draw Smooth Line
                ctx.beginPath();
                ctx.moveTo(points[0].x, points[0].y);
                for (let i = 0; i < points.length - 1; i++) {
                    const xc = (points[i].x + points[i + 1].x) / 2;
                    const yc = (points[i].y + points[i + 1].y) / 2;
                    ctx.quadraticCurveTo(points[i].x, points[i].y, xc, yc);
                }
                ctx.lineTo(points[points.length - 1].x, points[points.length - 1].y);
                ctx.strokeStyle = '#38bdf8';
                ctx.lineWidth = 2.5;
                ctx.stroke();

                // Fill Area under curve
                ctx.lineTo(width - padding.right, zeroY);
                ctx.lineTo(padding.left, zeroY);
                ctx.closePath();
                ctx.fillStyle = gradLegion;
                ctx.fill();

                // X-Axis Minute Labels
                ctx.fillStyle = 'rgba(148, 163, 184, 0.8)';
                ctx.textAlign = 'center';
                const labelInterval = Math.max(1, Math.floor(timestamps.length / 8));
                timestamps.forEach((min, i) => {
                    if (i % labelInterval === 0 || i === timestamps.length - 1) {
                        const x = padding.left + i * stepX;
                        ctx.fillText(`${min}m`, x, height - padding.bottom + 18);
                    }
                });

                // Render Milestone Pins
                const milestones = this.timelineData.Milestones || [];
                const totalSec = Math.max(1, this.timelineData.DurationSeconds);

                milestones.forEach(m => {
                    const ratio = Math.min(1.0, m.GameTimeSeconds / totalSec);
                    const pinX = padding.left + ratio * chartW;

                    // Pin Stem
                    ctx.strokeStyle = m.Team === 1 ? '#10b981' : '#f43f5e';
                    ctx.lineWidth = 1.5;
                    ctx.setLineDash([2, 2]);
                    ctx.beginPath();
                    ctx.moveTo(pinX, zeroY - 20);
                    ctx.lineTo(pinX, zeroY + 20);
                    ctx.stroke();
                    ctx.setLineDash([]);

                    // Pin Head
                    ctx.fillStyle = m.Team === 1 ? '#059669' : '#e11d48';
                    ctx.beginPath();
                    ctx.arc(pinX, zeroY, 5, 0, Math.PI * 2);
                    ctx.fill();
                    ctx.strokeStyle = '#ffffff';
                    ctx.lineWidth = 1;
                    ctx.stroke();
                });
            };

            render();
            window.addEventListener('resize', render);
        },

        renderCumulativeCurves: function (ctx, width, height) {
            const padding = {top: 30, right: 35, bottom: 40, left: 60};
            const chartW = width - padding.left - padding.right;
            const chartH = height - padding.top - padding.bottom;

            const timestamps = this.timelineData.MinuteTimestamps || [];
            const legGold = this.timelineData.LegionCumulativeGold || [];
            const hbGold = this.timelineData.HellbourneCumulativeGold || [];

            const maxVal = Math.max(10000, ...legGold, ...hbGold);
            const stepX = chartW / Math.max(1, timestamps.length - 1);

            // Draw Y-Axis Grid
            ctx.strokeStyle = 'rgba(255, 255, 255, 0.08)';
            ctx.lineWidth = 1;
            ctx.setLineDash([4, 4]);
            ctx.font = '10px Inter, sans-serif';
            ctx.fillStyle = 'rgba(148, 163, 184, 0.7)';
            ctx.textAlign = 'right';

            for (let i = 0; i <= 4; i++) {
                const val = (maxVal / 4) * i;
                const y = height - padding.bottom - (val / maxVal) * chartH;
                ctx.beginPath();
                ctx.moveTo(padding.left, y);
                ctx.lineTo(width - padding.right, y);
                ctx.stroke();
                ctx.fillText(`${(val / 1000).toFixed(0)}k`, padding.left - 8, y + 3);
            }
            ctx.setLineDash([]);

            // Helper to draw smooth series
            const drawSeries = (series, color) => {
                ctx.strokeStyle = color;
                ctx.lineWidth = 2.5;
                ctx.beginPath();
                series.forEach((val, i) => {
                    const x = padding.left + i * stepX;
                    const y = height - padding.bottom - (val / maxVal) * chartH;
                    if (i === 0) ctx.moveTo(x, y);
                    else ctx.lineTo(x, y);
                });
                ctx.stroke();
            };

            drawSeries(legGold, '#10b981'); // Emerald (Legion)
            drawSeries(hbGold, '#f43f5e'); // Rose (Hellbourne)

            // X-Axis Minute Labels
            ctx.fillStyle = 'rgba(148, 163, 184, 0.8)';
            ctx.textAlign = 'center';
            const labelInterval = Math.max(1, Math.floor(timestamps.length / 8));
            timestamps.forEach((min, i) => {
                if (i % labelInterval === 0 || i === timestamps.length - 1) {
                    const x = padding.left + i * stepX;
                    ctx.fillText(`${min}m`, x, height - padding.bottom + 18);
                }
            });
        },

        setChartMode: function (mode) {
            this.activeChartMode = mode;
            document.querySelectorAll('.swing-chart-btn').forEach(btn => {
                if (btn.dataset.mode === mode) {
                    btn.classList.add('bg-purple-600', 'text-white', 'shadow-md');
                    btn.classList.remove('bg-slate-800', 'text-slate-400');
                } else {
                    btn.classList.remove('bg-purple-600', 'text-white', 'shadow-md');
                    btn.classList.add('bg-slate-800', 'text-slate-400');
                }
            });
            this.initTimelineChart();
        },

        // =========================================================================
        // 2. INTERACTIVE 2D MINIMAP COMBAT & WARD HEATMAP
        // =========================================================================
        initHeatmapCanvas: function () {
            const canvas = document.getElementById('match-heatmap-canvas');
            if (!canvas || !this.heatmapData) return;

            const ctx = canvas.getContext('2d');
            const size = 512;
            canvas.width = size;
            canvas.height = size;

            const kills = this.heatmapData.KillEvents || [];
            const wards = this.heatmapData.WardEvents || [];

            ctx.clearRect(0, 0, size, size);

            // Filter points according to active layer
            let activePoints = [];
            if (this.activeHeatmapFilter === 'all') {
                activePoints = kills;
            } else if (this.activeHeatmapFilter === 'legion') {
                activePoints = kills.filter(k => k.KillerTeam === 1);
            } else if (this.activeHeatmapFilter === 'hellbourne') {
                activePoints = kills.filter(k => k.KillerTeam === 2);
            } else if (this.activeHeatmapFilter === 'wards') {
                activePoints = wards;
            }

            // 1. Draw Thermal Gaussian Density Overlay
            if (this.activeHeatmapFilter !== 'wards') {
                activePoints.forEach(p => {
                    const px = p.NormalizedX * size;
                    const py = p.NormalizedY * size;
                    const radius = 38;

                    const grad = ctx.createRadialGradient(px, py, 2, px, py, radius);
                    grad.addColorStop(0, 'rgba(239, 68, 68, 0.45)');   // Red hot center
                    grad.addColorStop(0.4, 'rgba(245, 158, 11, 0.25)'); // Orange/Yellow
                    grad.addColorStop(0.8, 'rgba(16, 185, 129, 0.10)'); // Green ring
                    grad.addColorStop(1, 'rgba(0, 0, 0, 0)');

                    ctx.fillStyle = grad;
                    ctx.beginPath();
                    ctx.arc(px, py, radius, 0, Math.PI * 2);
                    ctx.fill();
                });
            }

            // 2. Draw Interactive Kill / Ward Pins
            const pinContainer = document.getElementById('heatmap-pin-overlay');
            if (pinContainer) {
                pinContainer.innerHTML = '';
                activePoints.forEach(p => {
                    const pin = document.createElement('div');
                    pin.className = 'absolute transform -translate-x-1/2 -translate-y-1/2 cursor-pointer group transition-transform hover:scale-125 z-10';
                    pin.style.left = (p.NormalizedX * 100) + '%';
                    pin.style.top = (p.NormalizedY * 100) + '%';

                    const isWard = p.EventType === 'WARD';
                    const bgClass = isWard ? 'bg-emerald-500 border-emerald-300' : (p.KillerTeam === 1 ? 'bg-cyan-500 border-cyan-300' : 'bg-rose-500 border-rose-300');

                    pin.innerHTML = `
                        <div class="w-4 h-4 rounded-full ${bgClass} border shadow-lg flex items-center justify-center text-[8px] font-bold text-white">
                            ${isWard ? '👁️' : '⚔️'}
                        </div>
                        <!-- Glassmorphic Tooltip Card -->
                        <div class="hidden group-hover:block absolute bottom-6 left-1/2 transform -translate-x-1/2 w-48 p-2.5 bg-slate-900/95 backdrop-blur-md rounded-xl border border-slate-700 shadow-2xl text-xs text-white z-50 pointer-events-none">
                            <div class="font-bold text-purple-300 flex items-center justify-between border-b border-slate-800 pb-1 mb-1">
                                <span>${p.FormattedTime}</span>
                                <span class="text-[10px] uppercase font-bold text-slate-400">${p.EventType}</span>
                            </div>
                            <p class="text-[11px] text-slate-200">${p.Description}</p>
                        </div>
                    `;
                    pinContainer.appendChild(pin);
                });
            }
        },

        setHeatmapFilter: function (filter) {
            this.activeHeatmapFilter = filter;
            document.querySelectorAll('.heatmap-filter-btn').forEach(btn => {
                if (btn.dataset.filter === filter) {
                    btn.classList.add('bg-purple-600', 'text-white');
                    btn.classList.remove('bg-slate-800', 'text-slate-400');
                } else {
                    btn.classList.remove('bg-purple-600', 'text-white');
                    btn.classList.add('bg-slate-800', 'text-slate-400');
                }
            });
            this.initHeatmapCanvas();
        },

        // =========================================================================
        // 3. 6-AXIS PLAYER PERFORMANCE RADAR CHART
        // =========================================================================
        initRadarChart: function () {
            const svg = document.getElementById('match-radar-svg');
            if (!svg || !this.telemetryData || this.telemetryData.length === 0) return;

            const p1 = this.telemetryData.find(p => p.AccountId == this.selectedRadarPlayer1) || this.telemetryData[0];
            const p2 = this.telemetryData.find(p => p.AccountId == this.selectedRadarPlayer2) || this.telemetryData[1];

            // Normalize metrics across all players in match
            const maxKda = Math.max(3, ...this.telemetryData.map(p => (p.HeroKills + p.HeroAssists) / Math.max(1, p.HeroDeaths)));
            const maxGpm = Math.max(400, ...this.telemetryData.map(p => p.Gpm));
            const maxXpm = Math.max(400, ...this.telemetryData.map(p => p.Xpm));
            const maxDmg = Math.max(15, ...this.telemetryData.map(p => p.DamageSharePercent));
            const maxCc = Math.max(10, ...this.telemetryData.map(p => p.CrowdControlDuration));
            const maxWards = Math.max(4, ...this.telemetryData.map(p => p.WardsPlaced));

            const calcKda = p => (p.HeroKills + p.HeroAssists) / Math.max(1, p.HeroDeaths);

            const axes = [
                {name: 'KDA Ratio', getVal: p => Math.min(1.0, calcKda(p) / maxKda)},
                {name: 'GPM (Farm)', getVal: p => Math.min(1.0, p.Gpm / maxGpm)},
                {name: 'XPM (Levels)', getVal: p => Math.min(1.0, p.Xpm / maxXpm)},
                {name: 'Damage %', getVal: p => Math.min(1.0, p.DamageSharePercent / maxDmg)},
                {name: 'CC / Utility', getVal: p => Math.min(1.0, p.CrowdControlDuration / maxCc)},
                {name: 'Vision / Wards', getVal: p => Math.min(1.0, p.WardsPlaced / maxWards)}
            ];

            const cx = 150, cy = 150, radius = 100;
            const numAxes = axes.length;

            let svgContent = '';

            // Draw Background Concentric Hexagons
            [0.25, 0.5, 0.75, 1.0].forEach(level => {
                let hexPoints = [];
                for (let i = 0; i < numAxes; i++) {
                    const angle = (Math.PI * 2 / numAxes) * i - Math.PI / 2;
                    const x = cx + Math.cos(angle) * (radius * level);
                    const y = cy + Math.sin(angle) * (radius * level);
                    hexPoints.push(`${x.toFixed(1)},${y.toFixed(1)}`);
                }
                svgContent += `<polygon points="${hexPoints.join(' ')}" fill="none" stroke="rgba(255, 255, 255, 0.1)" stroke-width="1"/>`;
            });

            // Draw Spokes & Labels
            for (let i = 0; i < numAxes; i++) {
                const angle = (Math.PI * 2 / numAxes) * i - Math.PI / 2;
                const x = cx + Math.cos(angle) * radius;
                const y = cy + Math.sin(angle) * radius;
                svgContent += `<line x1="${cx}" y1="${cy}" x2="${x.toFixed(1)}" y2="${y.toFixed(1)}" stroke="rgba(255, 255, 255, 0.12)" stroke-width="1"/>`;

                // Label
                const labelX = cx + Math.cos(angle) * (radius + 22);
                const labelY = cy + Math.sin(angle) * (radius + 16);
                svgContent += `<text x="${labelX.toFixed(1)}" y="${labelY.toFixed(1)}" fill="#94a3b8" font-size="9" font-weight="600" text-anchor="middle" alignment-baseline="middle">${axes[i].name}</text>`;
            }

            // Draw Player 1 Polygon (Cyan/Emerald)
            if (p1) {
                const p1Points = axes.map((axis, i) => {
                    const val = Math.max(0.1, axis.getVal(p1));
                    const angle = (Math.PI * 2 / numAxes) * i - Math.PI / 2;
                    return `${(cx + Math.cos(angle) * (radius * val)).toFixed(1)},${(cy + Math.sin(angle) * (radius * val)).toFixed(1)}`;
                }).join(' ');

                svgContent += `<polygon points="${p1Points}" fill="rgba(6, 182, 212, 0.35)" stroke="#06b6d4" stroke-width="2"/>`;
            }

            // Draw Player 2 Polygon (Rose/Crimson)
            if (p2) {
                const p2Points = axes.map((axis, i) => {
                    const val = Math.max(0.1, axis.getVal(p2));
                    const angle = (Math.PI * 2 / numAxes) * i - Math.PI / 2;
                    return `${(cx + Math.cos(angle) * (radius * val)).toFixed(1)},${(cy + Math.sin(angle) * (radius * val)).toFixed(1)}`;
                }).join(' ');

                svgContent += `<polygon points="${p2Points}" fill="rgba(244, 63, 94, 0.35)" stroke="#f43f5e" stroke-width="2"/>`;
            }

            svg.innerHTML = svgContent;

            // Update Player Select Legend Card Labels
            const p1Label = document.getElementById('radar-p1-label');
            const p2Label = document.getElementById('radar-p2-label');
            if (p1Label && p1) p1Label.textContent = `${p1.AccountName} (${p1.HeroDisplayName})`;
            if (p2Label && p2) p2Label.textContent = `${p2.AccountName} (${p2.HeroDisplayName})`;
        },

        // =========================================================================
        // 4. DAMAGE COMPOSITION BARS
        // =========================================================================
        initDamageBars: function () {
            const container = document.getElementById('match-damage-telemetry-container');
            if (!container || !this.telemetryData) return;

            const maxHeroDmg = Math.max(1, ...this.telemetryData.map(p => p.HeroDamage));

            container.innerHTML = this.telemetryData.map(p => {
                const pct = Math.round((p.HeroDamage / maxHeroDmg) * 100);
                const isLegion = p.Team === 1;
                const barColor = isLegion ? 'bg-gradient-to-r from-cyan-600 to-emerald-500' : 'bg-gradient-to-r from-rose-600 to-amber-500';

                return `
                    <div class="p-3 rounded-2xl bg-slate-900/60 border border-slate-800 space-y-1.5 hover:border-slate-700 transition-all">
                        <div class="flex items-center justify-between text-xs">
                            <div class="flex items-center gap-2">
                                <img src="/images/heroes/${(p.HeroIdentifier || 'default').toLowerCase().replace(/^icon_/, '').replace(/^hero_/, '').replace(/[^a-z0-9]/g, '') || 'default'}.png" class="w-6 h-6 rounded-lg object-cover border border-slate-700" onError="this.src='/images/heroes/default.png';"/>
                                <span class="font-bold text-white">${p.AccountName}</span>
                                <span class="text-slate-400 text-[11px]">(${p.HeroDisplayName})</span>
                            </div>
                            <div class="flex items-center gap-3 font-semibold text-slate-300">
                                <span>⚔️ ${p.HeroDamage.toLocaleString()} Dmg</span>
                                <span class="text-purple-400 font-bold">${p.DamageSharePercent}% Share</span>
                            </div>
                        </div>
                        <!-- Stacked Bar -->
                        <div class="w-full h-3 rounded-full bg-slate-950 overflow-hidden p-0.5 border border-slate-800 flex">
                            <div class="h-full rounded-full ${barColor}" style="width: ${pct}%"></div>
                        </div>
                        <div class="flex items-center justify-between text-[10px] text-slate-400 px-1">
                            <span>🛡️ Absorbed: ${Math.round(p.DamageTaken).toLocaleString()}</span>
                            <span>💫 CC Applied: ${p.CrowdControlDuration.toFixed(1)}s</span>
                            <span>💚 Healing: ${Math.round(p.HealingDone).toLocaleString()}</span>
                        </div>
                    </div>
                `;
            }).join('');
        },

        initEventListeners: function () {
            // Player Select dropdowns for radar comparison
            const sel1 = document.getElementById('radar-player1-select');
            const sel2 = document.getElementById('radar-player2-select');

            if (sel1) {
                sel1.addEventListener('change', e => {
                    this.selectedRadarPlayer1 = parseInt(e.target.value);
                    this.initRadarChart();
                });
            }
            if (sel2) {
                sel2.addEventListener('change', e => {
                    this.selectedRadarPlayer2 = parseInt(e.target.value);
                    this.initRadarChart();
                });
            }
        }
    };
})();
