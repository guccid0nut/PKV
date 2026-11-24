// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * DAWNBRINGER 1-Click Match Brag Card Generator (Esports Graphic Card for Discord/Twitter)
 * Renders a 1200x630 high-res card onto Canvas and supports 1-click clipboard copy & PNG download.
 */
(function () {
    'use strict';

    let cardCanvas = null;
    let cardCtx = null;

    function initCanvas() {
        if (!cardCanvas) {
            cardCanvas = document.createElement('canvas');
            cardCanvas.width = 1200;
            cardCanvas.height = 630;
            cardCtx = cardCanvas.getContext('2d');
        }
        return cardCtx;
    }

    const NexusBragCard = {
        generate: async function (data) {
            const ctx = initCanvas();
            const w = 1200;
            const h = 630;

            // 1. Dark Glass Background
            const bgGrad = ctx.createLinearGradient(0, 0, w, h);
            bgGrad.addColorStop(0, '#090d16');
            bgGrad.addColorStop(0.5, '#0f172a');
            bgGrad.addColorStop(1, '#050811');
            ctx.fillStyle = bgGrad;
            ctx.fillRect(0, 0, w, h);

            // Ambient Glows
            const glow1 = ctx.createRadialGradient(200, 150, 20, 200, 150, 400);
            glow1.addColorStop(0, data.isVictory ? 'rgba(16, 185, 129, 0.25)' : 'rgba(225, 29, 72, 0.25)');
            glow1.addColorStop(1, 'rgba(0,0,0,0)');
            ctx.fillStyle = glow1;
            ctx.fillRect(0, 0, w, h);

            const glow2 = ctx.createRadialGradient(1000, 450, 20, 1000, 450, 400);
            glow2.addColorStop(0, 'rgba(168, 85, 247, 0.2)');
            glow2.addColorStop(1, 'rgba(0,0,0,0)');
            ctx.fillStyle = glow2;
            ctx.fillRect(0, 0, w, h);

            // Outer Card Border
            ctx.strokeStyle = data.isVictory ? 'rgba(16, 185, 129, 0.4)' : 'rgba(225, 29, 72, 0.4)';
            ctx.lineWidth = 4;
            ctx.strokeRect(12, 12, w - 24, h - 24);

            // 2. Header Bar
            ctx.fillStyle = 'rgba(15, 23, 42, 0.85)';
            ctx.fillRect(20, 20, w - 40, 70);
            ctx.strokeStyle = 'rgba(255, 255, 255, 0.1)';
            ctx.lineWidth = 1;
            ctx.strokeRect(20, 20, w - 40, 70);

            // Brand Logo & Title
            ctx.fillStyle = '#f8fafc';
            ctx.font = '900 24px Outfit, sans-serif';
            ctx.textAlign = 'left';
            ctx.fillText('⚡ PROJECT KONGOR', 45, 62);

            ctx.fillStyle = '#94a3b8';
            ctx.font = '600 16px Outfit, sans-serif';
            ctx.fillText(`MATCH #${data.matchId || '000000'} • ${data.mapName || 'Forests of Caldavar'} • ${data.duration || '35:20'}`, 340, 62);

            // Match Result Banner (Top Right)
            const resX = w - 220;
            ctx.fillStyle = data.isVictory ? '#10b981' : '#f43f5e';
            ctx.font = '900 22px Outfit, sans-serif';
            ctx.textAlign = 'right';
            ctx.fillText(data.isVictory ? '🏆 VICTORY' : '💀 DEFEAT', w - 45, 62);

            // 3. Hero & Player Panel (Left Column)
            ctx.fillStyle = 'rgba(30, 41, 59, 0.6)';
            ctx.beginPath();
            ctx.roundRect(40, 115, 360, 425, 20);
            ctx.fill();
            ctx.strokeStyle = 'rgba(168, 85, 247, 0.3)';
            ctx.stroke();

            // Player Avatar / Hero Portrait box
            ctx.fillStyle = '#1e1b4b';
            ctx.beginPath();
            ctx.roundRect(65, 140, 110, 110, 16);
            ctx.fill();
            ctx.strokeStyle = '#a855f7';
            ctx.lineWidth = 3;
            ctx.stroke();

            ctx.fillStyle = '#fde047';
            ctx.font = '900 48px sans-serif';
            ctx.textAlign = 'center';
            ctx.fillText('⚔️', 120, 212);

            // Player Name & Title
            ctx.fillStyle = '#ffffff';
            ctx.font = '900 28px Outfit, sans-serif';
            ctx.textAlign = 'left';
            ctx.fillText(data.playerName || 'Newerth Hero', 195, 185);

            ctx.fillStyle = '#cbd5e1';
            ctx.font = '700 16px Outfit, sans-serif';
            ctx.fillText(data.heroName || 'Hero', 195, 215);

            // Title Badge
            if (data.title) {
                ctx.fillStyle = 'rgba(234, 179, 8, 0.2)';
                ctx.beginPath();
                ctx.roundRect(195, 228, 180, 28, 8);
                ctx.fill();
                ctx.strokeStyle = '#eab308';
                ctx.lineWidth = 1;
                ctx.stroke();

                ctx.fillStyle = '#fde047';
                ctx.font = '800 12px Outfit, sans-serif';
                ctx.fillText(`👑 ${data.title}`, 205, 247);
            }

            // Rank & MMR Delta Box
            ctx.fillStyle = 'rgba(15, 23, 42, 0.9)';
            ctx.beginPath();
            ctx.roundRect(65, 275, 310, 80, 14);
            ctx.fill();
            ctx.strokeStyle = 'rgba(255, 255, 255, 0.1)';
            ctx.stroke();

            ctx.fillStyle = '#94a3b8';
            ctx.font = '700 13px Outfit, sans-serif';
            ctx.textAlign = 'left';
            ctx.fillText('RANK / MMR', 85, 305);

            ctx.fillStyle = '#f8fafc';
            ctx.font = '900 22px Outfit, sans-serif';
            ctx.fillText(data.rankName || 'Immortal', 85, 335);

            const mmrDelta = data.mmrDelta || (data.isVictory ? '+25 MMR' : '-20 MMR');
            ctx.fillStyle = data.isVictory ? '#34d399' : '#f87171';
            ctx.font = '900 22px Outfit, sans-serif';
            ctx.textAlign = 'right';
            ctx.fillText(mmrDelta, 355, 335);

            // Role / MVP Badge
            ctx.fillStyle = data.isMvp ? 'rgba(234, 179, 8, 0.25)' : 'rgba(59, 130, 246, 0.2)';
            ctx.beginPath();
            ctx.roundRect(65, 375, 310, 60, 14);
            ctx.fill();
            ctx.strokeStyle = data.isMvp ? '#f59e0b' : '#3b82f6';
            ctx.stroke();

            ctx.fillStyle = data.isMvp ? '#fde047' : '#93c5fd';
            ctx.font = '900 18px Outfit, sans-serif';
            ctx.textAlign = 'center';
            ctx.fillText(data.isMvp ? '⭐ MATCH MVP' : `🎮 ${data.gameMode || 'Ranked Matchmaking'}`, 220, 412);

            // 4. Combat Stats Matrix (Right Column)
            ctx.fillStyle = 'rgba(30, 41, 59, 0.6)';
            ctx.beginPath();
            ctx.roundRect(430, 115, 730, 425, 20);
            ctx.fill();
            ctx.strokeStyle = 'rgba(255, 255, 255, 0.1)';
            ctx.stroke();

            // Stat Cards Grid (KDA, GPM, XPM, Damage)
            const stats = [
                {
                    label: 'KILLS / DEATHS / ASSISTS',
                    val: `${data.kills || 0} / ${data.deaths || 0} / ${data.assists || 0}`,
                    color: '#38bdf8'
                },
                {
                    label: 'KDR RATIO',
                    val: `${(data.kdr || ((data.kills || 1) / Math.max(1, data.deaths || 1))).toFixed(2)}`,
                    color: '#a855f7'
                },
                {label: 'GOLD PER MINUTE (GPM)', val: `${data.gpm || 520}`, color: '#f59e0b'},
                {label: 'EXP PER MINUTE (XPM)', val: `${data.xpm || 610}`, color: '#10b981'},
                {label: 'HERO DAMAGE', val: `${(data.damage || 24500).toLocaleString()}`, color: '#f43f5e'},
                {
                    label: 'WARDS PLACED / CREEP KILLS',
                    val: `${data.wards || 8} Wards • ${data.creeps || 185} CS`,
                    color: '#c084fc'
                }
            ];

            stats.forEach((st, idx) => {
                const row = Math.floor(idx / 2);
                const col = idx % 2;
                const sx = 460 + col * 340;
                const sy = 145 + row * 95;

                ctx.fillStyle = 'rgba(15, 23, 42, 0.8)';
                ctx.beginPath();
                ctx.roundRect(sx, sy, 320, 80, 14);
                ctx.fill();
                ctx.strokeStyle = 'rgba(255, 255, 255, 0.08)';
                ctx.stroke();

                ctx.fillStyle = '#94a3b8';
                ctx.font = '800 11px Outfit, sans-serif';
                ctx.textAlign = 'left';
                ctx.fillText(st.label, sx + 20, sy + 30);

                ctx.fillStyle = st.color;
                ctx.font = '900 24px Outfit, sans-serif';
                ctx.fillText(st.val, sx + 20, sy + 62);
            });

            // Items Tray at the bottom of Right Panel
            ctx.fillStyle = 'rgba(15, 23, 42, 0.9)';
            ctx.beginPath();
            ctx.roundRect(460, 440, 670, 75, 14);
            ctx.fill();

            ctx.fillStyle = '#94a3b8';
            ctx.font = '800 12px Outfit, sans-serif';
            ctx.textAlign = 'left';
            ctx.fillText('INVENTORY BUILD', 480, 482);

            const items = data.items || ['Frostwolf', 'Daemonic', 'Savage Mace', 'Geometer', 'Shrunken Head', 'Ghost Marchers'];
            items.slice(0, 6).forEach((it, idx) => {
                const ix = 640 + idx * 75;
                const iy = 450;
                ctx.fillStyle = '#1e293b';
                ctx.beginPath();
                ctx.roundRect(ix, iy, 55, 55, 10);
                ctx.fill();
                ctx.strokeStyle = '#475569';
                ctx.stroke();

                ctx.fillStyle = '#cbd5e1';
                ctx.font = '700 9px Outfit, sans-serif';
                ctx.textAlign = 'center';
                ctx.fillText((it || '').substring(0, 8), ix + 27, iy + 32);
            });

            // 5. Footer Watermark
            ctx.fillStyle = '#64748b';
            ctx.font = '700 14px Outfit, sans-serif';
            ctx.textAlign = 'center';
            ctx.fillText('PROJECT KONGOR • Official Web Portal • https://projectkongor.com', w / 2, 595);

            // Display in Preview Modal
            const previewImg = document.getElementById('bragCardPreviewImage');
            if (previewImg) {
                previewImg.src = cardCanvas.toDataURL('image/png');
            }

            const modal = document.getElementById('bragCardModal');
            if (modal) {
                modal.classList.remove('hidden');
                modal.classList.add('flex');
            }
        },

        copyToClipboard: async function () {
            if (!cardCanvas) return;
            const copyBtn = document.getElementById('copyBragCardBtn');
            const toast = document.getElementById('bragCardToast');

            try {
                cardCanvas.toBlob(async (blob) => {
                    if (blob && navigator.clipboard && navigator.clipboard.write) {
                        await navigator.clipboard.write([
                            new ClipboardItem({'image/png': blob})
                        ]);
                        if (toast) {
                            toast.innerText = '✓ Card image copied to clipboard! Paste directly into Discord!';
                            toast.classList.remove('opacity-0');
                            toast.classList.add('opacity-100');
                            setTimeout(() => toast.classList.remove('opacity-100'), 2500);
                        }
                    } else {
                        // Fallback download
                        NexusBragCard.downloadPng();
                    }
                }, 'image/png');
            } catch (err) {
                console.error('Clipboard copy failed', err);
                NexusBragCard.downloadPng();
            }
        },

        downloadPng: function () {
            if (!cardCanvas) return;
            const a = document.createElement('a');
            a.download = `project_kongor_match_card_${Date.now()}.png`;
            a.href = cardCanvas.toDataURL('image/png');
            a.click();
        },

        closeModal: function () {
            const modal = document.getElementById('bragCardModal');
            if (modal) {
                modal.classList.add('hidden');
                modal.classList.remove('flex');
            }
        }
    };

    window.NexusBragCard = NexusBragCard;

    document.addEventListener('DOMContentLoaded', () => {
        if (!document.getElementById('bragCardModal')) {
            const modalHtml = `
            <div id="bragCardModal" class="fixed inset-0 z-50 hidden items-center justify-center bg-slate-950/85 backdrop-blur-md p-4 transition-all animate-fadeIn">
                <div class="glass-panel w-full max-w-4xl rounded-3xl border border-purple-500/40 p-6 shadow-2xl bg-slate-900/95 space-y-5">
                    <!-- Modal Header -->
                    <div class="flex items-center justify-between border-b border-slate-800 pb-4">
                        <div class="flex items-center gap-3">
                            <div class="w-10 h-10 rounded-2xl bg-purple-500/20 border border-purple-500/40 flex items-center justify-center text-purple-400 text-xl shadow-inner">
                                ✨
                            </div>
                            <div>
                                <h3 class="text-xl font-black text-white tracking-tight">Match Brag Card Generator</h3>
                                <p class="text-xs text-slate-400">Share your match victory & stats on Discord, Twitter, or Reddit</p>
                            </div>
                        </div>
                        <button onclick="NexusBragCard.closeModal()" class="p-2 rounded-xl bg-slate-800 hover:bg-slate-700 text-slate-300 hover:text-white transition-all text-sm">
                            <i class="fas fa-times"></i>
                        </button>
                    </div>

                    <!-- Card Preview -->
                    <div class="rounded-2xl overflow-hidden border border-slate-700/60 shadow-2xl bg-slate-950">
                        <img id="bragCardPreviewImage" alt="Match Brag Card" class="w-full h-auto object-contain max-h-[420px] mx-auto"/>
                    </div>

                    <!-- Action Buttons -->
                    <div class="flex flex-wrap items-center justify-between gap-3 pt-2">
                        <div id="bragCardToast" class="text-xs font-bold text-emerald-400 opacity-0 transition-opacity duration-300">
                            ✓ Copied to clipboard!
                        </div>
                        <div class="flex items-center gap-3">
                            <button id="copyBragCardBtn" onclick="NexusBragCard.copyToClipboard()" 
                                    class="px-5 py-2.5 rounded-2xl bg-gradient-to-r from-purple-600 to-indigo-600 hover:from-purple-500 hover:to-indigo-500 text-white font-bold text-xs shadow-lg shadow-purple-600/30 flex items-center gap-2 transition-all">
                                <i class="fas fa-copy"></i>
                                <span>Copy Image (Discord)</span>
                            </button>
                            <button onclick="NexusBragCard.downloadPng()" 
                                    class="px-5 py-2.5 rounded-2xl bg-slate-800 hover:bg-slate-700 border border-slate-700 text-slate-200 font-bold text-xs flex items-center gap-2 transition-all">
                                <i class="fas fa-download"></i>
                                <span>Download PNG</span>
                            </button>
                        </div>
                    </div>
                </div>
            </div>
            `;
            document.body.insertAdjacentHTML('beforeend', modalHtml);
        }
    });
})();
