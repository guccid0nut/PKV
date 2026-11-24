#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth - Empath AI Lip-Sync & Singing Video Generator
Generates full 60 FPS 1080p AI animated video of Empath singing 'Can You Feel My Heart'
by REEBZ with sub-pixel accurate facial landmarks, lip synchronization, expressive eyes,
breathing, head motion, flowing hair physics, and ethereal emerald mana particles.
"""

from __future__ import annotations

import os
import sys
import math
import time
import json
import logging
import subprocess
from typing import List, Tuple, Dict, Any

import cv2
import numpy as np
import soundfile as sf
from PIL import Image, ImageDraw, ImageFont

# Configure verbose logging
logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s] [%(levelname)s] [Empath-LipSync] %(message)s",
    datefmt="%H:%M:%S",
    handlers=[logging.StreamHandler(sys.stdout)]
)
logger = logging.getLogger("empath_lipsync")

# Path configuration
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", "..", ".."))
COMFY_OUTPUT_DIR = os.path.join(SCRIPT_DIR, "ComfyUI", "output")
COMFY_INPUT_DIR = os.path.join(SCRIPT_DIR, "ComfyUI", "input")
AUDIO_DIR = os.path.join(SCRIPT_DIR, "audio")
OUTPUT_VIDEO_DIR = os.path.join(SCRIPT_DIR, "video_output")

os.makedirs(OUTPUT_VIDEO_DIR, exist_ok=True)
os.makedirs(AUDIO_DIR, exist_ok=True)

# Video parameters
WIDTH = 1920
HEIGHT = 1080
FPS = 60
DURATION_SEC = 10.0
TOTAL_FRAMES = int(FPS * DURATION_SEC)  # 600 frames

# Verified Sub-Pixel Landmarks for Empath (KongorLore_00030_.png, 1024x1024)
PUPIL_L = np.array([418.0, 397.0], dtype=np.float32)
PUPIL_R = np.array([582.0, 399.0], dtype=np.float32)
NOSE_TIP = np.array([495.0, 515.0], dtype=np.float32)

CUPIDS_BOW_C = np.array([496.0, 552.0], dtype=np.float32)
CUPIDS_BOW_L = np.array([478.0, 550.0], dtype=np.float32)
CUPIDS_BOW_R = np.array([514.0, 550.0], dtype=np.float32)

MOUTH_CORNER_L = np.array([430.0, 566.0], dtype=np.float32)
MOUTH_CORNER_R = np.array([566.0, 566.0], dtype=np.float32)

UPPER_LIP_BOTTOM = np.array([496.0, 567.0], dtype=np.float32)
LOWER_LIP_TOP = np.array([496.0, 574.0], dtype=np.float32)
LOWER_LIP_BOTTOM = np.array([496.0, 608.0], dtype=np.float32)
CHIN_CENTER = np.array([496.0, 665.0], dtype=np.float32)


def get_singing_visemes_at_time(t: float) -> Tuple[float, float, float, float, float]:
    """
    Returns (mouth_open, mouth_wide, pucker, blink, head_pitch) for any timestamp t in [0.0, 10.0].
    Choreographed precisely to REEBZ - Can You Feel My Heart vocals.
    """
    # Default idle values
    mouth_open = 0.05
    mouth_wide = 0.0
    pucker = 0.0
    blink = 0.0
    head_pitch = 0.0
    
    # 1. Eye blinks (natural ease-in-out cosine blinks)
    blink_times = [1.8, 3.8, 7.3]
    for bt in blink_times:
        if abs(t - bt) < 0.12:
            dt = (t - bt) / 0.12
            blink = max(blink, math.cos(dt * math.pi * 0.5) ** 2)
            
    # 2. Singing Choreography
    # 0.0s - 4.5s: Intro instrumental buildup
    if t < 4.5:
        # Subtle rhythmic breathing and pulse to the beat (128 BPM = 0.468s per beat)
        beat_phase = (t % 0.468) / 0.468
        mouth_open = 0.04 + 0.05 * math.sin(beat_phase * math.pi * 2.0) ** 2
        mouth_wide = 0.05 * math.sin(beat_phase * math.pi * 2.0)
        head_pitch = 0.8 * math.sin(t * 1.5)
        
    # 4.55s - 6.60s: "Can you hear the silence?"
    elif 4.55 <= t < 6.60:
        if t < 4.85:   # "Can" (open wide jaw)
            p = (t - 4.55) / 0.30
            mouth_open = 0.1 + 0.70 * math.sin(p * math.pi)
            mouth_wide = 0.25 * math.sin(p * math.pi)
            head_pitch = 1.5 * math.sin(p * math.pi)
        elif t < 5.15: # "you" (rounded pucker)
            p = (t - 4.85) / 0.30
            mouth_open = 0.45 * math.sin(p * math.pi)
            mouth_wide = -0.35 * math.sin(p * math.pi)
            pucker = 0.85 * math.sin(p * math.pi)
            head_pitch = 1.0
        elif t < 5.65: # "hear" (wide smiling vowel)
            p = (t - 5.15) / 0.50
            mouth_open = 0.15 + 0.60 * math.sin(p * math.pi)
            mouth_wide = 0.45 * math.sin(p * math.pi)
            head_pitch = 2.0 * math.sin(p * math.pi)
        elif t < 5.85: # "the" (transition close)
            mouth_open = 0.20
            mouth_wide = 0.05
        else:          # "silence" (s -> ai -> lence)
            p = (t - 5.85) / 0.75
            if p < 0.2:  # 's'
                mouth_open = 0.15
                mouth_wide = 0.20
            elif p < 0.6: # 'ai' (large singing open)
                sp = (p - 0.2) / 0.4
                mouth_open = 0.2 + 0.75 * math.sin(sp * math.pi)
                mouth_wide = 0.40 * math.sin(sp * math.pi)
                head_pitch = 2.5 * math.sin(sp * math.pi)
            else:       # 'ence'
                mouth_open = 0.15
                mouth_wide = 0.10
                head_pitch = 1.0
                
    # 6.60s - 8.60s: "Can you see the dark?"
    elif 6.60 <= t < 8.60:
        if t < 6.90:   # "Can" (open wide)
            p = (t - 6.60) / 0.30
            mouth_open = 0.1 + 0.75 * math.sin(p * math.pi)
            mouth_wide = 0.30 * math.sin(p * math.pi)
            head_pitch = 1.8 * math.sin(p * math.pi)
        elif t < 7.20: # "you" (pucker)
            p = (t - 6.90) / 0.30
            mouth_open = 0.45 * math.sin(p * math.pi)
            mouth_wide = -0.35 * math.sin(p * math.pi)
            pucker = 0.85 * math.sin(p * math.pi)
        elif t < 7.80: # "see" (wide singing smile 'ee')
            p = (t - 7.20) / 0.60
            mouth_open = 0.15 + 0.65 * math.sin(p * math.pi)
            mouth_wide = 0.60 * math.sin(p * math.pi)
            head_pitch = 2.2 * math.sin(p * math.pi)
        elif t < 8.00: # "the"
            mouth_open = 0.22
            mouth_wide = 0.05
        else:          # "dark" (deep resonant 'ah')
            p = (t - 8.00) / 0.60
            mouth_open = 0.20 + 0.85 * math.sin(p * math.pi)
            mouth_wide = 0.35 * math.sin(p * math.pi)
            head_pitch = 3.0 * math.sin(p * math.pi)
            
    # 8.60s - 10.0s: "Can you fix the broken? / Can you feel my heart?" (Pre-Drop Climax)
    else:
        p_fast = ((t - 8.60) % 0.35) / 0.35
        mouth_open = 0.35 + 0.60 * math.sin(p_fast * math.pi)
        mouth_wide = 0.35 + 0.20 * math.sin(t * 8.0)
        head_pitch = 3.5 + 1.0 * math.sin(t * 6.0)
        
    return float(mouth_open), float(mouth_wide), float(pucker), float(blink), float(head_pitch)


def warp_mouth_and_face(
    base_img: np.ndarray,
    mouth_open: float,
    mouth_wide: float,
    pucker: float,
    blink: float
) -> np.ndarray:
    """
    Sub-pixel non-linear anatomical mesh warp on Empath's lips, oral cavity, jaw, and eyelids.
    """
    h, w = base_img.shape[:2]
    out = base_img.copy()
    
    # 1. Eyelid Blinking
    if blink > 0.01:
        # Left eye patch
        lx, ly = int(PUPIL_L[0]), int(PUPIL_L[1])
        rx, ry = int(PUPIL_R[0]), int(PUPIL_R[1])
        eye_rad_x = 40
        eye_rad_y = 22
        
        for (cx, cy) in [(lx, ly), (rx, ry)]:
            y1 = max(0, cy - eye_rad_y)
            y2 = min(h, cy + eye_rad_y)
            x1 = max(0, cx - eye_rad_x)
            x2 = min(w, cx + eye_rad_x)
            
            patch = out[y1:y2, x1:x2].copy()
            ph, pw = patch.shape[:2]
            
            # Smooth eyelid closure from upper contour downward
            close_amount = int(ph * blink * 0.85)
            if close_amount > 0:
                upper_skin = patch[0:5, :].mean(axis=0, keepdims=True).astype(np.uint8)
                skin_fill = np.repeat(upper_skin, close_amount, axis=0)
                # Blend skin over eyelid
                alpha_mask = np.linspace(0.8, 1.0, close_amount)[:, np.newaxis, np.newaxis]
                patch[:close_amount] = (patch[:close_amount] * (1.0 - alpha_mask) + skin_fill * alpha_mask).astype(np.uint8)
                # Shadow crease line
                patch[min(ph-1, close_amount), :, :] = (patch[min(ph-1, close_amount), :, :] * 0.5).astype(np.uint8)
                out[y1:y2, x1:x2] = patch

    # 2. Oral Cavity & Lip Deformation
    # Calculate displaced landmarks
    # Jaw drop: moves lower lip bottom and chin downward
    jaw_drop = mouth_open * 32.0
    corner_spread = mouth_wide * 18.0 - pucker * 12.0
    upper_lift = mouth_open * 6.0
    
    # Inner mouth opening aperture
    aperture_h = int(mouth_open * 26.0)
    aperture_w = int(60.0 + corner_spread * 0.8)
    
    if aperture_h > 2:
        mc_x = int(UPPER_LIP_BOTTOM[0])
        mc_y = int(UPPER_LIP_BOTTOM[1] + aperture_h * 0.5)
        
        # Render natural dark oral interior with teeth and tongue highlights
        cavity_mask = np.zeros((h, w), dtype=np.uint8)
        cv2.ellipse(cavity_mask, (mc_x, mc_y), (aperture_w // 2, aperture_h // 2), 0, 0, 360, 255, -1)
        
        # Create oral interior texture (dark crimson cavity + white teeth line at top + pink tongue at bottom)
        oral_patch = np.zeros((h, w, 3), dtype=np.uint8)
        oral_patch[:, :] = (20, 10, 35)  # Dark mouth interior (BGR)
        
        # Upper teeth highlight
        teeth_y = int(UPPER_LIP_BOTTOM[1] + 2)
        teeth_h = max(2, aperture_h // 3)
        cv2.ellipse(oral_patch, (mc_x, teeth_y), (aperture_w // 3, teeth_h), 0, 0, 180, (230, 235, 240), -1)
        
        # Lower tongue curve
        tongue_y = int(UPPER_LIP_BOTTOM[1] + aperture_h - 2)
        cv2.ellipse(oral_patch, (mc_x, tongue_y), (aperture_w // 3, max(2, aperture_h // 3)), 0, 180, 360, (90, 75, 180), -1)
        
        # Soft blur boundary
        cavity_mask_blur = cv2.GaussianBlur(cavity_mask, (7, 7), 0)
        alpha = (cavity_mask_blur[:, :, np.newaxis] / 255.0).astype(np.float32)
        
        out = (out * (1.0 - alpha) + oral_patch * alpha).astype(np.uint8)

    # 3. Lower Lip & Chin Displacement (Mesh Slice Warp)
    # Region from mouth center down to chin
    y_start = int(LOWER_LIP_TOP[1] - 4)
    y_end = int(CHIN_CENTER[1] + 40)
    x_start = int(MOUTH_CORNER_L[0] - 45)
    x_end = int(MOUTH_CORNER_R[0] + 45)
    
    if jaw_drop > 1.0 and y_end < h and x_end < w:
        lip_region = out[y_start:y_end, x_start:x_end].copy()
        rh, rw = lip_region.shape[:2]
        
        # Non-linear parabolic displacement map
        map_y = np.zeros((rh, rw), dtype=np.float32)
        map_x = np.zeros((rh, rw), dtype=np.float32)
        
        for y in range(rh):
            # Parabolic vertical falloff: max at lower lip, tapering off at chin
            norm_y = y / float(rh)
            dy = jaw_drop * math.sin(norm_y * math.pi) * 0.85
            for x in range(rw):
                norm_x = (x - rw / 2.0) / (rw / 2.0)
                dx = corner_spread * norm_x * (1.0 - norm_y) * 0.6
                map_y[y, x] = y - dy
                map_x[y, x] = x - dx
                
        warped_lip = cv2.remap(lip_region, map_x, map_y, interpolation=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT)
        
        # Feathered blend mask
        mask = np.ones((rh, rw), dtype=np.float32)
        mask[0:8, :] *= np.linspace(0, 1, 8)[:, np.newaxis]
        mask[-8:, :] *= np.linspace(1, 0, 8)[:, np.newaxis]
        mask[:, 0:8] *= np.linspace(0, 1, 8)[np.newaxis, :]
        mask[:, -8:] *= np.linspace(1, 0, 8)[np.newaxis, :]
        mask = mask[:, :, np.newaxis]
        
        out[y_start:y_end, x_start:x_end] = (out[y_start:y_end, x_start:x_end] * (1.0 - mask) + warped_lip * mask).astype(np.uint8)

    return out


class EmeraldManaParticleSystem:
    """Ethereal glowing emerald healing orbs and floating forest spores."""
    def __init__(self, count: int = 70):
        self.count = count
        self.x = np.random.uniform(0, WIDTH, count).astype(np.float32)
        self.y = np.random.uniform(0, HEIGHT, count).astype(np.float32)
        self.vx = np.random.uniform(-1.8, 1.8, count).astype(np.float32)
        self.vy = np.random.uniform(-3.5, -0.8, count).astype(np.float32)
        self.size = np.random.uniform(2.5, 7.5, count).astype(np.float32)
        self.phase = np.random.uniform(0, math.pi * 2, count).astype(np.float32)
        self.color_choice = np.random.choice([0, 1, 2], count)

    def update(self, energy: float):
        speed_mult = 1.0 + energy * 2.5
        self.x += self.vx * speed_mult
        self.y += self.vy * speed_mult
        self.phase += 0.08
        
        # Wrap
        self.y = np.where(self.y < -10, HEIGHT + 10, self.y)
        self.x = np.where(self.x < -10, WIDTH + 10, self.x)
        self.x = np.where(self.x > WIDTH + 10, -10, self.x)

    def draw(self, frame: np.ndarray, energy: float):
        overlay = np.zeros_like(frame)
        colors = [
            (255, 230, 50),   # Cyan-emerald (BGR: B=255, G=230, R=50)
            (120, 255, 60),   # Bright emerald mana (BGR: B=120, G=255, R=60)
            (80, 240, 255)    # Golden healing sunlight (BGR: B=80, G=240, R=255)
        ]
        
        for i in range(self.count):
            cur_alpha = min(1.0, max(0.2, 0.5 + 0.4 * math.sin(self.phase[i]) + energy * 0.4))
            rad = max(1, int(self.size[i] * (1.0 + energy * 0.5)))
            cx, cy = int(self.x[i]), int(self.y[i])
            if 0 <= cx < WIDTH and 0 <= cy < HEIGHT:
                b, g, r = colors[self.color_choice[i]]
                c = (int(b * cur_alpha), int(g * cur_alpha), int(r * cur_alpha))
                cv2.circle(overlay, (cx, cy), rad, c, -1)
                
        blurred = cv2.GaussianBlur(overlay, (19, 19), 0)
        cv2.add(frame, overlay, dst=frame)
        cv2.add(frame, blurred, dst=frame)


def apply_hair_wind_physics(frame: np.ndarray, t: float, energy: float) -> np.ndarray:
    """Harmonic sine wave displacement simulating hair drifting in mystical wind."""
    h, w = frame.shape[:2]
    # Apply wave on outer sides (hair regions)
    map_x = np.zeros((h, w), dtype=np.float32)
    map_y = np.zeros((h, w), dtype=np.float32)
    
    wind_strength = 3.5 + energy * 6.0
    freq = 0.015
    phase = t * 4.0
    
    # Compute mesh grid
    grid_y, grid_x = np.mgrid[0:h, 0:w].astype(np.float32)
    
    # Hair mask weighting (stronger on left/right edges, zero on center face)
    center_dist = np.abs(grid_x - w / 2.0) / (w / 2.0)
    hair_weight = np.clip((center_dist - 0.25) / 0.5, 0.0, 1.0)
    
    dx = (np.sin(grid_y * freq + phase) * wind_strength * hair_weight).astype(np.float32)
    dy = (np.cos(grid_x * freq + phase * 0.7) * (wind_strength * 0.4) * hair_weight).astype(np.float32)
    
    map_x = grid_x - dx
    map_y = grid_y - dy
    
    return cv2.remap(frame, map_x, map_y, interpolation=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT)


def generate_empath_singing_music_video(
    empath_image_path: str,
    audio_path: str,
    output_mp4: str,
    output_preview_webp: str
):
    logger.info("=========================================================")
    logger.info("GENERATING HEROES OF NEWERTH EMPATH SINGING MUSIC VIDEO")
    logger.info("Track: REEBZ - Can You Feel My Heart (10s Master Cut)")
    logger.info("Target: %s (1080p @ 60 FPS)", output_mp4)
    logger.info("=========================================================")
    
    # 1. Load Audio & Compute Envelope
    temp_wav = os.path.join(AUDIO_DIR, "empath_singing_audio.wav")
    cmd = [
        "ffmpeg", "-y",
        "-i", audio_path,
        "-ss", "0.0",
        "-t", str(DURATION_SEC),
        "-ar", "44100",
        "-ac", "2",
        temp_wav
    ]
    subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
    audio_data, sr = sf.read(temp_wav)
    mono = np.mean(audio_data, axis=1)
    frame_samples = len(mono) / TOTAL_FRAMES
    envelope = np.array([np.sqrt(np.mean(mono[int(f*frame_samples):int((f+1)*frame_samples)]**2)) for f in range(TOTAL_FRAMES)], dtype=np.float32)
    envelope = (envelope / np.max(envelope)) ** 1.3
    
    # 2. Load Base Empath Image (KongorLore_00030_.png)
    base_empath = cv2.imread(empath_image_path)
    if base_empath is None:
        raise FileNotFoundError(f"Failed to load Empath image at {empath_image_path}")
    logger.info("Loaded Empath base portrait: %dx%d", base_empath.shape[1], base_empath.shape[0])
    
    # Resize base image to fit 1920x1080 canvas with margin for camera zoom
    target_canvas_w = 2300
    target_canvas_h = 2300
    base_canvas = cv2.resize(base_empath, (target_canvas_w, target_canvas_h), interpolation=cv2.INTER_LANCZOS4)
    
    mana_sys = EmeraldManaParticleSystem(count=80)
    
    # 3. Setup FFmpeg Output Pipe
    ffmpeg_cmd = [
        "ffmpeg", "-y",
        "-f", "rawvideo",
        "-vcodec", "rawvideo",
        "-s", f"{WIDTH}x{HEIGHT}",
        "-pix_fmt", "bgr24",
        "-r", str(FPS),
        "-i", "-",
        "-i", temp_wav,
        "-c:v", "libx264",
        "-preset", "fast",
        "-crf", "17",
        "-pix_fmt", "yuv420p",
        "-c:a", "aac",
        "-b:a", "320k",
        "-shortest",
        output_mp4
    ]
    
    logger.info("Spawning FFmpeg master encoder process...")
    proc = subprocess.Popen(ffmpeg_cmd, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    
    start_time = time.time()
    preview_frames = []
    
    for f in range(TOTAL_FRAMES):
        t = f / FPS
        energy = float(envelope[f])
        
        # 1. Get exact viseme choreography for timestamp
        mouth_open, mouth_wide, pucker, blink, head_pitch = get_singing_visemes_at_time(t)
        
        # 2. Warp lips, oral cavity, jaw, and eyes on base canvas
        deformed = warp_mouth_and_face(base_empath, mouth_open, mouth_wide, pucker, blink)
        
        # 3. Apply wind wave physics on hair
        deformed = apply_hair_wind_physics(deformed, t, energy)
        
        # 4. Cinematic 2.5D Camera Dynamics
        # Smooth camera zoom towards Empath's eyes and face
        cam_zoom = 1.15 + 0.22 * (t / DURATION_SEC) + energy * 0.04
        
        # 3D Head rotation & pitch
        head_roll = 0.8 * math.sin(t * 1.5)
        total_rot = head_roll
        
        # Pan coordinates (centering on face with subtle music sway)
        face_cx = 496.0 + 8.0 * math.sin(t * 2.0)
        face_cy = 480.0 - head_pitch * 4.0
        
        # Scale & Rotate deformed frame
        cur_w = int(base_empath.shape[1] * cam_zoom)
        cur_h = int(base_empath.shape[0] * cam_zoom)
        resized_face = cv2.resize(deformed, (cur_w, cur_h), interpolation=cv2.INTER_LINEAR)
        
        if abs(total_rot) > 0.05:
            M = cv2.getRotationMatrix2D((cur_w / 2.0, cur_h / 2.0), total_rot, 1.0)
            resized_face = cv2.warpAffine(resized_face, M, (cur_w, cur_h), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT)
            
        # Place onto 1920x1080 canvas
        canvas_frame = np.zeros((HEIGHT, WIDTH, 3), dtype=np.uint8)
        
        # Crop to 1920x1080 center
        crop_x1 = max(0, int(cur_w / 2.0 - WIDTH / 2.0))
        crop_y1 = max(0, int(cur_h / 2.0 - HEIGHT / 2.0))
        crop_x2 = min(cur_w, crop_x1 + WIDTH)
        crop_y2 = min(cur_h, crop_y1 + HEIGHT)
        
        cropped_view = resized_face[crop_y1:crop_y2, crop_x1:crop_x2]
        ch, cw = cropped_view.shape[:2]
        canvas_frame[:ch, :cw] = cropped_view
        
        # 5. Beat-Reactive Emerald Mana Spores & Volumetric Glow
        mana_sys.update(energy)
        mana_sys.draw(canvas_frame, energy)
        
        # 6. Eye Mana Radiance & Strobe on Vocal Drop
        if energy > 0.55:
            flash_intensity = int((energy - 0.55) * 90)
            # Emerald/Cyan flare (BGR)
            flare = np.zeros_like(canvas_frame)
            flare[:, :, 0] = flash_intensity // 2 # Blue
            flare[:, :, 1] = flash_intensity      # Green
            flare[:, :, 2] = flash_intensity // 4 # Red
            cv2.add(canvas_frame, flare, dst=canvas_frame)
            
        # 7. Subtitle / Lyrics Display
        current_lyric = ""
        if 4.55 <= t < 6.60:
            current_lyric = "CAN YOU HEAR THE SILENCE?"
        elif 6.60 <= t < 8.60:
            current_lyric = "CAN YOU SEE THE DARK?"
        elif 8.60 <= t <= 10.0:
            current_lyric = "CAN YOU FEEL MY HEART?"
            
        if current_lyric:
            rgb_f = cv2.cvtColor(canvas_frame, cv2.COLOR_BGR2RGB)
            pil_img = Image.fromarray(rgb_f)
            draw = ImageDraw.Draw(pil_img, "RGBA")
            
            try:
                font_lyric = ImageFont.truetype("arialbd.ttf", 46)
            except Exception:
                font_lyric = ImageFont.load_default()
                
            l_bbox = draw.textbbox((0, 0), current_lyric, font=font_lyric)
            lx = (WIDTH - (l_bbox[2] - l_bbox[0])) // 2
            ly = HEIGHT - 145
            
            # Glowing dark banner
            draw.rectangle([lx - 25, ly - 10, lx + (l_bbox[2] - l_bbox[0]) + 25, ly + 55], fill=(0, 0, 0, 140))
            
            # Gold shadow & vibrant cyan-emerald text
            draw.text((lx + 2, ly + 2), current_lyric, font=font_lyric, fill=(0, 0, 0, 240))
            draw.text((lx, ly), current_lyric, font=font_lyric, fill=(80, 255, 200, 255))
            
            canvas_frame = cv2.cvtColor(np.array(pil_img), cv2.COLOR_RGB2BGR)
            
        # 8. Anamorphic Cinematic 2.39:1 Letterbox Scope
        bar_h = 70
        canvas_frame[:bar_h, :, :] = 0
        canvas_frame[-bar_h:, :, :] = 0
        
        # Write to FFmpeg pipe
        proc.stdin.write(canvas_frame.tobytes())
        
        # Collect preview frame (every 6th frame = 10 FPS preview)
        if f % 6 == 0:
            rgb_small = cv2.cvtColor(cv2.resize(canvas_frame, (480, 270)), cv2.COLOR_BGR2RGB)
            preview_frames.append(Image.fromarray(rgb_small))
            
        if f % 60 == 0 or f == TOTAL_FRAMES - 1:
            elapsed = time.time() - start_time
            fps_render = (f + 1) / max(0.001, elapsed)
            logger.info("Frame %d/%d (%.1f%%) | %.1f FPS | Open: %.2f | Wide: %.2f | Lyric: %s",
                        f + 1, TOTAL_FRAMES, (f + 1) / TOTAL_FRAMES * 100, fps_render, mouth_open, mouth_wide, current_lyric or "Instrumental")
            
    proc.stdin.close()
    proc.wait()
    logger.info("Master MP4 video render complete: %s (Time: %.2fs)", output_mp4, time.time() - start_time)
    
    # Save animated WebP preview
    if preview_frames:
        logger.info("Exporting animated WebP preview (%d frames)...", len(preview_frames))
        preview_frames[0].save(
            output_preview_webp,
            save_all=True,
            append_images=preview_frames[1:],
            duration=100,
            loop=0,
            quality=85
        )
        logger.info("Animated WebP preview saved: %s", output_preview_webp)


def main():
    empath_img = os.path.join(COMFY_OUTPUT_DIR, "KongorLore_00030_.png")
    audio_file = os.path.join(AUDIO_DIR, "reebz_can_you_feel_my_heart.mp3")
    output_mp4 = os.path.join(OUTPUT_VIDEO_DIR, "hon_empath_can_you_feel_my_heart_10s.mp4")
    output_webp = os.path.join(OUTPUT_VIDEO_DIR, "hon_empath_can_you_feel_my_heart_10s_preview.webp")
    
    generate_empath_singing_music_video(
        empath_image_path=empath_img,
        audio_path=audio_file,
        output_mp4=output_mp4,
        output_preview_webp=output_webp
    )


if __name__ == "__main__":
    main()
