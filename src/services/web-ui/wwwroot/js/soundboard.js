// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * DAWNBRINGER HoN Soundboard & Audio Experience Engine
 * Streams authentic game audio from VFS backend + Web Audio API synthesizer fallback + Waveform visualizer.
 */
(function () {
    'use strict';

    let audioCtx = null;
    let isMuted = false;
    let masterVolume = 0.8;
    let currentPlayingAudio = null;
    let activeVisualizerInterval = null;

    function getAudioContext() {
        if (!audioCtx) {
            const AudioContextClass = window.AudioContext || window.webkitAudioContext;
            if (AudioContextClass) {
                audioCtx = new AudioContextClass();
            }
        }
        if (audioCtx && audioCtx.state === 'suspended') {
            audioCtx.resume();
        }
        return audioCtx;
    }

    const SOUND_CATALOG = [
        // Classic Announcer Lines
        {
            id: 'first_blood',
            name: 'First Blood',
            pack: 'Classic',
            category: 'kills',
            desc: 'First blood spilled on Newerth',
            audioUrl: '/api/v1/assets/sound?path=shared/sounds/announcer/first_blood.ogg',
            freq: 440,
            duration: 1.2
        },
        {
            id: 'double_kill',
            name: 'Double Kill!',
            pack: 'Classic',
            category: 'kills',
            desc: 'Two enemies slain in rapid succession',
            audioUrl: '/api/v1/assets/sound?path=shared/sounds/announcer/double_kill.ogg',
            freq: 480,
            duration: 1.3
        },
        {
            id: 'hat_trick',
            name: 'Hat Trick! (3x)',
            pack: 'Classic',
            category: 'sprees',
            desc: '3 consecutive kills',
            audioUrl: '/api/v1/assets/sound?path=shared/sounds/announcer/3_kills.ogg',
            freq: 520,
            duration: 1.4
        },
        {
            id: 'quad_kill',
            name: 'Quad Kill! (4x)',
            pack: 'Classic',
            category: 'sprees',
            desc: '4 consecutive kills',
            audioUrl: '/api/v1/assets/sound?path=shared/sounds/announcer/4_kills.ogg',
            freq: 600,
            duration: 1.6
        },
        {
            id: 'bloodlust',
            name: 'Bloodlust! (5x)',
            pack: 'Badass',
            category: 'kills',
            desc: 'Unstoppable 5 kill streak',
            audioUrl: '/api/v1/assets/sound?announcer=ballsofsteel&action=5_kills',
            freq: 330,
            duration: 1.5
        },
        {
            id: 'annihilation',
            name: 'ANNIHILATION! (5x)',
            pack: 'Classic',
            category: 'sprees',
            desc: 'Team wipe 5-man ace kill',
            audioUrl: '/api/v1/assets/sound?path=shared/sounds/announcer/annihilation.ogg',
            freq: 880,
            duration: 2.4
        },
        {
            id: 'immortal',
            name: 'Immortal! (10x)',
            pack: 'Classic',
            category: 'sprees',
            desc: '10 consecutive kills without dying',
            audioUrl: '/api/v1/assets/sound?path=shared/sounds/announcer/10_kills.ogg',
            freq: 950,
            duration: 2.0
        },
        {
            id: 'nemesis',
            name: 'Nemesis!',
            pack: 'Classic',
            category: 'combat',
            desc: 'Dominating a rival player',
            audioUrl: '/api/v1/assets/sound?path=shared/sounds/announcer/nemesis.ogg',
            freq: 350,
            duration: 1.5
        },
        {
            id: 'retribution',
            name: 'Retribution!',
            pack: 'Classic',
            category: 'combat',
            desc: 'Revenge kill on your nemesis',
            audioUrl: '/api/v1/assets/sound?path=shared/sounds/announcer/payback.ogg',
            freq: 490,
            duration: 1.5
        },
        {
            id: 'humiliation',
            name: 'HUMILIATION!',
            pack: 'Classic',
            category: 'taunts',
            desc: 'Killed by creep, neutral, or tower',
            audioUrl: '/api/v1/assets/sound?path=shared/sounds/announcer/humiliation.ogg',
            freq: 180,
            duration: 2.0
        },
        {
            id: 'rage_quit',
            name: 'Rage Quit!',
            pack: 'Flamboyant',
            category: 'combat',
            desc: 'Enemy player rage disconnect',
            audioUrl: '/api/v1/assets/sound?path=shared/sounds/announcer/flamboyant/rage_quit.ogg',
            freq: 300,
            duration: 1.8
        },

        // Iconic Announcer Packs
        {
            id: 'cherry_popper',
            name: 'Cherry Popper!',
            pack: 'Flamboyant',
            category: 'announcer',
            desc: 'First kill with Flamboyant pack',
            audioUrl: '/api/v1/assets/sound?path=shared/sounds/announcer/flamboyant/first_blood.ogg',
            freq: 660,
            duration: 1.8
        },
        {
            id: 'fabulous',
            name: 'FABULOUS!',
            pack: 'Flamboyant',
            category: 'announcer',
            desc: 'Flamboyant 5-kill Annihilation',
            audioUrl: '/api/v1/assets/sound?path=shared/sounds/announcer/flamboyant/annihilation.ogg',
            freq: 720,
            duration: 1.7
        },
        {
            id: 'smackdown',
            name: 'SMACKDOWN!',
            pack: 'Badass',
            category: 'taunts',
            desc: 'Badass announcer smackdown',
            audioUrl: '/api/v1/assets/sound?announcer=ballsofsteel&action=humiliation',
            freq: 220,
            duration: 1.6
        },
        {
            id: 'breaky_cost',
            name: 'But At What Cost?!',
            pack: 'BreakyCPK',
            category: 'announcer',
            desc: 'Legendary caster shoutout',
            audioUrl: '/api/v1/assets/sound?announcer=breakycpk&action=annihilation',
            freq: 410,
            duration: 2.0
        },
        {
            id: 'samuel_danger',
            name: 'Bad Mother!',
            pack: 'Samuel L.',
            category: 'announcer',
            desc: 'Samuel L. Jackson Annihilation',
            audioUrl: '/api/v1/assets/sound?announcer=bamf&action=annihilation',
            freq: 260,
            duration: 1.8
        },
        {
            id: 'pimp_first_blood',
            name: 'Pimp First Blood',
            pack: 'Pimp',
            category: 'announcer',
            desc: 'Pimp pack first blood',
            audioUrl: '/api/v1/assets/sound?announcer=pimp&action=first_blood',
            freq: 420,
            duration: 1.8
        },

        // Battlefield Taunts
        {
            id: 'taunt_baby',
            name: 'Baby Cry Taunt',
            pack: 'Taunts',
            category: 'taunts',
            desc: 'Crying baby taunt effect',
            audioUrl: '/api/v1/assets/sound?taunt=cryingbaby&action=cast',
            freq: 550,
            duration: 2.0
        },
        {
            id: 'taunt_hellbourne',
            name: 'Heavy Metal Taunt',
            pack: 'Taunts',
            category: 'taunts',
            desc: 'Hellbourne heavy metal riff',
            audioUrl: '/api/v1/assets/sound?taunt=hellbourne&action=deathmetal',
            freq: 150,
            duration: 2.5
        },
        {
            id: 'taunt_dumpster',
            name: 'Dumpster Taunt',
            pack: 'Taunts',
            category: 'taunts',
            desc: 'Drop them in the trash dumpster',
            audioUrl: '/api/v1/assets/sound?taunt=dumpster&action=taunt',
            freq: 200,
            duration: 1.8
        },
        {
            id: 'taunt_gs',
            name: 'Dropping Gs Taunt',
            pack: 'Taunts',
            category: 'taunts',
            desc: 'Dropping gold coins taunt',
            audioUrl: '/api/v1/assets/sound?taunt=gs&action=taunt',
            freq: 600,
            duration: 2.0
        },
        {
            id: 'taunt_acme',
            name: 'ACME Anvil Taunt',
            pack: 'Taunts',
            category: 'taunts',
            desc: 'ACME cartoon anvil slam',
            audioUrl: '/api/v1/assets/sound?taunt=acme&action=anvil',
            freq: 300,
            duration: 1.8
        },
        {
            id: 'taunt_kongor',
            name: 'Kongor Roar Taunt',
            pack: 'Taunts',
            category: 'taunts',
            desc: 'Kongor battlefield roar',
            audioUrl: '/api/v1/assets/sound?taunt=kongor&action=kongortaunt',
            freq: 120,
            duration: 2.2
        },

        // Game & UI Audio SFX
        {
            id: 'taunt_bell',
            name: 'Bling / Coin Chime',
            pack: 'SFX',
            category: 'sfx',
            desc: 'Vault gold activation chime',
            audioUrl: '/api/v1/assets/sound?path=shared/sounds/announcer/pimp/bling_1.ogg',
            freq: 110,
            duration: 2.2
        },
        {
            id: 'victory_fanfare',
            name: 'Legion Victory',
            pack: 'SFX',
            category: 'sfx',
            desc: 'Legion victory anthem',
            audioUrl: '/api/v1/assets/sound?path=shared/sounds/announcer/legion_wins.ogg',
            freq: 587,
            duration: 2.5
        },
        {
            id: 'prepare_battle',
            name: 'Prepare for Battle',
            pack: 'SFX',
            category: 'sfx',
            desc: 'Match start trumpet herald',
            audioUrl: '/api/v1/assets/sound?path=shared/sounds/announcer/prepare_for_battle.ogg',
            freq: 440,
            duration: 2.0
        }
    ];

    /**
     * Procedural Web Audio API sound synthesizer (Fallback if audio clip network stream fails)
     */
    function synthesizeSound(soundDef) {
        const ctx = getAudioContext();
        if (!ctx || isMuted) return;

        const now = ctx.currentTime;
        const gainNode = ctx.createGain();
        gainNode.gain.setValueAtTime(masterVolume, now);

        if (soundDef.id === 'taunt_bell') {
            [110, 220, 330, 440, 587].forEach((f, idx) => {
                const osc = ctx.createOscillator();
                const oscGain = ctx.createGain();
                osc.type = idx % 2 === 0 ? 'sine' : 'triangle';
                osc.frequency.setValueAtTime(f, now);
                osc.frequency.exponentialRampToValueAtTime(f * 0.95, now + 2.0);
                oscGain.gain.setValueAtTime(0.4 / (idx + 1), now);
                oscGain.gain.exponentialRampToValueAtTime(0.001, now + 2.0);
                osc.connect(oscGain);
                oscGain.connect(gainNode);
                osc.start(now);
                osc.stop(now + 2.0);
            });
        } else {
            const osc = ctx.createOscillator();
            osc.type = 'triangle';
            osc.frequency.setValueAtTime(soundDef.freq || 440, now);
            osc.frequency.exponentialRampToValueAtTime((soundDef.freq || 440) * 1.25, now + 0.15);
            osc.frequency.exponentialRampToValueAtTime((soundDef.freq || 440) * 0.9, now + (soundDef.duration || 1.2));

            gainNode.gain.setValueAtTime(masterVolume * 0.6, now);
            gainNode.gain.exponentialRampToValueAtTime(0.001, now + (soundDef.duration || 1.2));

            osc.connect(gainNode);
            osc.start(now);
            osc.stop(now + (soundDef.duration || 1.2));
        }

        gainNode.connect(ctx.destination);
    }

    function triggerWaveAnimation(durationMs) {
        const visualizer = document.getElementById('soundboardVisualizer');
        if (!visualizer) return;

        visualizer.classList.remove('opacity-0');
        visualizer.classList.add('opacity-100');

        const bars = visualizer.querySelectorAll('.sound-bar');

        if (activeVisualizerInterval) clearInterval(activeVisualizerInterval);

        activeVisualizerInterval = setInterval(() => {
            bars.forEach(bar => {
                const randomH = Math.floor(Math.random() * 26) + 6;
                bar.style.height = randomH + 'px';
            });
        }, 100);

        setTimeout(() => {
            if (activeVisualizerInterval) {
                clearInterval(activeVisualizerInterval);
                activeVisualizerInterval = null;
            }
            bars.forEach(bar => bar.style.height = '4px');
            visualizer.classList.remove('opacity-100');
            visualizer.classList.add('opacity-0');
        }, durationMs || 1600);
    }

    const NexusSoundboard = {
        catalog: SOUND_CATALOG,

        playUrl: function (url, displayName) {
            if (isMuted) return;

            if (currentPlayingAudio) {
                try {
                    currentPlayingAudio.pause();
                    currentPlayingAudio.currentTime = 0;
                } catch (e) {
                }
            }

            try {
                const audio = new Audio(url);
                audio.volume = masterVolume;
                currentPlayingAudio = audio;

                audio.play().then(() => {
                    triggerWaveAnimation(2200);
                }).catch(err => {
                    console.warn('[NexusSoundboard] Audio play failed, using synthesizer fallback:', err);
                    synthesizeSound({id: 'fallback', freq: 440, duration: 1.0});
                    triggerWaveAnimation(1000);
                });

                audio.onended = () => {
                    if (currentPlayingAudio === audio) currentPlayingAudio = null;
                };

                const toast = document.getElementById('soundboardToast');
                if (toast && displayName) {
                    toast.innerText = `🔊 Playing: ${displayName}`;
                    toast.classList.remove('opacity-0', 'translate-y-2');
                    toast.classList.add('opacity-100', 'translate-y-0');
                    setTimeout(() => {
                        toast.classList.remove('opacity-100', 'translate-y-0');
                        toast.classList.add('opacity-0', 'translate-y-2');
                    }, 2200);
                }
            } catch (e) {
                console.error('[NexusSoundboard] Error creating audio element:', e);
            }
        },

        play: function (soundId) {
            const item = SOUND_CATALOG.find(s => s.id === soundId);
            if (!item) return;

            if (item.audioUrl && !isMuted) {
                this.playUrl(item.audioUrl, `${item.name} (${item.pack})`);
            } else {
                synthesizeSound(item);
                triggerWaveAnimation(1200);
            }
        },

        toggleMute: function () {
            isMuted = !isMuted;
            if (isMuted && currentPlayingAudio) {
                try {
                    currentPlayingAudio.pause();
                } catch (e) {
                }
            }
            const muteBtn = document.getElementById('soundboardMuteBtn');
            if (muteBtn) {
                muteBtn.innerHTML = isMuted ? '<i class="fas fa-volume-mute text-rose-400"></i>' : '<i class="fas fa-volume-up text-amber-400"></i>';
            }
            return isMuted;
        },

        setVolume: function (vol) {
            masterVolume = Math.max(0, Math.min(1, vol));
            if (currentPlayingAudio) {
                currentPlayingAudio.volume = masterVolume;
            }
        },

        openSoundboardModal: function () {
            const modal = document.getElementById('soundboardModal');
            if (modal) {
                modal.classList.remove('hidden');
                modal.classList.add('flex');
            }
        },

        closeSoundboardModal: function () {
            const modal = document.getElementById('soundboardModal');
            if (modal) {
                modal.classList.add('hidden');
                modal.classList.remove('flex');
            }
        }
    };

    window.NexusSoundboard = NexusSoundboard;

    document.addEventListener('DOMContentLoaded', () => {
        // Build Soundboard modal if not present
        if (!document.getElementById('soundboardModal')) {
            const modalHtml = `
            <div id="soundboardModal" class="fixed inset-0 z-50 hidden items-center justify-center bg-slate-950/80 backdrop-blur-md p-4 transition-all animate-fadeIn">
                <div class="glass-panel w-full max-w-2xl rounded-3xl border border-amber-500/30 p-6 shadow-2xl bg-slate-900/95 space-y-5">
                    <!-- Modal Header -->
                    <div class="flex items-center justify-between border-b border-slate-800 pb-4">
                        <div class="flex items-center gap-3">
                            <div class="w-10 h-10 rounded-2xl bg-amber-500/20 border border-amber-500/40 flex items-center justify-center text-amber-400 text-xl shadow-inner">
                                🎙️
                            </div>
                            <div>
                                <h3 class="text-xl font-black text-white tracking-tight">HoN Announcer & Soundboard</h3>
                                <p class="text-xs text-slate-400">Authentic Heroes of Newerth announcer voice lines and taunts</p>
                            </div>
                        </div>
                        <div class="flex items-center gap-2">
                            <button id="soundboardMuteBtn" onclick="NexusSoundboard.toggleMute()" title="Toggle Mute" class="p-2 rounded-xl bg-slate-800 hover:bg-slate-700 text-slate-300 transition-all text-sm cursor-pointer">
                                <i class="fas fa-volume-up text-amber-400"></i>
                            </button>
                            <button onclick="NexusSoundboard.closeSoundboardModal()" title="Close" class="p-2 rounded-xl bg-slate-800 hover:bg-slate-700 text-slate-300 hover:text-white transition-all text-sm cursor-pointer">
                                <i class="fas fa-times"></i>
                            </button>
                        </div>
                    </div>

                    <!-- Sound Wave Visualizer -->
                    <div id="soundboardVisualizer" class="h-8 flex items-center justify-center gap-1.5 opacity-0 transition-opacity duration-300">
                        <span class="sound-bar w-1.5 bg-amber-400 rounded-full transition-all duration-150" style="height: 4px;"></span>
                        <span class="sound-bar w-1.5 bg-pink-500 rounded-full transition-all duration-150" style="height: 4px;"></span>
                        <span class="sound-bar w-1.5 bg-purple-500 rounded-full transition-all duration-150" style="height: 4px;"></span>
                        <span class="sound-bar w-1.5 bg-cyan-400 rounded-full transition-all duration-150" style="height: 4px;"></span>
                        <span class="sound-bar w-1.5 bg-emerald-400 rounded-full transition-all duration-150" style="height: 4px;"></span>
                    </div>

                    <!-- Audio Trigger Grid -->
                    <div class="grid grid-cols-2 sm:grid-cols-3 gap-2.5 max-h-80 overflow-y-auto pr-1">
                        ${SOUND_CATALOG.map(s => `
                            <button type="button" onclick="NexusSoundboard.play('${s.id}')" 
                                    class="group p-3 rounded-2xl bg-slate-950/80 hover:bg-gradient-to-r hover:from-amber-600/20 hover:to-purple-600/20 border border-slate-800 hover:border-amber-500/50 text-left transition-all shadow-md flex flex-col justify-between cursor-pointer active:scale-95">
                                <div class="flex items-center justify-between">
                                    <span class="text-xs font-black text-slate-200 group-hover:text-amber-300 tracking-wide">${s.name}</span>
                                    <span class="text-[10px] px-1.5 py-0.5 rounded bg-slate-900 border border-slate-700 text-slate-400 font-mono">${s.pack}</span>
                                </div>
                                <span class="text-[10px] text-slate-500 mt-1 line-clamp-1">${s.desc}</span>
                            </button>
                        `).join('')}
                    </div>

                    <!-- Toast Notification -->
                    <div id="soundboardToast" class="text-center text-xs font-bold text-amber-400 bg-amber-950/30 border border-amber-500/20 rounded-xl py-1.5 opacity-0 translate-y-2 transition-all duration-300">
                        🔊 Soundboard ready
                    </div>
                </div>
            </div>
            `;
            document.body.insertAdjacentHTML('beforeend', modalHtml);
        }
    });
})();
