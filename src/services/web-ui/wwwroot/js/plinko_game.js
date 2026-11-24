// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * DAWNBRINGER Goblin Plinko 2D Physics Engine
 * Authentic Heroes of Newerth Plinko Arcade simulation.
 * Staggered uniform peg grid, weighted physical bouncing, coin rotation,
 * ghost motion trail, Web Audio pin synth, and 3D treasure chest buckets.
 */
(function () {
    'use strict';

    const SIDE_PADDING = 36;
    const NUM_ROWS = 11;
    const NUM_COLS = 10;
    const TICKET_COST = 100;
    const GOLD_COST = 50;

    let canvas = null;
    let ctx = null;
    let isDropping = false;
    let selectedChute = 2; // Default center chute (0..4)

    let plinkoState = {
        canDaily: false,
        secondsUntilDaily: 0,
        tickets: 0,
        gold: 0,
        silver: 0,
        ticketCost: TICKET_COST,
        goldCost: GOLD_COST,
        unlockedBorders: [],
        unlockedTitles: [],
        equippedBorder: null,
        equippedTitle: null
    };

    // 9 Bottom Slots matching upgraded prize configuration
    const BUCKETS = [
        {
            slot: 0,
            label: '100',
            icon: '🥈',
            title: '100 Silver',
            color: '#94a3b8',
            bg: 'rgba(148, 163, 184, 0.15)',
            glow: 'rgba(148, 163, 184, 0.4)'
        },
        {
            slot: 1,
            label: '75 GC',
            icon: '🪙',
            title: '75 Gold Coins',
            color: '#eab308',
            bg: 'rgba(234, 179, 8, 0.18)',
            glow: 'rgba(234, 179, 8, 0.45)'
        },
        {
            slot: 2,
            label: '300x',
            icon: '🎟️',
            title: '300 Tickets',
            color: '#a855f7',
            bg: 'rgba(168, 85, 247, 0.2)',
            glow: 'rgba(168, 85, 247, 0.5)'
        },
        {
            slot: 3,
            label: 'TAUNT',
            icon: '🎭',
            title: 'In-Game Taunts & Announcers',
            color: '#38bdf8',
            bg: 'rgba(56, 189, 248, 0.2)',
            glow: 'rgba(56, 189, 248, 0.5)'
        },
        {
            slot: 4,
            label: 'JACKPOT',
            icon: '👑',
            title: "Kongor's Trove",
            color: '#f59e0b',
            bg: 'rgba(245, 158, 11, 0.25)',
            glow: 'rgba(245, 158, 11, 0.7)',
            isJackpot: true
        },
        {
            slot: 5,
            label: '250 GC',
            icon: '🪙',
            title: '250 Gold Coins',
            color: '#eab308',
            bg: 'rgba(234, 179, 8, 0.22)',
            glow: 'rgba(234, 179, 8, 0.55)'
        },
        {
            slot: 6,
            label: 'AVATAR',
            icon: '🦸',
            title: 'In-Game Alt Avatars',
            color: '#ec4899',
            bg: 'rgba(236, 72, 153, 0.2)',
            glow: 'rgba(236, 72, 153, 0.5)'
        },
        {
            slot: 7,
            label: '75 GC',
            icon: '🪙',
            title: '75 Gold Coins',
            color: '#eab308',
            bg: 'rgba(234, 179, 8, 0.18)',
            glow: 'rgba(234, 179, 8, 0.45)'
        },
        {
            slot: 8,
            label: '100',
            icon: '🥈',
            title: '100 Silver',
            color: '#94a3b8',
            bg: 'rgba(148, 163, 184, 0.15)',
            glow: 'rgba(148, 163, 184, 0.4)'
        }
    ];

    let pegs = [];
    let sideBumpers = [];
    let activeBall = null;
    let particles = [];
    let bucketBounce = new Array(BUCKETS.length).fill(0);
    let animationFrameId = null;
    let lastFrameTime = performance.now();
    let isPlinkoInView = !('IntersectionObserver' in window);
    let cachedPlinkoRect = null;

    function ensurePlinkoLoop() {
        if (animationFrameId !== null || document.hidden || !canvas || !ctx) return;
        if (!isPlinkoInView && !activeBall && !isDropping && particles.length === 0) return;
        lastFrameTime = performance.now();
        animationFrameId = requestAnimationFrame(renderPlinko);
    }

    // Top Marquee Bulbs (HoN bulbAnimations emulation)
    let bulbStep = 0;
    let bulbTimer = 0;
    const bulbColors = ['#f59e0b', '#ef4444', '#3b82f6', '#10b981', '#a855f7', '#f59e0b', '#ef4444', '#3b82f6'];

    // Web Audio Synthesizer for 0-latency physical metallic peg clinks
    let audioCtx = null;

    function playPegClink(pitchFactor = 1.0) {
        try {
            if (!audioCtx) {
                const AudioContextClass = window.AudioContext || window.webkitAudioContext;
                if (AudioContextClass) {
                    audioCtx = new AudioContextClass();
                }
            }
            if (!audioCtx) return;
            if (audioCtx.state === 'suspended') {
                audioCtx.resume();
            }

            const now = audioCtx.currentTime;
            const osc = audioCtx.createOscillator();
            const gain = audioCtx.createGain();

            // High metallic sine/triangle clink with fast decay
            osc.type = 'triangle';
            const baseFreq = 1800 + Math.random() * 600;
            osc.frequency.setValueAtTime(baseFreq * pitchFactor, now);
            osc.frequency.exponentialRampToValueAtTime(baseFreq * pitchFactor * 0.5, now + 0.045);

            gain.gain.setValueAtTime(0.22, now);
            gain.gain.exponentialRampToValueAtTime(0.001, now + 0.045);

            osc.connect(gain);
            gain.connect(audioCtx.destination);

            osc.start(now);
            osc.stop(now + 0.048);
        } catch (e) {
            // Audio context not allowed without interaction yet
        }
    }

    /**
     * Builds the rectangular staggered peg grid matching HoN Plinko
     */
    function initPegs(width, height) {
        pegs = [];
        sideBumpers = [];

        const startY = 76;
        const endY = height - 92;
        const dy = (endY - startY) / (NUM_ROWS - 1);
        const colSpacing = (width - SIDE_PADDING * 2) / NUM_COLS;

        for (let r = 0; r < NUM_ROWS; r++) {
            const y = startY + r * dy;
            const isStaggered = (r % 2 === 1);

            if (isStaggered) {
                // Odd rows: 10 pegs centered between even row pegs
                for (let c = 0; c < NUM_COLS; c++) {
                    const x = SIDE_PADDING + colSpacing / 2 + c * colSpacing;
                    pegs.push({
                        x: x,
                        y: y,
                        radius: 4.8,
                        hitGlow: 0,
                        wobble: 0
                    });
                }
            } else {
                // Even rows: 11 pegs spanning the field
                for (let c = 0; c <= NUM_COLS; c++) {
                    const x = SIDE_PADDING + c * colSpacing;
                    pegs.push({
                        x: x,
                        y: y,
                        radius: 4.8,
                        hitGlow: 0,
                        wobble: 0
                    });
                }
            }

            // Left & Right side guide bumpers to deflect balls back into the board
            if (r > 0 && r < NUM_ROWS - 1) {
                sideBumpers.push({
                    x: SIDE_PADDING * 0.45,
                    y: y + dy * 0.5,
                    radius: 6,
                    isLeft: true
                });
                sideBumpers.push({
                    x: width - SIDE_PADDING * 0.45,
                    y: y + dy * 0.5,
                    radius: 6,
                    isLeft: false
                });
            }
        }
    }

    /**
     * Main 60FPS Plinko Animation & Rendering Loop
     */
    function renderPlinko(now) {
        if (!ctx || !canvas) return;

        const dt = Math.min((now - lastFrameTime) / 1000, 0.033) || 0.016;
        lastFrameTime = now;

        const w = canvas.width;
        const h = canvas.height;

        ctx.clearRect(0, 0, w, h);

        // 1. Draw Board Background (Dark Mahogany Tavern / Obsidian with Wood Planks)
        const bgGrad = ctx.createLinearGradient(0, 0, 0, h);
        bgGrad.addColorStop(0, '#090d16');
        bgGrad.addColorStop(0.5, '#0d1527');
        bgGrad.addColorStop(1, '#080c14');
        ctx.fillStyle = bgGrad;
        ctx.fillRect(0, 0, w, h);

        // Subtle horizontal wood planks
        ctx.strokeStyle = 'rgba(255, 255, 255, 0.025)';
        ctx.lineWidth = 1;
        for (let py = 30; py < h; py += 38) {
            ctx.beginPath();
            ctx.moveTo(0, py);
            ctx.lineTo(w, py);
            ctx.stroke();
        }

        // Ambient Radial Spotlight from center
        const radialGlow = ctx.createRadialGradient(w / 2, h * 0.45, 30, w / 2, h * 0.45, w * 0.65);
        radialGlow.addColorStop(0, 'rgba(245, 158, 11, 0.06)');
        radialGlow.addColorStop(0.6, 'rgba(168, 85, 247, 0.03)');
        radialGlow.addColorStop(1, 'rgba(0, 0, 0, 0)');
        ctx.fillStyle = radialGlow;
        ctx.fillRect(0, 0, w, h);

        // 2. Draw Top Marquee Carnival Bulbs (Animated Chasing Lights)
        bulbTimer += dt;
        if (bulbTimer > 0.16) {
            bulbTimer = 0;
            bulbStep = (bulbStep + 1) % 8;
        }

        const numBulbs = 12;
        const bulbY = 22;
        const bulbSpacing = w / (numBulbs + 1);
        for (let i = 1; i <= numBulbs; i++) {
            const bx = i * bulbSpacing;
            const isOn = (i + bulbStep) % 2 === 0;
            const color = bulbColors[i % bulbColors.length];

            ctx.beginPath();
            ctx.arc(bx, bulbY, isOn ? 5.5 : 4, 0, Math.PI * 2);
            ctx.fillStyle = isOn ? color : 'rgba(71, 85, 105, 0.4)';
            ctx.fill();

            if (isOn) {
                ctx.beginPath();
                ctx.arc(bx, bulbY, 11, 0, Math.PI * 2);
                ctx.fillStyle = color === '#f59e0b' ? 'rgba(245, 158, 11, 0.3)' : 'rgba(59, 130, 246, 0.3)';
                ctx.fill();
            }
        }

        // 3. Draw Top Drop Chute Selectors (5 Funnel Positions)
        const numChutes = 5;
        const chuteY = 48;
        const chuteSpacing = (w - 120) / (numChutes - 1);
        for (let i = 0; i < numChutes; i++) {
            const cx = 60 + i * chuteSpacing;
            const isHovered = (i === selectedChute);

            ctx.save();
            ctx.translate(cx, chuteY);
            ctx.beginPath();
            ctx.moveTo(-10, -8);
            ctx.lineTo(10, -8);
            ctx.lineTo(0, 4);
            ctx.closePath();
            ctx.fillStyle = isHovered ? '#f59e0b' : 'rgba(148, 163, 184, 0.3)';
            ctx.fill();

            if (isHovered) {
                ctx.strokeStyle = '#fef08a';
                ctx.lineWidth = 1.5;
                ctx.stroke();
            }
            ctx.restore();
        }

        // 4. Draw Side Guide Bumpers
        sideBumpers.forEach(b => {
            ctx.beginPath();
            ctx.arc(b.x, b.y, b.radius, 0, Math.PI * 2);
            ctx.fillStyle = '#1e293b';
            ctx.fill();
            ctx.strokeStyle = '#475569';
            ctx.lineWidth = 1.5;
            ctx.stroke();
        });

        // 5. Draw 9 Bottom Treasure Chest Buckets
        const bucketWidth = w / BUCKETS.length;
        const bucketHeight = 76;
        const bucketY = h - bucketHeight - 4;

        BUCKETS.forEach((b, idx) => {
            const bx = idx * bucketWidth;
            const bounce = bucketBounce[idx] || 0;
            const scale = 1 + bounce * 0.15;

            ctx.save();
            ctx.translate(bx + bucketWidth / 2, bucketY + bucketHeight / 2);
            ctx.scale(scale, scale);
            ctx.translate(-(bx + bucketWidth / 2), -(bucketY + bucketHeight / 2));

            // Bucket Container with Chest Shading
            const bGrad = ctx.createLinearGradient(bx, bucketY, bx, bucketY + bucketHeight);
            bGrad.addColorStop(0, b.isJackpot ? 'rgba(245, 158, 11, 0.35)' : b.bg);
            bGrad.addColorStop(1, b.isJackpot ? 'rgba(180, 83, 9, 0.45)' : 'rgba(15, 23, 42, 0.85)');

            ctx.fillStyle = bGrad;
            ctx.beginPath();
            ctx.roundRect(bx + 3, bucketY, bucketWidth - 6, bucketHeight, 10);
            ctx.fill();

            ctx.strokeStyle = b.isJackpot ? '#fbbf24' : b.color;
            ctx.lineWidth = b.isJackpot ? 2 : 1;
            ctx.stroke();

            // Jackpot Radiant Rays Effect
            if (b.isJackpot) {
                ctx.save();
                ctx.beginPath();
                ctx.roundRect(bx + 3, bucketY, bucketWidth - 6, bucketHeight, 10);
                ctx.clip();
                const rayTime = now * 0.002;
                ctx.strokeStyle = 'rgba(254, 240, 138, 0.15)';
                ctx.lineWidth = 2;
                for (let a = 0; a < Math.PI * 2; a += Math.PI / 4) {
                    ctx.beginPath();
                    ctx.moveTo(bx + bucketWidth / 2, bucketY + 38);
                    ctx.lineTo(
                        bx + bucketWidth / 2 + Math.cos(a + rayTime) * 60,
                        bucketY + 38 + Math.sin(a + rayTime) * 60
                    );
                    ctx.stroke();
                }
                ctx.restore();
            }

            // Chest Metal Rivets & Trim
            ctx.fillStyle = '#64748b';
            ctx.beginPath();
            ctx.arc(bx + 8, bucketY + 8, 2, 0, Math.PI * 2);
            ctx.arc(bx + bucketWidth - 8, bucketY + 8, 2, 0, Math.PI * 2);
            ctx.fill();

            // Bucket Prize Text & Icon
            ctx.fillStyle = b.isJackpot ? '#fef08a' : '#ffffff';
            ctx.font = b.isJackpot ? 'bold 12px Outfit, sans-serif' : 'bold 11px Outfit, sans-serif';
            ctx.textAlign = 'center';
            ctx.fillText(b.label, bx + bucketWidth / 2, bucketY + 30);

            ctx.font = b.isJackpot ? '18px sans-serif' : '15px sans-serif';
            ctx.fillText(b.icon, bx + bucketWidth / 2, bucketY + 54);

            ctx.restore();

            // Decay bounce animation
            if (bucketBounce[idx] > 0) {
                bucketBounce[idx] = Math.max(0, bucketBounce[idx] - dt * 4);
            }
        });

        // Vertical Dividers between buckets
        for (let i = 1; i < BUCKETS.length; i++) {
            const dx = i * bucketWidth;
            ctx.beginPath();
            ctx.moveTo(dx, bucketY - 8);
            ctx.lineTo(dx, bucketY + bucketHeight);
            ctx.strokeStyle = '#475569';
            ctx.lineWidth = 2;
            ctx.stroke();

            // Brass cap on divider
            ctx.beginPath();
            ctx.arc(dx, bucketY - 8, 3.5, 0, Math.PI * 2);
            ctx.fillStyle = '#f59e0b';
            ctx.fill();
        }

        // 6. Draw Pegs with 3D Metallic Brass Styling & Glowing Collision Rings
        pegs.forEach(peg => {
            // Collision Glow Shockwave
            if (peg.hitGlow > 0) {
                ctx.beginPath();
                ctx.arc(peg.x, peg.y, peg.radius + (1 - peg.hitGlow) * 16, 0, Math.PI * 2);
                ctx.fillStyle = `rgba(245, 158, 11, ${peg.hitGlow * 0.6})`;
                ctx.fill();

                ctx.strokeStyle = `rgba(254, 240, 138, ${peg.hitGlow * 0.8})`;
                ctx.lineWidth = 1.5;
                ctx.stroke();

                peg.hitGlow = Math.max(0, peg.hitGlow - dt * 3.5);
            }

            // Peg Drop Shadow
            ctx.beginPath();
            ctx.arc(peg.x + 1.2, peg.y + 1.5, peg.radius, 0, Math.PI * 2);
            ctx.fillStyle = 'rgba(0, 0, 0, 0.6)';
            ctx.fill();

            // 3D Brass Outer Rim
            const pegGrad = ctx.createRadialGradient(peg.x - 1.5, peg.y - 1.5, 1, peg.x, peg.y, peg.radius);
            pegGrad.addColorStop(0, '#fef08a');
            pegGrad.addColorStop(0.5, '#eab308');
            pegGrad.addColorStop(1, '#713f12');

            ctx.beginPath();
            ctx.arc(peg.x, peg.y, peg.radius, 0, Math.PI * 2);
            ctx.fillStyle = pegGrad;
            ctx.fill();

            ctx.strokeStyle = '#ca8a04';
            ctx.lineWidth = 0.8;
            ctx.stroke();

            // Center Pin Highlight
            ctx.beginPath();
            ctx.arc(peg.x - 1, peg.y - 1, 1.2, 0, Math.PI * 2);
            ctx.fillStyle = '#ffffff';
            ctx.fill();
        });

        // 7. Update & Render Active Ball (HoN Puck with Physics, Ghost Trail, and Rotation)
        if (activeBall) {
            // Store motion history for 3-step Ghost Trail (HoN puck_trail_1..3 emulation)
            activeBall.history.unshift({x: activeBall.x, y: activeBall.y, angle: activeBall.angle});
            if (activeBall.history.length > 5) {
                activeBall.history.pop();
            }

            // Draw Ghost Motion Trails
            activeBall.history.forEach((pos, idx) => {
                if (idx === 0) return;
                const alpha = (1 - idx / activeBall.history.length) * 0.35;
                ctx.save();
                ctx.translate(pos.x, pos.y);
                ctx.beginPath();
                ctx.arc(0, 0, activeBall.radius * (1 - idx * 0.08), 0, Math.PI * 2);
                ctx.fillStyle = `rgba(245, 158, 11, ${alpha})`;
                ctx.fill();
                ctx.restore();
            });

            // Physics Integration (Smooth Gravity & Natural Velocity)
            const gravity = 460; // px/s^2
            activeBall.vy += gravity * dt;

            // Terminal velocity clamping
            activeBall.vy = Math.min(320, activeBall.vy);

            // Progressive steering toward target winning slot
            if (activeBall.targetX !== undefined) {
                const progress = Math.max(0, Math.min(1, (activeBall.y - 70) / (bucketY - 70)));
                const steerStrength = 1.6 + progress * 7.0;
                const targetDelta = activeBall.targetX - activeBall.x;
                activeBall.vx += targetDelta * steerStrength * dt;
            }

            activeBall.vx = Math.max(-190, Math.min(190, activeBall.vx));
            activeBall.vx *= 0.995; // Air resistance

            // Move position
            activeBall.x += activeBall.vx * dt;
            activeBall.y += activeBall.vy * dt;

            // Coin spin rotation based on horizontal momentum
            activeBall.angle += activeBall.vRot * dt;
            activeBall.vRot *= 0.99;

            // Wall Collision & Rebound
            const leftWall = SIDE_PADDING * 0.7 + activeBall.radius;
            const rightWall = w - SIDE_PADDING * 0.7 - activeBall.radius;
            if (activeBall.x < leftWall) {
                activeBall.x = leftWall;
                activeBall.vx = Math.abs(activeBall.vx) * 0.6 + 15;
                activeBall.vRot = Math.random() * 4 + 2;
                playPegClink(0.9);
            } else if (activeBall.x > rightWall) {
                activeBall.x = rightWall;
                activeBall.vx = -Math.abs(activeBall.vx) * 0.6 - 15;
                activeBall.vRot = -(Math.random() * 4 + 2);
                playPegClink(0.9);
            }

            // Side Bumpers Collision
            sideBumpers.forEach(b => {
                const dx = activeBall.x - b.x;
                const dy = activeBall.y - b.y;
                const dist = Math.hypot(dx, dy);
                if (dist < activeBall.radius + b.radius) {
                    const nx = dx / dist;
                    const ny = dy / dist;
                    activeBall.vx = (b.isLeft ? 1 : -1) * (Math.abs(activeBall.vx) * 0.75 + 40);
                    activeBall.vy = Math.abs(activeBall.vy) * 0.5 + 20;
                    activeBall.x = b.x + nx * (activeBall.radius + b.radius + 1);
                    activeBall.y = b.y + ny * (activeBall.radius + b.radius + 1);
                    playPegClink(1.1);
                }
            });

            // Peg Collisions Resolution
            pegs.forEach(peg => {
                const dx = activeBall.x - peg.x;
                const dy = activeBall.y - peg.y;
                const dist = Math.hypot(dx, dy);

                if (dist < activeBall.radius + peg.radius) {
                    const nx = dx / (dist || 1);
                    const ny = dy / (dist || 1);

                    // Compute relative velocity along collision normal
                    const vn = activeBall.vx * nx + activeBall.vy * ny;

                    if (vn < 0) { // Moving towards peg
                        const restitution = 0.58; // Elasticity
                        const impulse = -(1 + restitution) * vn;

                        // Slight randomized lateral bias on near head-on hits
                        let lateralKick = 0;
                        if (Math.abs(nx) < 0.2) {
                            lateralKick = (Math.random() > 0.5 ? 1 : -1) * (20 + Math.random() * 26);
                        }

                        activeBall.vx = activeBall.vx + impulse * nx + lateralKick;
                        activeBall.vy = Math.max(25, activeBall.vy + impulse * ny);

                        // Separate overlapping position
                        activeBall.x = peg.x + nx * (activeBall.radius + peg.radius + 0.5);
                        activeBall.y = peg.y + ny * (activeBall.radius + peg.radius + 0.5);

                        // Trigger peg visual & acoustic effects
                        peg.hitGlow = 1.0;
                        activeBall.vRot = (activeBall.vx / activeBall.radius) * 3.0 + (Math.random() - 0.5) * 4;

                        const depthPitch = 0.85 + (activeBall.y / h) * 0.45;
                        playPegClink(depthPitch);
                    }
                }
            });

            // Render 3D Gold Coin (Puck)
            ctx.save();
            ctx.translate(activeBall.x, activeBall.y);
            ctx.rotate(activeBall.angle);

            // Coin Shadow
            ctx.beginPath();
            ctx.arc(2, 3, activeBall.radius, 0, Math.PI * 2);
            ctx.fillStyle = 'rgba(0, 0, 0, 0.5)';
            ctx.fill();

            // Outer Milled Golden Rim
            const coinRim = ctx.createLinearGradient(-activeBall.radius, -activeBall.radius, activeBall.radius, activeBall.radius);
            coinRim.addColorStop(0, '#fef08a');
            coinRim.addColorStop(0.3, '#f59e0b');
            coinRim.addColorStop(0.7, '#d97706');
            coinRim.addColorStop(1, '#78350f');

            ctx.beginPath();
            ctx.arc(0, 0, activeBall.radius, 0, Math.PI * 2);
            ctx.fillStyle = coinRim;
            ctx.fill();

            ctx.strokeStyle = '#fef08a';
            ctx.lineWidth = 1.5;
            ctx.stroke();

            // Inner Medallion Face
            ctx.beginPath();
            ctx.arc(0, 0, activeBall.radius - 2.8, 0, Math.PI * 2);
            ctx.fillStyle = '#b45309';
            ctx.fill();

            // Embossed HoN / Kongor Crest Icon
            ctx.fillStyle = '#fde047';
            ctx.font = 'bold 11px sans-serif';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            ctx.fillText('⚡', 0, 0.5);

            ctx.restore();

            // Check if Ball Landed in Bottom Bucket (Guaranteed Visual Synchronization)
            if (activeBall.y >= bucketY + 14) {
                const landedSlot = activeBall.dropResult.slotIndex;
                bucketBounce[landedSlot] = 1.0;
                createConfetti(activeBall.targetX, bucketY + 15, BUCKETS[landedSlot].isJackpot);
                onBallLanded(activeBall.dropResult, landedSlot);
                activeBall = null;
            }
        }

        // 8. Update & Draw Confetti & Particle Sparks
        particles.forEach((p, idx) => {
            p.x += p.vx * dt * 60;
            p.y += p.vy * dt * 60;
            p.vy += 0.16; // Particle gravity
            p.alpha -= dt * 0.85;
            p.rotation += p.vRot;

            ctx.save();
            ctx.translate(p.x, p.y);
            ctx.rotate(p.rotation);
            ctx.fillStyle = p.color;
            ctx.globalAlpha = Math.max(0, p.alpha);

            if (p.isSparkle) {
                ctx.beginPath();
                ctx.arc(0, 0, p.size / 2, 0, Math.PI * 2);
                ctx.fill();
            } else {
                ctx.fillRect(-p.size / 2, -p.size / 2, p.size, p.size);
            }
            ctx.restore();

            if (p.alpha <= 0) {
                particles.splice(idx, 1);
            }
        });

        if (!document.hidden && (isPlinkoInView || activeBall || isDropping || particles.length > 0)) {
            animationFrameId = requestAnimationFrame(renderPlinko);
        } else {
            animationFrameId = null;
        }
    }

    /**
     * Confetti & Sparkles Burst
     */
    function createConfetti(x, y, isJackpot) {
        const colors = isJackpot
            ? ['#f59e0b', '#fbbf24', '#fef08a', '#ec4899', '#a855f7', '#ffffff']
            : ['#38bdf8', '#60a5fa', '#a855f7', '#10b981', '#f59e0b', '#ffffff'];

        const count = isJackpot ? 75 : 45;
        for (let i = 0; i < count; i++) {
            particles.push({
                x: x,
                y: y,
                vx: (Math.random() - 0.5) * (isJackpot ? 11 : 7),
                vy: -Math.random() * (isJackpot ? 10 : 7) - 2.5,
                size: Math.random() * (isJackpot ? 7 : 5) + 3,
                color: colors[Math.floor(Math.random() * colors.length)],
                alpha: 1.0,
                rotation: Math.random() * Math.PI,
                vRot: (Math.random() - 0.5) * 0.25,
                isSparkle: Math.random() > 0.5
            });
        }
    }

    /**
     * Handles Prize Award & Victory Fanfare
     */
    function onBallLanded(result, landedSlot) {
        isDropping = false;
        updatePlinkoUI();

        if (window.NexusSoundboard) {
            window.NexusSoundboard.play(result.isJackpot ? 'fabulous' : 'gold_coins');
        }

        // Display Winner Modal
        const resultModal = document.getElementById('plinkoResultModal');
        const prizeTitle = document.getElementById('plinkoPrizeTitle');
        const prizeDesc = document.getElementById('plinkoPrizeDesc');

        if (resultModal && prizeTitle && prizeDesc) {
            prizeTitle.innerText = result.isJackpot ? '🎉 LEGENDARY JACKPOT! 🎉' : '🎁 Prize Claimed!';
            prizeDesc.innerHTML = `<span class="text-amber-300 font-bold text-base">${result.prizeName}</span>`;
            resultModal.classList.remove('hidden');
            resultModal.classList.add('flex');
        }

        // Refresh live currency & tickets
        fetchPlinkoStatus();
    }

    /**
     * Fetches current Plinko user status, tickets, and cooldown
     */
    async function fetchPlinkoStatus() {
        try {
            const res = await fetch('/api/plinko/status');
            if (res.ok) {
                plinkoState = await res.json();
                updatePlinkoUI();
            }
        } catch (err) {
            console.error('Failed to fetch Plinko status', err);
        }
    }

    /**
     * Updates the DOM state based on plinkoState
     */
    function updatePlinkoUI() {
        const dailyBtn = document.getElementById('plinkoDailyBtn');
        const ticketBtn = document.getElementById('plinkoTicketBtn');
        const goldBtn = document.getElementById('plinkoGoldBtn');
        const ticketCountEl = document.getElementById('plinkoTicketCount');
        const timerEl = document.getElementById('plinkoDailyTimer');
        const userGoldEl = document.getElementById('userGoldDisplay');
        const userSilverEl = document.getElementById('userSilverDisplay');

        const userTickets = plinkoState.plinkoTickets ?? plinkoState.tickets ?? 0;
        const userGold = plinkoState.goldCoins ?? plinkoState.gold ?? 0;
        const userSilver = plinkoState.silverCoins ?? plinkoState.silver ?? 0;
        const ticketCost = plinkoState.ticketCost ?? TICKET_COST;
        const goldCost = plinkoState.goldCost ?? GOLD_COST;

        if (ticketCountEl) ticketCountEl.innerText = userTickets.toLocaleString();
        if (userGoldEl) userGoldEl.innerText = userGold.toLocaleString();
        if (userSilverEl) userSilverEl.innerText = userSilver.toLocaleString();

        if (dailyBtn && timerEl) {
            if (plinkoState.canSpinDaily) {
                dailyBtn.disabled = isDropping;
                dailyBtn.classList.remove('opacity-50', 'cursor-not-allowed');
                timerEl.innerText = '✨ READY TO DROP!';
                timerEl.className = 'text-xs font-bold text-emerald-400 animate-pulse';
            } else {
                dailyBtn.disabled = true;
                dailyBtn.classList.add('opacity-50', 'cursor-not-allowed');
                formatCountdown(plinkoState.secondsUntilNextDailySpin || plinkoState.secondsUntilDaily || 0);
            }
        }

        if (ticketBtn) {
            ticketBtn.disabled = isDropping || userTickets < ticketCost;
            if (ticketBtn.disabled) {
                ticketBtn.classList.add('opacity-50', 'cursor-not-allowed');
            } else {
                ticketBtn.classList.remove('opacity-50', 'cursor-not-allowed');
            }
        }

        if (goldBtn) {
            goldBtn.disabled = isDropping || userGold < goldCost;
            if (goldBtn.disabled) {
                goldBtn.classList.add('opacity-50', 'cursor-not-allowed');
            } else {
                goldBtn.classList.remove('opacity-50', 'cursor-not-allowed');
            }
        }

        renderCosmeticsVault();
    }

    function formatCountdown(totalSec) {
        const timerEl = document.getElementById('plinkoDailyTimer');
        if (!timerEl) return;

        if (totalSec <= 0) {
            timerEl.innerText = '✨ READY TO DROP!';
            timerEl.className = 'text-xs font-bold text-emerald-400 animate-pulse';
            if (document.getElementById('plinkoDailyBtn')) {
                document.getElementById('plinkoDailyBtn').disabled = isDropping;
                document.getElementById('plinkoDailyBtn').classList.remove('opacity-50', 'cursor-not-allowed');
            }
            return;
        }

        const hrs = Math.floor(totalSec / 3600);
        const mins = Math.floor((totalSec % 3600) / 60);
        const secs = Math.floor(totalSec % 60);
        timerEl.innerText = `⏳ Next Drop: ${hrs.toString().padStart(2, '0')}:${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
        timerEl.className = 'text-xs font-mono text-amber-400/90';
    }

    /**
     * Renders cosmetics & avatar frame vault
     */
    function renderCosmeticsVault() {
        const vaultContainer = document.getElementById('plinkoCosmeticsVault');
        if (!vaultContainer) return;

        const borders = [
            {id: 'border_flaming_kongor', name: 'Flaming Kongor', css: 'border-flaming-kongor'},
            {id: 'border_frostbite', name: 'Frostbite Glacius', css: 'border-frostbite'},
            {id: 'border_electric_diamond', name: 'Electric Diamond', css: 'border-electric-diamond'},
            {id: 'border_prismatic_void', name: 'Prismatic Void', css: 'border-prismatic-void'},
            {id: 'border_hellbourne_pyre', name: 'Hellbourne Pyre', css: 'border-hellbourne-pyre'}
        ];

        const titles = [
            {id: 'title_legion_veteran', name: 'Legion Veteran', css: 'title-badge-legion-veteran'},
            {id: 'title_hellbourne_slayer', name: 'Hellbourne Slayer', css: 'title-badge-hellbourne-slayer'},
            {id: 'title_tavern_regular', name: 'Tavern Regular', css: 'title-badge-tavern-regular'},
            {id: 'title_kongors_bane', name: "Kongor's Bane", css: 'title-badge-kongors-bane'},
            {id: 'title_immortal_legend', name: 'Immortal Legend', css: 'title-badge-immortal-legend'}
        ];

        const unlockedBordersSet = new Set(plinkoState.unlockedBorders || []);
        const unlockedTitlesSet = new Set(plinkoState.unlockedTitles || []);

        let html = `
            <div class="space-y-4">
                <div>
                    <h4 class="text-xs font-bold uppercase tracking-wider text-slate-300 mb-2">Unlocked Avatar Frames</h4>
                    <div class="grid grid-cols-2 sm:grid-cols-5 gap-3">
                        ${borders.map(b => {
            const owned = unlockedBordersSet.has(b.id);
            const equipped = plinkoState.equippedBorder === b.id;
            return `
                                <div class="p-3 rounded-2xl bg-slate-900/90 border ${equipped ? 'border-amber-400 ring-2 ring-amber-400/30' : 'border-slate-800'} text-center space-y-2 relative">
                                    <div class="w-14 h-14 mx-auto rounded-full ${owned ? b.css : 'border border-slate-700 opacity-40'} p-0.5">
                                        <img src="/images/default-avatar.png" class="w-full h-full object-cover rounded-full bg-slate-950"/>
                                    </div>
                                    <div class="text-[11px] font-black text-slate-200 truncate">${b.name}</div>
                                    ${owned ? `
                                        <button onclick="window.PlinkoGame.equipCosmetic('${b.id}', null)" class="w-full py-1 text-[10px] font-bold rounded-lg ${equipped ? 'bg-amber-500 text-slate-950' : 'bg-slate-800 hover:bg-slate-700 text-amber-300'} transition-all">
                                            ${equipped ? '✓ Equipped' : 'Equip'}
                                        </button>
                                    ` : `
                                        <span class="text-[10px] text-slate-600 font-semibold">🔒 Locked</span>
                                    `}
                                </div>
                            `;
        }).join('')}
                    </div>
                </div>

                <div class="pt-2 border-t border-slate-800">
                    <h4 class="text-xs font-bold uppercase tracking-wider text-slate-300 mb-2">Unlocked Prestige Titles</h4>
                    <div class="flex flex-wrap gap-2">
                        ${titles.map(t => {
            const owned = unlockedTitlesSet.has(t.id);
            const equipped = plinkoState.equippedTitle === t.id;
            return `
                                <div class="px-3 py-1.5 rounded-xl border ${owned ? t.css : 'border-slate-800 bg-slate-900/50 text-slate-600'} text-xs font-black flex items-center gap-2">
                                    <span>${t.name}</span>
                                    ${owned ? `
                                        <button onclick="window.PlinkoGame.equipCosmetic(null, '${t.id}')" class="text-[10px] px-1.5 py-0.5 rounded ${equipped ? 'bg-amber-400 text-slate-950' : 'bg-slate-900/80 text-white hover:bg-slate-800'} transition-all">
                                            ${equipped ? '✓ Active' : 'Select'}
                                        </button>
                                    ` : '🔒'}
                                </div>
                            `;
        }).join('')}
                    </div>
                </div>
            </div>
        `;

        vaultContainer.innerHTML = html;
    }

    /**
     * Executes Plinko Drop API Call
     * @param {"daily"|"tickets"|"gold"} currencyType
     * @param {number|null} customChute
     */
    async function triggerDrop(currencyType = "daily", customChute = null) {
        if (isDropping) return;
        isDropping = true;
        ensurePlinkoLoop();

        if (customChute !== null) {
            selectedChute = customChute;
        }

        const dailyBtn = document.getElementById('plinkoDailyBtn');
        const ticketBtn = document.getElementById('plinkoTicketBtn');
        const goldBtn = document.getElementById('plinkoGoldBtn');
        if (dailyBtn) dailyBtn.disabled = true;
        if (ticketBtn) ticketBtn.disabled = true;
        if (goldBtn) goldBtn.disabled = true;

        try {
            const res = await fetch('/api/plinko/drop', {
                method: 'POST',
                headers: {'Content-Type': 'application/json'},
                body: JSON.stringify({
                    useDaily: currencyType === "daily",
                    currency: currencyType
                })
            });

            if (!res.ok) {
                const err = await res.json();
                alert(err.message || 'Failed to drop Plinko ball.');
                isDropping = false;
                updatePlinkoUI();
                return;
            }

            const data = await res.json();

            // Calculate Target Landing X on Canvas
            const bucketWidth = canvas.width / BUCKETS.length;
            const targetX = data.slotIndex * bucketWidth + bucketWidth / 2;

            // Calculate Starting X based on selected drop chute
            const chuteSpacing = (canvas.width - 120) / 4;
            const startX = 60 + selectedChute * chuteSpacing + (Math.random() - 0.5) * 8;

            activeBall = {
                x: startX,
                y: 42,
                radius: 12,
                vx: (Math.random() - 0.5) * 20,
                vy: 30,
                vRot: (Math.random() - 0.5) * 6,
                angle: 0,
                history: [],
                targetX: targetX,
                dropResult: data
            };

            if (window.NexusSoundboard) {
                window.NexusSoundboard.play('taunt_bell');
            }

        } catch (err) {
            console.error('Plinko drop request failed', err);
            isDropping = false;
            updatePlinkoUI();
        }
    }

    async function equipCosmetic(borderId, titleId) {
        try {
            const res = await fetch('/api/plinko/equip-cosmetic', {
                method: 'POST',
                headers: {'Content-Type': 'application/json'},
                body: JSON.stringify({borderId: borderId, titleId: titleId})
            });

            if (res.ok) {
                if (borderId !== null) plinkoState.equippedBorder = borderId;
                if (titleId !== null) plinkoState.equippedTitle = titleId;
                renderCosmeticsVault();
            }
        } catch (err) {
            console.error('Failed to equip cosmetic', err);
        }
    }

    /**
     * Canvas Click & Mousemove handler for top chute interaction
     */
    function handleCanvasInteraction(evt) {
        const rect = cachedPlinkoRect || (cachedPlinkoRect = canvas.getBoundingClientRect());
        if (!rect.width || !rect.height) return;
        const mouseX = (evt.clientX - rect.left) * (canvas.width / rect.width);
        const mouseY = (evt.clientY - rect.top) * (canvas.height / rect.height);

        // Check if mouse is in top chute selector zone
        if (mouseY <= 65) {
            const chuteSpacing = (canvas.width - 120) / 4;
            for (let i = 0; i < 5; i++) {
                const cx = 60 + i * chuteSpacing;
                if (Math.abs(mouseX - cx) < 25) {
                    selectedChute = i;
                    canvas.style.cursor = 'pointer';

                    if (evt.type === 'click' && !isDropping) {
                        const userTickets = plinkoState.plinkoTickets ?? plinkoState.tickets ?? 0;
                        const ticketCost = plinkoState.ticketCost ?? TICKET_COST;
                        if (plinkoState.canSpinDaily) {
                            triggerDrop("daily", i);
                        } else if (userTickets >= ticketCost) {
                            triggerDrop("tickets", i);
                        }
                    }
                    return;
                }
            }
        }
        canvas.style.cursor = 'default';
    }

    window.PlinkoGame = {
        init: function () {
            canvas = document.getElementById('plinkoCanvas');
            if (!canvas) return;

            ctx = canvas.getContext('2d');
            const parentWidth = canvas.parentElement.clientWidth || 640;
            canvas.width = Math.min(650, parentWidth);
            canvas.height = 510;

            initPegs(canvas.width, canvas.height);

            canvas.addEventListener('mousemove', handleCanvasInteraction);
            canvas.addEventListener('click', handleCanvasInteraction);

            lastFrameTime = performance.now();
            if (animationFrameId) {
                cancelAnimationFrame(animationFrameId);
            }
            renderPlinko(lastFrameTime);
            if ('IntersectionObserver' in window) {
                const io = new IntersectionObserver((entries) => {
                    for (const entry of entries) {
                        isPlinkoInView = entry.isIntersecting;
                        cachedPlinkoRect = null;
                        if (isPlinkoInView && canvas && canvas.parentElement) {
                            const newWidth = Math.min(650, canvas.parentElement.clientWidth || 640);
                            if (newWidth !== canvas.width) {
                                canvas.width = newWidth;
                                initPegs(canvas.width, canvas.height);
                            }
                            ensurePlinkoLoop();
                        }
                    }
                }, { rootMargin: '150px' });
                io.observe(canvas);
            }
            if ('ResizeObserver' in window) {
                const ro = new ResizeObserver(() => { cachedPlinkoRect = null; });
                ro.observe(canvas);
            }
            window.addEventListener('scroll', () => { cachedPlinkoRect = null; }, { passive: true });
            document.addEventListener('visibilitychange', () => {
                if (!document.hidden) ensurePlinkoLoop();
            });
            fetchPlinkoStatus();

            setInterval(() => {
                if (plinkoState.secondsUntilNextDailySpin > 0) {
                    plinkoState.secondsUntilNextDailySpin--;
                    formatCountdown(plinkoState.secondsUntilNextDailySpin);
                }
            }, 1000);

            window.addEventListener('resize', () => {
                if (canvas && canvas.parentElement) {
                    const newWidth = Math.min(650, canvas.parentElement.clientWidth || 640);
                    if (newWidth !== canvas.width) {
                        canvas.width = newWidth;
                        initPegs(canvas.width, canvas.height);
                    }
                }
            });
        },
        dropDaily: () => triggerDrop("daily"),
        dropTicket: () => triggerDrop("tickets"),
        dropGold: () => triggerDrop("gold"),
        equipCosmetic: equipCosmetic,
        closeResultModal: () => {
            const modal = document.getElementById('plinkoResultModal');
            if (modal) {
                modal.classList.add('hidden');
                modal.classList.remove('flex');
            }
        }
    };

    document.addEventListener('DOMContentLoaded', () => {
        if (document.getElementById('plinkoCanvas')) {
            window.PlinkoGame.init();
        }
    });
})();
