// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * DAWNBRINGER Web Portal - Slot Machine Username Banner & Winner Celebration
 * Spins through linked sub-accounts/alts every 10 seconds with casino reel animations
 * and triggers a celebratory golden jackpot burst when landing on the Main Account.
 */
(() => {
    'use strict';

    document.addEventListener('DOMContentLoaded', () => {
        const container = document.getElementById('slot-machine-username-container');
        if (!container) return;

        const textEl = document.getElementById('slot-username-text');
        const winnerBadge = document.getElementById('slot-winner-badge');
        const sparksContainer = document.getElementById('slot-winner-sparks');
        const accountsBadge = document.getElementById('slot-accounts-badge');

        let accounts = [];
        try {
            accounts = JSON.parse(container.getAttribute('data-accounts') || '[]');
        } catch (e) {
            console.error('Failed to parse linked accounts data', e);
            return;
        }

        if (!Array.isArray(accounts) || accounts.length <= 1) return;

        const mainAccount = (container.getAttribute('data-main-account') || '').trim();
        let currentAccount = (container.getAttribute('data-current-account') || accounts[0]).trim();
        let isSpinning = false;

        const GLYPHS = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_!$#';

        function getRandomGlyph() {
            return GLYPHS[Math.floor(Math.random() * GLYPHS.length)];
        }

        function createCelebrationSparks() {
            if (!sparksContainer) return;
            sparksContainer.innerHTML = '';

            const particles = ['✨', '⭐', '💫', '👑', '🎉', '✦', '◆'];
            const count = 18;

            for (let i = 0; i < count; i++) {
                const spark = document.createElement('span');
                const particle = particles[Math.floor(Math.random() * particles.length)];
                spark.textContent = particle;
                spark.className = 'slot-spark-particle';

                const angle = (Math.PI * 2 * i) / count + (Math.random() * 0.4 - 0.2);
                const distance = 40 + Math.random() * 65;
                const tx = Math.cos(angle) * distance;
                const ty = Math.sin(angle) * distance - 20;
                const rot = Math.random() * 360;
                const scale = 0.6 + Math.random() * 0.7;
                const duration = 1200 + Math.random() * 600;

                spark.style.setProperty('--tx', `${tx}px`);
                spark.style.setProperty('--ty', `${ty}px`);
                spark.style.setProperty('--rot', `${rot}deg`);
                spark.style.setProperty('--scale', `${scale}`);
                spark.style.animationDuration = `${duration}ms`;

                sparksContainer.appendChild(spark);

                setTimeout(() => {
                    spark.remove();
                }, duration + 100);
            }
        }

        function spinTo(targetName) {
            if (isSpinning) return;
            isSpinning = true;

            // Reset winner badge
            if (winnerBadge) {
                winnerBadge.classList.remove('opacity-100', 'scale-100', 'translate-y-0');
                winnerBadge.classList.add('opacity-0', 'scale-50', '-translate-y-2');
            }
            container.classList.remove('slot-winner-glow', 'slot-alt-glow');

            const sourceName = currentAccount;
            const maxLen = Math.max(sourceName.length, targetName.length);
            const targetPadded = targetName.padEnd(maxLen, ' ');

            // Setup characters
            let displayChars = Array.from(sourceName.padEnd(maxLen, ' '));
            textEl.textContent = displayChars.join('').trim();
            textEl.classList.add('slot-spinning');

            const startTime = performance.now();
            const totalDuration = 1800; // Total spin time (ms)
            const reelStagger = 120; // Stagger per character

            const interval = setInterval(() => {
                const elapsed = performance.now() - startTime;
                let allDone = true;

                for (let i = 0; i < maxLen; i++) {
                    const stopTime = 700 + (i * reelStagger);
                    if (elapsed < stopTime) {
                        allDone = false;
                        displayChars[i] = getRandomGlyph();
                    } else {
                        displayChars[i] = targetPadded[i];
                    }
                }

                textEl.textContent = displayChars.join('').trim();

                if (allDone || elapsed >= totalDuration + (maxLen * reelStagger)) {
                    clearInterval(interval);
                    textEl.textContent = targetName;
                    textEl.classList.remove('slot-spinning');
                    currentAccount = targetName;
                    isSpinning = false;

                    const isMain = targetName.toLowerCase() === mainAccount.toLowerCase();

                    if (isMain) {
                        // WINNER CELEBRATION!
                        container.classList.add('slot-winner-glow');
                        if (winnerBadge) {
                            winnerBadge.classList.remove('opacity-0', 'scale-50', '-translate-y-2');
                            winnerBadge.classList.add('opacity-100', 'scale-100', 'translate-y-0');
                        }
                        createCelebrationSparks();

                        // Fade badge after 4.5 seconds
                        setTimeout(() => {
                            if (winnerBadge && !isSpinning) {
                                winnerBadge.classList.remove('opacity-100', 'scale-100', 'translate-y-0');
                                winnerBadge.classList.add('opacity-0', 'scale-50', '-translate-y-2');
                            }
                            container.classList.remove('slot-winner-glow');
                        }, 4500);
                    } else {
                        // Alt account lock-in flash
                        container.classList.add('slot-alt-glow');
                        setTimeout(() => {
                            container.classList.remove('slot-alt-glow');
                        }, 1800);
                    }
                }
            }, 45);
        }

        // Spin every 10 seconds
        const SPIN_INTERVAL = 10000;
        setInterval(() => {
            if (isSpinning) return;

            // Pick a random account different from the current one
            const otherAccounts = accounts.filter(a => a.toLowerCase() !== currentAccount.toLowerCase());
            const pool = otherAccounts.length > 0 ? otherAccounts : accounts;
            const nextAccount = pool[Math.floor(Math.random() * pool.length)];

            spinTo(nextAccount);
        }, SPIN_INTERVAL);
    });
})();
