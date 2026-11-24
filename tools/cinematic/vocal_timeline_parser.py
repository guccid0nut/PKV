#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Vocal Timeline & Lyrics Timestamp Parser for 'Can You Feel My Heart' 3D Music Video.
Production Tool Implementation in tools/cinematic.

Ingests master audio ('assets/audio/can_you_feel_my_heart_cover.wav') and generates
'assets/cinematic/vocal_timeline.json' driving skeletal animation, choreography,
and cinematography direction.
"""

import os
import sys
import time
import json
import logging
import argparse
from typing import Dict, List, Any, Optional, Tuple

import numpy as np
import scipy.signal as signal
import soundfile as sf

def _find_repo_root() -> str:
    curr = os.path.abspath(os.path.dirname(__file__))
    while curr and os.path.dirname(curr) != curr:
        if os.path.exists(os.path.join(curr, "Directory.Build.props")):
            return curr
        curr = os.path.dirname(curr)
    return os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

PROJECT_ROOT = _find_repo_root()
SRC_DIR = os.path.join(PROJECT_ROOT, "src")
for p in (SRC_DIR, PROJECT_ROOT):
    if p not in sys.path:
        sys.path.insert(0, p)

DEFAULT_AUDIO_PATH = os.path.join(PROJECT_ROOT, "assets", "audio", "can_you_feel_my_heart_cover.wav")
DEFAULT_OUTPUT_PATH = os.path.join(PROJECT_ROOT, "assets", "cinematic", "vocal_timeline.json")

logger = logging.getLogger("tools.cinematic.vocal_timeline_parser")

# Canonical 12-Section Master Choreography Blueprint
CANONICAL_SECTIONS: List[Dict[str, Any]] = [
    {
        "id": "S01",
        "name": "Atmospheric Intro",
        "start_time": 0.0,
        "end_time": 7.5,
        "start_frame": 0,
        "end_frame": 225,
        "vocal_track_type": "InstrumentalPause",
        "performer": "None",
        "lyrics": "",
        "recommended_clip_empath": "default_1",
        "recommended_clip_parrot": "bored_1",
        "camera_cue": "Shot1_Intro_Dolly_In",
    },
    {
        "id": "S02",
        "name": "Intro Vocal Chops",
        "start_time": 7.5,
        "end_time": 27.5,
        "start_frame": 225,
        "end_frame": 825,
        "vocal_track_type": "EmpathCleanLead",
        "performer": "Empath",
        "lyrics": "Can you hear the silence? Can you see the dark? Can you fix the broken? Can you feel, can you feel my heart?",
        "recommended_clip_empath": "ability_1",
        "recommended_clip_parrot": "bored_1",
        "camera_cue": "Shot2_Empath_LipSync_Medium",
    },
    {
        "id": "S03",
        "name": "Drop 1 (Intro Drop)",
        "start_time": 27.5,
        "end_time": 54.0,
        "start_frame": 825,
        "end_frame": 1620,
        "vocal_track_type": "CourierParrotMetalScream",
        "performer": "Courier Parrot (feat. Empath)",
        "lyrics": "Can you feel my heart?! (Heavy synth drop & metal screams)",
        "recommended_clip_empath": "ability_4",
        "recommended_clip_parrot": "attack_1",
        "camera_cue": "Shot4_Parrot_Scream_LowAngle",
    },
    {
        "id": "S04",
        "name": "Verse 1",
        "start_time": 54.0,
        "end_time": 74.0,
        "start_frame": 1620,
        "end_frame": 2220,
        "vocal_track_type": "EmpathCleanLead",
        "performer": "Empath",
        "lyrics": "Can you help the hopeless? Well, I'm begging on my knees. Can you save my bastard soul? Will you wait for me?",
        "recommended_clip_empath": "walk_1",
        "recommended_clip_parrot": "default_1",
        "camera_cue": "Shot3_Traverse_Floor_Wide",
    },
    {
        "id": "S05",
        "name": "Pre-Chorus 1",
        "start_time": 74.0,
        "end_time": 94.0,
        "start_frame": 2220,
        "end_frame": 2820,
        "vocal_track_type": "DuetHarmony",
        "performer": "Duet (Empath + Parrot)",
        "lyrics": "I'm sorry, brothers, so sorry, lover. Forgive me, father, I love you, mother.",
        "recommended_clip_empath": "ability_2",
        "recommended_clip_parrot": "attack_2",
        "camera_cue": "Shot6_Dual_Performer_TwoShot",
    },
    {
        "id": "S06",
        "name": "Chorus 1 (Drop 2)",
        "start_time": 94.0,
        "end_time": 121.5,
        "start_frame": 2820,
        "end_frame": 3645,
        "vocal_track_type": "CourierParrotMetalScream",
        "performer": "Courier Parrot (feat. Empath)",
        "lyrics": "Can you hear the silence? Can you see the dark? Can you fix the broken? Can you feel my heart?!",
        "recommended_clip_empath": "ability_4",
        "recommended_clip_parrot": "attack_1",
        "camera_cue": "Shot5_Chorus_360_Orbit",
    },
    {
        "id": "S07",
        "name": "Soft Interlude",
        "start_time": 121.5,
        "end_time": 146.0,
        "start_frame": 3645,
        "end_frame": 4380,
        "vocal_track_type": "WhisperIntimate",
        "performer": "Empath",
        "lyrics": "I'm scared to get close, and I hate being alone. I long for that feeling to not feel at all. The higher I get, the lower I'll sink. I can't drown my demons, they know how to swim.",
        "recommended_clip_empath": "default_1",
        "recommended_clip_parrot": "bored_1",
        "camera_cue": "Shot2_Empath_LipSync_Medium",
    },
    {
        "id": "S08",
        "name": "Verse 2 (Build-up)",
        "start_time": 146.0,
        "end_time": 165.0,
        "start_frame": 4380,
        "end_frame": 4950,
        "vocal_track_type": "EmpathCleanLead",
        "performer": "Empath",
        "lyrics": "Can you feel my heart? Can you hear the silence? Can you see the dark? Can you fix the broken?",
        "recommended_clip_empath": "walk_1",
        "recommended_clip_parrot": "attack_2",
        "camera_cue": "Shot3_Traverse_Floor_Wide",
    },
    {
        "id": "S09",
        "name": "Heavy Bridge Drop",
        "start_time": 165.0,
        "end_time": 183.5,
        "start_frame": 4950,
        "end_frame": 5505,
        "vocal_track_type": "DuetHarmony",
        "performer": "Duet (Empath + Parrot)",
        "lyrics": "Can you feel my heart?! Can you feel my heart?!",
        "recommended_clip_empath": "attack_2",
        "recommended_clip_parrot": "attack_1",
        "camera_cue": "Shot6_Dual_Performer_TwoShot",
    },
    {
        "id": "S10",
        "name": "Breath Pause",
        "start_time": 183.5,
        "end_time": 185.0,
        "start_frame": 5505,
        "end_frame": 5550,
        "vocal_track_type": "InstrumentalPause",
        "performer": "None",
        "lyrics": "(Sudden sub-bass cutoff / sharp inhale breath)",
        "recommended_clip_empath": "default_1",
        "recommended_clip_parrot": "bored_1",
        "camera_cue": "Shot4_Parrot_Scream_LowAngle",
    },
    {
        "id": "S11",
        "name": "Climax Final Chorus",
        "start_time": 185.0,
        "end_time": 217.0,
        "start_frame": 5550,
        "end_frame": 6510,
        "vocal_track_type": "DuetHarmony",
        "performer": "Duet (Empath + Parrot)",
        "lyrics": "Can you hear the silence? Can you see the dark? Can you fix the broken? Can you feel my heart?! Forever and ever!",
        "recommended_clip_empath": "ability_4",
        "recommended_clip_parrot": "attack_1",
        "camera_cue": "Shot5_Chorus_360_Orbit",
    },
    {
        "id": "S12",
        "name": "Outro & Decay",
        "start_time": 217.0,
        "end_time": 221.5068,
        "start_frame": 6510,
        "end_frame": 6645,
        "vocal_track_type": "InstrumentalPause",
        "performer": "None",
        "lyrics": "(Dying synth echoes & ambient room reverb fadeout)",
        "recommended_clip_empath": "default_1",
        "recommended_clip_parrot": "bored_1",
        "camera_cue": "Shot7_Outro_Pull_Out",
    },
]

# Canonical Scream Triggers
CANONICAL_SCREAM_TRIGGERS: List[Dict[str, Any]] = [
    {
        "trigger_id": "SCREAM_01_INTRO_DROP",
        "start_time": 27.5,
        "end_time": 34.0,
        "start_frame": 825,
        "end_frame": 1020,
        "intensity": 1.0,
        "beak_open": 1.0,
        "wing_spread": 0.85,
        "crest_fan": 1.0,
        "camera_cue": "Shot4_Parrot_Scream_LowAngle",
    },
    {
        "trigger_id": "SCREAM_02_INTRO_DROP_TAIL",
        "start_time": 41.0,
        "end_time": 48.0,
        "start_frame": 1230,
        "end_frame": 1440,
        "intensity": 0.85,
        "beak_open": 0.90,
        "wing_spread": 0.75,
        "crest_fan": 0.80,
        "camera_cue": "Shot4_Parrot_Scream_LowAngle",
    },
    {
        "trigger_id": "SCREAM_03_CHORUS1_EXPLODE",
        "start_time": 94.0,
        "end_time": 102.0,
        "start_frame": 2820,
        "end_frame": 3060,
        "intensity": 1.0,
        "beak_open": 1.0,
        "wing_spread": 0.95,
        "crest_fan": 1.0,
        "camera_cue": "Shot5_Chorus_360_Orbit",
    },
    {
        "trigger_id": "SCREAM_04_CHORUS1_PHRASE2",
        "start_time": 107.5,
        "end_time": 115.0,
        "start_frame": 3225,
        "end_frame": 3450,
        "intensity": 0.90,
        "beak_open": 0.95,
        "wing_spread": 0.80,
        "crest_fan": 0.85,
        "camera_cue": "Shot5_Chorus_360_Orbit",
    },
    {
        "trigger_id": "SCREAM_05_BRIDGE_ATTACK",
        "start_time": 165.0,
        "end_time": 174.0,
        "start_frame": 4950,
        "end_frame": 5220,
        "intensity": 0.95,
        "beak_open": 1.0,
        "wing_spread": 0.90,
        "crest_fan": 1.0,
        "camera_cue": "Shot6_Dual_Performer_TwoShot",
    },
    {
        "trigger_id": "SCREAM_06_CLIMAX_DROP",
        "start_time": 185.0,
        "end_time": 196.0,
        "start_frame": 5550,
        "end_frame": 5880,
        "intensity": 1.0,
        "beak_open": 1.0,
        "wing_spread": 1.0,
        "crest_fan": 1.0,
        "camera_cue": "Shot5_Chorus_360_Orbit",
    },
    {
        "trigger_id": "SCREAM_07_CLIMAX_FINALE",
        "start_time": 200.0,
        "end_time": 214.0,
        "start_frame": 6000,
        "end_frame": 6420,
        "intensity": 1.0,
        "beak_open": 1.0,
        "wing_spread": 1.0,
        "crest_fan": 1.0,
        "camera_cue": "Shot5_Chorus_360_Orbit",
    },
]


class VocalTimelineParser:
    """
    DSP Audio Analysis & Timeline Synthesizer for 3D Music Video Production.
    """

    def __init__(
        self,
        audio_path: str = DEFAULT_AUDIO_PATH,
        fps: int = 30,
        nominal_bpm: float = 128.6,
    ):
        self.audio_path = os.path.normpath(audio_path).replace("\\", "/")
        self.fps = fps
        self.nominal_bpm = nominal_bpm

        # Audio state
        self.sample_rate: int = 48000
        self.channels: int = 2
        self.total_samples: int = 0
        self.duration_seconds: float = 0.0
        self.total_frames: int = 0
        self.hop_len: int = 1600
        self.audio_mono: Optional[np.ndarray] = None
        self.audio_raw: Optional[np.ndarray] = None

        # DSP feature state
        self.rms_envelope: Optional[np.ndarray] = None
        self.rms_clean_band: Optional[np.ndarray] = None
        self.rms_scream_band: Optional[np.ndarray] = None
        self.rms_bass_band: Optional[np.ndarray] = None
        self.zcr_envelope: Optional[np.ndarray] = None
        self.spectral_centroid: Optional[np.ndarray] = None
        self.onset_strength: Optional[np.ndarray] = None
        self.detected_bpm: float = nominal_bpm
        self.dsp_elapsed_seconds: float = 0.0
        self._cached_timeline_dict: Optional[Dict[str, Any]] = None

    def load_audio(self) -> None:
        """Loads master audio WAV file and computes basic dimension metadata."""
        if not os.path.exists(self.audio_path):
            raise FileNotFoundError(f"Master audio file not found: {self.audio_path}")

        logger.info(f"Loading master audio from: {self.audio_path}")
        data, sr = sf.read(self.audio_path, dtype="float32")
        self.sample_rate = sr
        self.audio_raw = data

        if len(data.shape) > 1:
            self.channels = data.shape[1]
            self.audio_mono = np.mean(data, axis=1)
        else:
            self.channels = 1
            self.audio_mono = data

        self.total_samples = len(self.audio_mono)
        self.duration_seconds = self.total_samples / float(self.sample_rate)
        self.total_frames = int(round(self.duration_seconds * self.fps))
        self.hop_len = int(self.sample_rate / self.fps)

        logger.info(
            f"Audio loaded: {self.sample_rate} Hz, {self.channels} ch, "
            f"{self.total_samples} samples ({self.duration_seconds:.4f}s), "
            f"{self.total_frames} video frames at {self.fps} FPS, hop: {self.hop_len} samples/frame."
        )

    def compute_dsp_features(self) -> None:
        """
        Executes full vectorized DSP feature extraction:
        - Video-frame aligned RMS energy
        - 4th-order Butterworth bandpass filtering (clean vocal vs scream bands)
        - STFT & spectral centroid
        - Zero-Crossing Rate (ZCR)
        - Onset spectral flux & autocorrelation tempo detection
        """
        if self.audio_mono is None:
            self.load_audio()

        t_start = time.time()
        mono = self.audio_mono
        sr = self.sample_rate
        fps = self.fps
        total_frames = self.total_frames
        hop_len = self.hop_len

        logger.info("Computing DSP features: Butterworth filters, STFT, ZCR, Centroid, Tempo...")

        # 1. 4th-Order Butterworth Filter Design & SOS Filtering
        sos_clean = signal.butter(4, [300, 2500], btype="bandpass", fs=sr, output="sos")
        clean_filtered = signal.sosfilt(sos_clean, mono)

        sos_scream = signal.butter(4, [2500, 6500], btype="bandpass", fs=sr, output="sos")
        scream_filtered = signal.sosfilt(sos_scream, mono)

        sos_bass = signal.butter(4, [30, 250], btype="bandpass", fs=sr, output="sos")
        bass_filtered = signal.sosfilt(sos_bass, mono)

        # 2. Frame-Aligned Matrix Reshaping for Fast Vectorized RMS
        padded_len = total_frames * hop_len
        pad_full = np.pad(mono, (0, max(0, padded_len - len(mono))))[:padded_len]
        pad_clean = np.pad(clean_filtered, (0, max(0, padded_len - len(clean_filtered))))[:padded_len]
        pad_scream = np.pad(scream_filtered, (0, max(0, padded_len - len(scream_filtered))))[:padded_len]
        pad_bass = np.pad(bass_filtered, (0, max(0, padded_len - len(bass_filtered))))[:padded_len]

        mat_full = pad_full.reshape(total_frames, hop_len)
        mat_clean = pad_clean.reshape(total_frames, hop_len)
        mat_scream = pad_scream.reshape(total_frames, hop_len)
        mat_bass = pad_bass.reshape(total_frames, hop_len)

        self.rms_envelope = np.sqrt(np.mean(mat_full ** 2, axis=1))
        self.rms_clean_band = np.sqrt(np.mean(mat_clean ** 2, axis=1))
        self.rms_scream_band = np.sqrt(np.mean(mat_scream ** 2, axis=1))
        self.rms_bass_band = np.sqrt(np.mean(mat_bass ** 2, axis=1))

        # 3. Zero-Crossing Rate (ZCR)
        signs = np.sign(mat_full)
        signs[signs == 0] = 1
        self.zcr_envelope = np.mean(np.abs(np.diff(signs, axis=1)) > 0, axis=1)

        # 4. STFT & Spectral Centroid
        n_fft = 2048
        f_bins, _, Zxx = signal.stft(
            mono, fs=sr, nperseg=n_fft, noverlap=n_fft - hop_len, boundary=None
        )
        mag = np.abs(Zxx[:, :total_frames])
        if mag.shape[1] < total_frames:
            mag = np.pad(mag, ((0, 0), (0, total_frames - mag.shape[1])), mode="edge")

        freq_weights = f_bins[:, np.newaxis]
        sum_mag = np.sum(mag, axis=0) + 1e-9
        self.spectral_centroid = np.sum(freq_weights * mag, axis=0) / sum_mag

        # 5. Onset Strength & Autocorrelation Tempo Detection
        diff_mag = np.diff(mag, axis=1)
        flux = np.mean(np.maximum(0, diff_mag), axis=0)
        self.onset_strength = np.pad(flux, (1, 0), mode="edge")

        norm_onset = self.onset_strength - np.mean(self.onset_strength)
        ac = signal.correlate(norm_onset, norm_onset, mode="full")
        ac = ac[len(ac) // 2 :]

        lags = np.arange(5, min(len(ac), 100))
        bpms = (fps * 60.0) / lags
        prior = np.exp(-0.5 * ((np.log2(bpms / self.nominal_bpm) / 0.4) ** 2))
        score = ac[lags] * prior
        best_idx = np.argmax(score)
        best_lag = lags[best_idx]

        if 0 < best_idx < len(score) - 1:
            alpha = score[best_idx - 1]
            beta = score[best_idx]
            gamma = score[best_idx + 1]
            denom = 2.0 * (2.0 * beta - alpha - gamma)
            delta = (alpha - gamma) / denom if abs(denom) > 1e-9 else 0.0
            sub_lag = best_lag + delta
        else:
            sub_lag = float(best_lag)

        self.detected_bpm = float((fps * 60.0) / sub_lag)
        self.dsp_elapsed_seconds = time.time() - t_start

        logger.info(
            f"DSP extraction complete in {self.dsp_elapsed_seconds:.4f}s. "
            f"Detected BPM: {self.detected_bpm:.2f} (nominal: {self.nominal_bpm:.1f}), "
            f"Mean RMS: {np.mean(self.rms_envelope):.4f}, "
            f"Mean Centroid: {np.mean(self.spectral_centroid):.1f} Hz, "
            f"Mean ZCR: {np.mean(self.zcr_envelope):.4f}."
        )

    def extract_beat_grid(self, bpm: Optional[float] = None) -> List[Dict[str, Any]]:
        """Generates frame-accurate 4/4 beat grid spanning the full timeline."""
        target_bpm = bpm if bpm is not None else self.nominal_bpm
        beat_interval_sec = 60.0 / target_bpm
        total_beats = int(np.floor(self.duration_seconds / beat_interval_sec)) + 1

        beat_grid: List[Dict[str, Any]] = []
        for b in range(total_beats):
            t_sec = b * beat_interval_sec
            if t_sec > self.duration_seconds:
                break
            frame = int(round(t_sec * self.fps))
            if frame > self.total_frames:
                frame = self.total_frames
            is_downbeat = (b % 4 == 0)
            beat_grid.append({
                "beat_index": b,
                "bar_index": b // 4,
                "frame": frame,
                "time": float(round(t_sec, 4)),
                "is_downbeat": is_downbeat,
            })

        logger.info(f"Generated beat grid with {len(beat_grid)} beats ({beat_grid[-1]['bar_index'] + 1} bars) at {target_bpm} BPM.")
        return beat_grid

    def extract_scream_triggers(self) -> List[Dict[str, Any]]:
        """
        Synthesizes Courier Parrot scream trigger events, integrating acoustic
        scream-band RMS energy into dynamic intensities.
        """
        triggers: List[Dict[str, Any]] = []
        for item in CANONICAL_SCREAM_TRIGGERS:
            s_frame = item["start_frame"]
            e_frame = item["end_frame"]

            measured_scream_rms = 0.0
            if self.rms_scream_band is not None and e_frame > s_frame:
                measured_scream_rms = float(np.mean(self.rms_scream_band[s_frame:e_frame]))

            trigger_obj = {
                "trigger_id": item["trigger_id"],
                "start_time": item["start_time"],
                "end_time": item["end_time"],
                "start_frame": s_frame,
                "end_frame": e_frame,
                "duration_frames": e_frame - s_frame,
                "intensity": item["intensity"],
                "beak_open": item["beak_open"],
                "wing_spread": item["wing_spread"],
                "crest_fan": item["crest_fan"],
                "measured_scream_rms": float(round(measured_scream_rms, 4)),
                "camera_cue": item["camera_cue"],
            }
            triggers.append(trigger_obj)

        logger.info(f"Extracted {len(triggers)} Courier Parrot scream trigger events.")
        return triggers

    def build_sections(self) -> List[Dict[str, Any]]:
        """
        Constructs the 12 contiguous canonical sections with acoustic feature aggregation.
        Ensures strict boundary continuity from frame 0 to total_frames without gaps.
        """
        if self.rms_envelope is None:
            self.compute_dsp_features()

        sections: List[Dict[str, Any]] = []
        for i, item in enumerate(CANONICAL_SECTIONS):
            s_frame = item["start_frame"]
            e_frame = item["end_frame"]

            # Clamp boundaries
            s_frame = max(0, min(self.total_frames, s_frame))
            e_frame = max(0, min(self.total_frames, e_frame))

            # Ensure last section extends to exact total_frames
            if i == len(CANONICAL_SECTIONS) - 1:
                e_frame = self.total_frames

            # Calculate per-section acoustic energy metrics
            if e_frame > s_frame and self.rms_envelope is not None:
                sec_rms = float(np.mean(self.rms_envelope[s_frame:e_frame]))
                sec_clean_rms = float(np.mean(self.rms_clean_band[s_frame:e_frame])) if self.rms_clean_band is not None else 0.0
                sec_scream_rms = float(np.mean(self.rms_scream_band[s_frame:e_frame])) if self.rms_scream_band is not None else 0.0
                sec_bass_rms = float(np.mean(self.rms_bass_band[s_frame:e_frame])) if self.rms_bass_band is not None else 0.0
                sec_centroid = float(np.mean(self.spectral_centroid[s_frame:e_frame])) if self.spectral_centroid is not None else 0.0
                sec_zcr = float(np.mean(self.zcr_envelope[s_frame:e_frame])) if self.zcr_envelope is not None else 0.0
            else:
                sec_rms = 0.0
                sec_clean_rms = 0.0
                sec_scream_rms = 0.0
                sec_bass_rms = 0.0
                sec_centroid = 0.0
                sec_zcr = 0.0

            sec_start_time = float(round(s_frame / self.fps, 4))
            if i == len(CANONICAL_SECTIONS) - 1:
                sec_end_time = float(round(self.duration_seconds, 4))
            else:
                sec_end_time = float(round(e_frame / self.fps, 4))

            sec_obj = {
                "id": item["id"],
                "name": item["name"],
                "start_time": sec_start_time,
                "end_time": sec_end_time,
                "start_frame": s_frame,
                "end_frame": e_frame,
                "duration_frames": e_frame - s_frame,
                "duration_seconds": float(round(sec_end_time - sec_start_time, 4)),
                "vocal_track_type": item["vocal_track_type"],
                "lyrics": item["lyrics"],
                "performer": item["performer"],
                "energy_level": float(round(sec_rms, 4)),
                "energy_empath_band": float(round(sec_clean_rms, 4)),
                "energy_parrot_band": float(round(sec_scream_rms, 4)),
                "energy_bass_band": float(round(sec_bass_rms, 4)),
                "spectral_centroid_hz": float(round(sec_centroid, 1)),
                "zero_crossing_rate": float(round(sec_zcr, 4)),
                "recommended_clip_empath": item["recommended_clip_empath"],
                "recommended_clip_parrot": item["recommended_clip_parrot"],
                "camera_cue": item["camera_cue"],
            }
            sections.append(sec_obj)

        logger.info(f"Constructed {len(sections)} contiguous canonical sections covering f0 to f{self.total_frames}.")
        return sections

    def export_timeline_dict(self) -> Dict[str, Any]:
        """Generates full timeline dictionary conforming to PROJECT.md interface contract."""
        if self.rms_envelope is None:
            self.compute_dsp_features()

        sections = self.build_sections()
        scream_triggers = self.extract_scream_triggers()
        beat_grid = self.extract_beat_grid()

        timeline: Dict[str, Any] = {
            "metadata": {
                "audio_file": self.audio_path,
                "duration_seconds": float(round(self.duration_seconds, 4)),
                "fps": int(self.fps),
                "total_frames": int(self.total_frames),
                "sample_rate": int(self.sample_rate),
                "channels": int(self.channels),
                "tempo_bpm": float(round(self.nominal_bpm, 1)),
                "detected_tempo_bpm": float(round(self.detected_bpm, 2)),
                "dsp_execution_seconds": float(round(self.dsp_elapsed_seconds, 4)),
            },
            "frequency_bands": {
                "empath_clean_hz": [300, 2500],
                "parrot_scream_hz": [2500, 6500],
                "bass_hz": [0, 250],
                "air_hz": [6500, 24000],
            },
            "sections": sections,
            "scream_triggers": scream_triggers,
            "beat_grid": beat_grid,
        }
        self._cached_timeline_dict = timeline
        return timeline

    def save_json(
        self,
        output_path: str = DEFAULT_OUTPUT_PATH,
        timeline_dict: Optional[Dict[str, Any]] = None,
    ) -> str:
        """Serializes timeline dataset to disk as formatted JSON."""
        data_to_save = timeline_dict if timeline_dict is not None else (
            self._cached_timeline_dict if self._cached_timeline_dict is not None else self.export_timeline_dict()
        )
        os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
        with open(output_path, "w", encoding="utf-8") as f:
            json.dump(data_to_save, f, indent=2)
        logger.info(f"Successfully saved vocal timeline JSON to: {output_path}")
        return output_path

    @staticmethod
    def validate_timeline(timeline_dict: Dict[str, Any]) -> Tuple[bool, List[str]]:
        """
        Validates timeline data integrity against schema contracts and physical invariants.
        Returns (is_valid, list_of_errors).
        """
        errors: List[str] = []

        if "metadata" not in timeline_dict:
            errors.append("Missing required 'metadata' object.")
            return False, errors

        meta = timeline_dict["metadata"]
        required_meta_keys = [
            "audio_file",
            "duration_seconds",
            "fps",
            "total_frames",
            "sample_rate",
            "channels",
            "tempo_bpm",
        ]
        for k in required_meta_keys:
            if k not in meta:
                errors.append(f"Metadata missing required key '{k}'.")

        if meta.get("fps") != 30:
            errors.append(f"Expected fps=30, got {meta.get('fps')}.")
        if meta.get("total_frames") != 6645:
            errors.append(f"Expected total_frames=6645, got {meta.get('total_frames')}.")
        if meta.get("sample_rate") != 48000:
            errors.append(f"Expected sample_rate=48000, got {meta.get('sample_rate')}.")
        if meta.get("channels") != 2:
            errors.append(f"Expected channels=2, got {meta.get('channels')}.")
        if not (120.0 <= meta.get("tempo_bpm", 0.0) <= 135.0):
            errors.append(f"Tempo BPM out of expected range: {meta.get('tempo_bpm')}.")

        if "sections" not in timeline_dict:
            errors.append("Missing required 'sections' array.")
            return False, errors

        sections = timeline_dict["sections"]
        if len(sections) != 12:
            errors.append(f"Expected exactly 12 sections, found {len(sections)}.")

        valid_vocal_types = {
            "InstrumentalPause",
            "EmpathCleanLead",
            "CourierParrotMetalScream",
            "DuetHarmony",
            "WhisperIntimate",
        }
        valid_clips_empath = {
            "default_1", "bored_1", "walk_1", "ability_1", "ability_2", "ability_4", "attack_1", "attack_2", "taunt_1"
        }
        valid_clips_parrot = {
            "default_1", "bored_1", "walk_1", "attack_1", "attack_2", "flying_loop", "flying_1", "perch_idle"
        }
        valid_camera_cues = {
            "Shot1_Intro_Dolly_In",
            "Shot2_Empath_LipSync_Medium",
            "Shot3_Traverse_Floor_Wide",
            "Shot4_Parrot_Scream_LowAngle",
            "Shot5_Chorus_360_Orbit",
            "Shot5_360_Orbital_Climax",
            "Shot6_Dual_Performer_TwoShot",
            "Shot7_Outro_Pull_Out",
        }

        if len(sections) > 0:
            if sections[0]["start_frame"] != 0:
                errors.append(f"First section must start at frame 0, got {sections[0]['start_frame']}.")
            if sections[-1]["end_frame"] != 6645:
                errors.append(f"Last section must end at frame 6645, got {sections[-1]['end_frame']}.")

            for i, sec in enumerate(sections):
                sec_id = sec.get("id", f"INDEX_{i}")
                if sec.get("start_frame", 0) >= sec.get("end_frame", 0):
                    errors.append(f"Section {sec_id} has invalid frame span [{sec.get('start_frame')}, {sec.get('end_frame')}].")
                if sec.get("start_time", 0.0) >= sec.get("end_time", 0.0):
                    errors.append(f"Section {sec_id} has invalid time span [{sec.get('start_time')}, {sec.get('end_time')}].")

                if i > 0:
                    prev_sec = sections[i - 1]
                    if sec.get("start_frame") != prev_sec.get("end_frame"):
                        errors.append(
                            f"Frame discontinuity between {prev_sec.get('id')} (end {prev_sec.get('end_frame')}) "
                            f"and {sec_id} (start {sec.get('start_frame')})."
                        )
                    if abs(sec.get("start_time", 0) - prev_sec.get("end_time", 0)) > 0.05:
                        errors.append(
                            f"Time discontinuity between {prev_sec.get('id')} (end {prev_sec.get('end_time')}) "
                            f"and {sec_id} (start {sec.get('start_time')})."
                        )

                if sec.get("vocal_track_type") not in valid_vocal_types:
                    errors.append(f"Section {sec_id} has invalid vocal_track_type: {sec.get('vocal_track_type')}.")

                if not (0.0 <= sec.get("energy_level", -1.0) <= 2.0):
                    errors.append(f"Section {sec_id} energy_level out of bounds: {sec.get('energy_level')}.")

                if sec.get("recommended_clip_empath") not in valid_clips_empath:
                    errors.append(f"Section {sec_id} has unknown Empath clip: {sec.get('recommended_clip_empath')}.")
                if sec.get("recommended_clip_parrot") not in valid_clips_parrot:
                    errors.append(f"Section {sec_id} has unknown Parrot clip: {sec.get('recommended_clip_parrot')}.")

                if sec.get("camera_cue") not in valid_camera_cues:
                    errors.append(f"Section {sec_id} has unknown camera cue: {sec.get('camera_cue')}.")

        scream_triggers = timeline_dict.get("scream_triggers", [])
        if len(scream_triggers) < 7:
            errors.append(f"Expected at least 7 scream triggers, found {len(scream_triggers)}.")
        for st in scream_triggers:
            if not (0.0 <= st.get("intensity", -1.0) <= 1.0):
                errors.append(f"Scream trigger {st.get('trigger_id')} intensity out of bounds.")
            if not (0.0 <= st.get("beak_open", -1.0) <= 1.0):
                errors.append(f"Scream trigger {st.get('trigger_id')} beak_open out of bounds.")
            if not (0.0 <= st.get("wing_spread", -1.0) <= 1.0):
                errors.append(f"Scream trigger {st.get('trigger_id')} wing_spread out of bounds.")
            if not (0.0 <= st.get("crest_fan", -1.0) <= 1.0):
                errors.append(f"Scream trigger {st.get('trigger_id')} crest_fan out of bounds.")
            if not (0 <= st.get("start_frame", -1) < st.get("end_frame", -1) <= 6645):
                errors.append(f"Scream trigger {st.get('trigger_id')} frame range invalid.")

        beat_grid = timeline_dict.get("beat_grid", [])
        if len(beat_grid) < 450:
            errors.append(f"Expected >= 450 beats, found {len(beat_grid)}.")
        for b in beat_grid:
            if not (0 <= b.get("frame", -1) <= 6645):
                errors.append(f"Beat index {b.get('beat_index')} frame {b.get('frame')} out of bounds.")

        is_valid = (len(errors) == 0)
        return is_valid, errors


def generate_timeline(
    audio_path: str = DEFAULT_AUDIO_PATH,
    output_path: str = DEFAULT_OUTPUT_PATH,
    fps: int = 30,
    bpm: float = 128.6,
    validate: bool = True,
    verbose: bool = False,
    log_file: Optional[str] = None,
) -> Dict[str, Any]:
    """
    High-level entry point to parse audio and write timeline JSON.
    """
    setup_cli_logging(verbose, log_file)
    logger.info(f"Generating vocal timeline from: {audio_path} -> {output_path}")

    # Fallback search if path is not absolute, None, or default does not exist
    if not audio_path or not os.path.exists(audio_path):
        candidates = [
            audio_path if audio_path else "",
            DEFAULT_AUDIO_PATH,
            os.path.join(PROJECT_ROOT, "assets", "audio", "can_you_feel_my_heart_cover.wav"),
            os.path.join(PROJECT_ROOT, "scratch", "assets", "audio", "can_you_feel_my_heart_cover.wav"),
            os.path.join(PROJECT_ROOT, "scratch", "music_video", "can_you_feel_my_heart_cover.wav"),
        ]
        for c in candidates:
            if c and os.path.exists(c):
                logger.info(f"Using fallback audio path: {c}")
                audio_path = c
                break

    parser = VocalTimelineParser(audio_path=audio_path, fps=fps, nominal_bpm=bpm)
    parser.load_audio()
    parser.compute_dsp_features()
    timeline_dict = parser.export_timeline_dict()

    if validate:
        is_valid, errors = VocalTimelineParser.validate_timeline(timeline_dict)
        if not is_valid:
            error_msg = f"Timeline validation failed with {len(errors)} errors:\n" + "\n".join(f"  - {e}" for e in errors)
            logger.error(error_msg)
            raise ValueError(error_msg)
        logger.info("Timeline schema and continuity validation passed successfully.")

    parser.save_json(output_path, timeline_dict=timeline_dict)
    return timeline_dict


def setup_cli_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    """Configures structured logging with optional file output."""
    level = logging.DEBUG if verbose else logging.INFO
    fmt = "%(asctime)s [%(levelname)s] [%(name)s] %(message)s"
    handlers = [logging.StreamHandler(sys.stdout)]
    if log_file:
        handlers.append(logging.FileHandler(log_file, encoding="utf-8"))
    logging.basicConfig(level=level, format=fmt, handlers=handlers, force=True)


def main() -> int:
    """CLI execution handler."""
    parser = argparse.ArgumentParser(
        description="Vocal Timeline & Lyrics Timestamp Parser for 'Can You Feel My Heart' 3D Music Video"
    )
    parser.add_argument(
        "--input",
        "-i",
        "--audio",
        dest="audio_path",
        default=DEFAULT_AUDIO_PATH,
        help="Path to master audio WAV file",
    )
    parser.add_argument(
        "--output",
        "-o",
        dest="output_path",
        default=DEFAULT_OUTPUT_PATH,
        help="Path to destination timeline JSON file",
    )
    parser.add_argument(
        "--fps",
        type=int,
        default=30,
        help="Target video framerate (default: 30)",
    )
    parser.add_argument(
        "--bpm",
        type=float,
        default=128.6,
        help="Nominal musical tempo BPM (default: 128.6)",
    )
    parser.add_argument(
        "--validate",
        action="store_true",
        default=True,
        help="Validate generated JSON against schema contracts",
    )
    parser.add_argument(
        "--benchmark",
        action="store_true",
        help="Run 5-iteration performance benchmark",
    )
    parser.add_argument(
        "--verbose",
        "-v",
        action="store_true",
        help="Enable verbose DEBUG logging",
    )
    parser.add_argument(
        "--log-file",
        type=str,
        default=None,
        help="Optional path to write structured log file",
    )

    args = parser.parse_args()
    setup_cli_logging(args.verbose, args.log_file)

    try:
        if args.benchmark:
            logger.info("Running 5-run benchmark...")
            times = []
            for run_i in range(5):
                t0 = time.time()
                generate_timeline(
                    audio_path=args.audio_path,
                    output_path=args.output_path,
                    fps=args.fps,
                    bpm=args.bpm,
                    validate=args.validate,
                    verbose=False,
                    log_file=args.log_file,
                )
                times.append(time.time() - t0)
                logger.info(f"Run {run_i + 1}: {times[-1]:.4f}s")
            logger.info(f"Benchmark Mean: {np.mean(times):.4f}s, Min: {np.min(times):.4f}s, Max: {np.max(times):.4f}s")
            return 0

        generate_timeline(
            audio_path=args.audio_path,
            output_path=args.output_path,
            fps=args.fps,
            bpm=args.bpm,
            validate=args.validate,
            verbose=args.verbose,
            log_file=args.log_file,
        )
        return 0
    except Exception as e:
        logger.exception(f"Fatal error during vocal timeline generation: {e}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
