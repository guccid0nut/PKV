#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth - 3D Empath Singing & Lip-Sync Video Synthesizer (ComfyUI 3D Engine)
Synthesizes a full 60 FPS 1080p photorealistic 3D CGI video of Empath singing 'Can You Feel My Heart'
using the 3D CGI multi-keyframe lattice generated in ComfyUI (SDXL Unreal Engine 5 / Octane renders).
"""

from __future__ import annotations

import os
import sys
import math
import time
import subprocess
import logging
from typing import List, Tuple, Dict

import cv2
import numpy as np
import soundfile as sf
from PIL import Image, ImageDraw, ImageFont

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s] [%(levelname)s] [Empath-3D-LipSync] %(message)s",
    datefmt="%H:%M:%S",
    handlers=[logging.StreamHandler(sys.stdout)]
)
logger = logging.getLogger("empath_3d_lipsync")

# Path setup
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
COMFY_OUTPUT_DIR = os.path.join(SCRIPT_DIR, "ComfyUI", "output")
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

import json

# 3D CGI Source Keyframes generated from ComfyUI
KEYFRAME_IDLE = os.path.join(COMFY_OUTPUT_DIR, "KongorLore_00032_.png")
KEYFRAME_BELTING = os.path.join(COMFY_OUTPUT_DIR, "KongorLore_00033_.png")
KEYFRAME_PUCKER = os.path.join(COMFY_OUTPUT_DIR, "KongorLore_00034_.png")
KEYFRAME_SMILE = os.path.join(COMFY_OUTPUT_DIR, "KongorLore_00035_.png")

# Load calibrated landmarks from empath_landmarks.json
LANDMARKS_FILE = os.path.join(SCRIPT_DIR, "empath_landmarks.json")

def get_calibrated_landmarks():
    with open(LANDMARKS_FILE, "r", encoding="utf-8") as f:
        data = json.load(f)
    return {k: np.array([float(v["x"]), float(v["y"])], dtype=np.float32) for k, v in data.items()}

LANDMARKS = get_calibrated_landmarks()
logger.info("Loaded custom calibrated landmarks from %s", LANDMARKS_FILE)


def compute_3d_viseme_weights(t: float) -> Dict[str, float]:
    """
    Computes precise blending weights between the 3D CGI keyframe states at timestamp t.
    Returns dict with weights: idle, belting, pucker, smile, blink, head_pitch, head_yaw
    """
    w_idle = 1.0
    w_belting = 0.0
    w_pucker = 0.0
    w_smile = 0.0
    blink = 0.0
    head_pitch = 0.0
    head_yaw = 0.0
    
    # 1. Natural 3D Eye Blinks
    blink_times = [1.8, 3.8, 7.3]
    for bt in blink_times:
        if abs(t - bt) < 0.14:
            dt = (t - bt) / 0.14
            blink = max(blink, math.cos(dt * math.pi * 0.5) ** 2)

    # 2. Singing Choreography for REEBZ - Can You Feel My Heart
    # 0.0s - 4.5s: Intro instrumental buildup
    if t < 4.5:
        beat = (t % 0.468) / 0.468
        w_idle = 1.0
        head_pitch = 1.2 * math.sin(t * 1.5)
        head_yaw = 1.0 * math.sin(t * 1.0)
        
    # 4.55s - 6.60s: "CAN YOU HEAR THE SILENCE?"
    elif 4.55 <= t < 6.60:
        if t < 4.85:   # "Can" -> Wide 3D belting opening
            p = (t - 4.55) / 0.30
            curve = math.sin(p * math.pi)
            w_belting = 0.85 * curve
            w_idle = 1.0 - w_belting
            head_pitch = 2.5 * curve
        elif t < 5.15: # "you" -> 3D pucker
            p = (t - 4.85) / 0.30
            curve = math.sin(p * math.pi)
            w_pucker = 0.90 * curve
            w_idle = 1.0 - w_pucker
            head_pitch = 1.5
        elif t < 5.65: # "hear" -> 3D wide singing smile
            p = (t - 5.15) / 0.50
            curve = math.sin(p * math.pi)
            w_smile = 0.85 * curve
            w_idle = 1.0 - w_smile
            head_pitch = 3.0 * curve
        elif t < 5.85: # "the" -> semi-idle transition
            w_idle = 0.80
            w_smile = 0.20
            head_pitch = 1.0
        else:          # "silence" -> belting into high vocal resonance
            p = (t - 5.85) / 0.75
            if p < 0.2:
                w_smile = 0.40
                w_idle = 0.60
            elif p < 0.65:
                sp = (p - 0.2) / 0.45
                curve = math.sin(sp * math.pi)
                w_belting = 0.95 * curve
                w_idle = 1.0 - w_belting
                head_pitch = 4.0 * curve
            else:
                w_idle = 0.70
                w_smile = 0.30
                head_pitch = 1.5
                
    # 6.60s - 8.60s: "CAN YOU SEE THE DARK?"
    elif 6.60 <= t < 8.60:
        if t < 6.90:   # "Can" -> Belting
            p = (t - 6.60) / 0.30
            curve = math.sin(p * math.pi)
            w_belting = 0.90 * curve
            w_idle = 1.0 - w_belting
            head_pitch = 2.8 * curve
        elif t < 7.20: # "you" -> Pucker
            p = (t - 6.90) / 0.30
            curve = math.sin(p * math.pi)
            w_pucker = 0.95 * curve
            w_idle = 1.0 - w_pucker
            head_pitch = 1.8
        elif t < 7.80: # "see" -> Maximum 3D wide singing smile
            p = (t - 7.20) / 0.60
            curve = math.sin(p * math.pi)
            w_smile = 1.00 * curve
            w_idle = 1.0 - w_smile
            head_pitch = 3.2 * curve
        elif t < 8.00: # "the"
            w_idle = 0.75
            w_smile = 0.25
        else:          # "dark" -> Deep resonant 3D belting opening with maximum vocal power
            p = (t - 8.00) / 0.60
            curve = math.sin(p * math.pi)
            w_belting = 1.00 * curve
            w_idle = 1.0 - w_belting
            head_pitch = 4.5 * curve
            head_yaw = -1.5 * curve
            
    # 8.60s - 10.00s: "CAN YOU FEEL MY HEART?" (Pre-Drop Climax)
    else:
        p_fast = ((t - 8.60) % 0.35) / 0.35
        curve = math.sin(p_fast * math.pi)
        w_belting = 0.65 + 0.35 * curve
        w_smile = 0.20 * math.sin(t * 6.0)
        w_idle = max(0.0, 1.0 - w_belting - w_smile)
        head_pitch = 4.0 + 1.2 * math.sin(t * 8.0)
        head_yaw = 2.0 * math.sin(t * 4.0)

    # Normalize weights
    tot = max(0.001, w_idle + w_belting + w_pucker + w_smile)
    return {
        "idle": w_idle / tot,
        "belting": w_belting / tot,
        "pucker": w_pucker / tot,
        "smile": w_smile / tot,
        "blink": float(blink),
        "head_pitch": float(head_pitch),
        "head_yaw": float(head_yaw)
    }


def blend_3d_face_mesh(
    img_idle: np.ndarray,
    img_belting: np.ndarray,
    img_pucker: np.ndarray,
    img_smile: np.ndarray,
    weights: Dict[str, float]
) -> np.ndarray:
    """
    Seamless volumetric blending between 3D CGI keyframe states using user-calibrated landmarks.
    Preserves outer clothing, shoulders, and hood stability while animating realistic 3D mouth,
    jaw, teeth, and facial muscle kinematics.
    """
    h, w = img_idle.shape[:2]
    
    # 1. Base linear blend
    blended = (
        img_idle.astype(np.float32) * weights["idle"] +
        img_belting.astype(np.float32) * weights["belting"] +
        img_pucker.astype(np.float32) * weights["pucker"] +
        img_smile.astype(np.float32) * weights["smile"]
    )
    
    # 2. Localized mouth region mask based on exact calibrated landmarks
    mc_x = int(LANDMARKS.get("upper_lip_bottom", np.array([512.0, 497.0]))[0])
    mc_y = int(LANDMARKS.get("upper_lip_bottom", np.array([512.0, 497.0]))[1])
    
    mouth_w = int(abs(LANDMARKS["mouth_corner_r"][0] - LANDMARKS["mouth_corner_l"][0]) * 0.9)
    mouth_h = int(abs(LANDMARKS["lower_lip_bottom"][1] - LANDMARKS["cupids_bow_c"][1]) * 2.2)
    
    mouth_mask = np.zeros((h, w), dtype=np.float32)
    cv2.ellipse(mouth_mask, (mc_x, mc_y + 10), (max(20, mouth_w), max(20, mouth_h)), 0, 0, 360, 1.0, -1)
    mouth_mask = cv2.GaussianBlur(mouth_mask, (41, 41), 0)[:, :, np.newaxis]
    
    # Blend base idle with morphed mouth expression
    final_face = img_idle.astype(np.float32) * (1.0 - mouth_mask) + blended * mouth_mask
    final_face = np.clip(final_face, 0, 255).astype(np.uint8)
    
    # 3. 3D Eyelid Blink Shadow & Closure using calibrated pupil positions
    blink = weights["blink"]
    if blink > 0.02:
        eye_centers = [
            (int(LANDMARKS["pupil_l"][0]), int(LANDMARKS["pupil_l"][1])),
            (int(LANDMARKS["pupil_r"][0]), int(LANDMARKS["pupil_r"][1]))
        ]
        for (ecx, ecy) in eye_centers:
            y1 = max(0, ecy - 30)
            y2 = min(h, ecy + 30)
            x1 = max(0, ecx - 45)
            x2 = min(w, ecx + 45)
            patch = final_face[y1:y2, x1:x2].copy()
            ph, pw = patch.shape[:2]
            
            close_px = int(ph * blink * 0.85)
            if close_px > 0:
                skin_col = patch[0:4, :].mean(axis=0, keepdims=True).astype(np.uint8)
                skin_fill = np.repeat(skin_col, close_px, axis=0)
                alpha = np.linspace(0.7, 1.0, close_px)[:, np.newaxis, np.newaxis]
                patch[:close_px] = (patch[:close_px] * (1.0 - alpha) + skin_fill * alpha).astype(np.uint8)
                patch[min(ph-1, close_px), :, :] = (patch[min(ph-1, close_px), :, :] * 0.45).astype(np.uint8)
                final_face[y1:y2, x1:x2] = patch
                
    return final_face


class VolumetricMana3D:
    """3D orbiting emerald mana spores with Z-depth layering and volumetric light bloom."""
    def __init__(self, count: int = 90):
        self.count = count
        self.x = np.random.uniform(-400, WIDTH + 400, count).astype(np.float32)
        self.y = np.random.uniform(0, HEIGHT + 200, count).astype(np.float32)
        self.z = np.random.uniform(0.3, 2.5, count).astype(np.float32)  # Z-depth (0.3=far/behind, 2.5=near/foreground)
        self.vx = np.random.uniform(-1.2, 1.2, count).astype(np.float32)
        self.vy = np.random.uniform(-3.0, -0.8, count).astype(np.float32)
        self.phase = np.random.uniform(0, math.pi * 2, count).astype(np.float32)

    def update(self, energy: float):
        speed = 1.0 + energy * 2.8
        self.x += self.vx * speed * self.z
        self.y += self.vy * speed * self.z
        self.phase += 0.08
        
        # Reset wrapped particles
        self.y = np.where(self.y < -30, HEIGHT + 30, self.y)
        self.x = np.where(self.x < -400, WIDTH + 400, self.x)
        self.x = np.where(self.x > WIDTH + 400, -400, self.x)

    def draw(self, frame: np.ndarray, energy: float, foreground: bool = True):
        overlay = np.zeros_like(frame)
        for i in range(self.count):
            is_fg = (self.z[i] >= 1.0)
            if is_fg != foreground:
                continue
                
            rad = max(1, int(self.z[i] * 3.5 * (1.0 + energy * 0.4)))
            alpha = min(1.0, max(0.2, (0.4 + 0.4 * math.sin(self.phase[i]) + energy * 0.3) * min(1.0, self.z[i])))
            cx, cy = int(self.x[i]), int(self.y[i])
            if 0 <= cx < WIDTH and 0 <= cy < HEIGHT:
                # Emerald mana glow (BGR: Cyan-Emerald)
                c = (int(240 * alpha), int(255 * alpha), int(60 * alpha))
                cv2.circle(overlay, (cx, cy), rad, c, -1)
                
        bloom = cv2.GaussianBlur(overlay, (21, 21), 0)
        cv2.add(frame, overlay, dst=frame)
        cv2.add(frame, bloom, dst=frame)


def generate_3d_empath_singing_video(audio_path: str, output_mp4: str, output_webp: str):
    logger.info("=========================================================")
    logger.info("SYNTHESIZING 3D COMFYUI PHOTOREALISTIC EMPATH SINGING VIDEO")
    logger.info("Audio: REEBZ - Can You Feel My Heart (10.0s Master Cut)")
    logger.info("Output: %s (1080p @ 60 FPS)", output_mp4)
    logger.info("=========================================================")
    
    # 1. Load Audio & Compute RMS Envelope
    temp_wav = os.path.join(AUDIO_DIR, "empath_3d_audio.wav")
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
    
    # 2. Load 3D Keyframes from ComfyUI
    logger.info("Loading 3D CGI keyframe lattice from ComfyUI...")
    img_idle = cv2.imread(KEYFRAME_IDLE)
    img_belting = cv2.imread(KEYFRAME_BELTING)
    img_pucker = cv2.imread(KEYFRAME_PUCKER)
    img_smile = cv2.imread(KEYFRAME_SMILE)
    
    for name, img, path in [("Idle", img_idle, KEYFRAME_IDLE),
                           ("Belting", img_belting, KEYFRAME_BELTING),
                           ("Pucker", img_pucker, KEYFRAME_PUCKER),
                           ("Smile", img_smile, KEYFRAME_SMILE)]:
        if img is None:
            raise FileNotFoundError(f"Missing 3D keyframe: {name} at {path}")
        logger.info("Loaded 3D Keyframe %s: %dx%d", name, img.shape[1], img.shape[0])
        
    mana_3d = VolumetricMana3D(count=90)
    
    # 3. Setup Master FFmpeg 60 FPS Encoder Pipe
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
        "-crf", "16",
        "-pix_fmt", "yuv420p",
        "-c:a", "aac",
        "-b:a", "320k",
        "-shortest",
        output_mp4
    ]
    
    logger.info("Spawning 60 FPS FFmpeg master render stream...")
    proc = subprocess.Popen(ffmpeg_cmd, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    
    start_time = time.time()
    preview_frames = []
    
    for f in range(TOTAL_FRAMES):
        t = f / FPS
        energy = float(envelope[f])
        
        # 1. Compute 3D Viseme & Pose Choreography
        weights = compute_3d_viseme_weights(t)
        
        # 2. Volumetric 3D Face & Jaw Synthesis
        face_3d = blend_3d_face_mesh(img_idle, img_belting, img_pucker, img_smile, weights)
        
        # 3. 3D Camera Dynamics & Parallax (Dolly push + 3D Head Pitch & Yaw)
        cam_zoom = 1.12 + 0.18 * (t / DURATION_SEC) + energy * 0.03
        pitch = weights["head_pitch"]
        yaw = weights["head_yaw"]
        
        # Scale 3D face to canvas size with dynamic camera dolly
        cur_w = int(face_3d.shape[1] * cam_zoom)
        cur_h = int(face_3d.shape[0] * cam_zoom)
        resized_3d = cv2.resize(face_3d, (cur_w, cur_h), interpolation=cv2.INTER_LINEAR)
        
        # 3D Perspective Rotation Matrix (pitch & yaw simulated via affine shear and rotation)
        rot_angle = yaw * 0.8
        M = cv2.getRotationMatrix2D((cur_w / 2.0, cur_h / 2.0), rot_angle, 1.0)
        # Vertical pitch translation
        M[1, 2] -= pitch * 3.5
        
        warped_3d = cv2.warpAffine(resized_3d, M, (cur_w, cur_h), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT)
        
        # Center crop onto 1920x1080 canvas
        canvas_frame = np.zeros((HEIGHT, WIDTH, 3), dtype=np.uint8)
        crop_x1 = max(0, int(cur_w / 2.0 - WIDTH / 2.0))
        crop_y1 = max(0, int(cur_h / 2.0 - HEIGHT / 2.0))
        crop_x2 = min(cur_w, crop_x1 + WIDTH)
        crop_y2 = min(cur_h, crop_y1 + HEIGHT)
        
        view_3d = warped_3d[crop_y1:crop_y2, crop_x1:crop_x2]
        ch, cw = view_3d.shape[:2]
        canvas_frame[:ch, :cw] = view_3d
        
        # 4. Volumetric 3D Foreground Mana Wisps & Bloom
        mana_3d.update(energy)
        mana_3d.draw(canvas_frame, energy, foreground=True)
        
        # 5. Beat-Reactive Volumetric Lighting Pulse
        if energy > 0.50:
            pulse = int((energy - 0.50) * 85)
            glow = np.zeros_like(canvas_frame)
            glow[:, :, 0] = pulse // 2  # Blue
            glow[:, :, 1] = pulse       # Emerald Green
            glow[:, :, 2] = pulse // 4  # Red
            cv2.add(canvas_frame, glow, dst=canvas_frame)
            
        # 6. Synchronized Subtitles
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
            
            # Subtle glassmorphism badge
            draw.rectangle([lx - 25, ly - 10, lx + (l_bbox[2] - l_bbox[0]) + 25, ly + 55], fill=(0, 0, 0, 150))
            draw.text((lx + 2, ly + 2), current_lyric, font=font_lyric, fill=(0, 0, 0, 250))
            draw.text((lx, ly), current_lyric, font=font_lyric, fill=(80, 255, 200, 255))
            
            canvas_frame = cv2.cvtColor(np.array(pil_img), cv2.COLOR_RGB2BGR)
            
        # 7. 2.39:1 Cinematic Letterbox
        bar_h = 70
        canvas_frame[:bar_h, :, :] = 0
        canvas_frame[-bar_h:, :, :] = 0
        
        # Write to master video stream
        proc.stdin.write(canvas_frame.tobytes())
        
        # Collect preview frame every 6th frame (10 FPS preview)
        if f % 6 == 0:
            rgb_small = cv2.cvtColor(cv2.resize(canvas_frame, (480, 270)), cv2.COLOR_BGR2RGB)
            preview_frames.append(Image.fromarray(rgb_small))
            
        if f % 60 == 0 or f == TOTAL_FRAMES - 1:
            elapsed = time.time() - start_time
            fps_render = (f + 1) / max(0.001, elapsed)
            logger.info("3D Frame %d/%d (%.1f%%) | %.1f FPS | Belting: %.2f | Smile: %.2f | Lyric: %s",
                        f + 1, TOTAL_FRAMES, (f + 1) / TOTAL_FRAMES * 100, fps_render,
                        weights["belting"], weights["smile"], current_lyric or "Instrumental")
            
    proc.stdin.close()
    proc.wait()
    logger.info("3D Master Video render complete: %s in %.2fs", output_mp4, time.time() - start_time)
    
    # Save animated WebP preview
    if preview_frames:
        logger.info("Exporting animated 3D WebP preview (%d frames)...", len(preview_frames))
        preview_frames[0].save(
            output_webp,
            save_all=True,
            append_images=preview_frames[1:],
            duration=100,
            loop=0,
            quality=85
        )
        logger.info("3D WebP preview saved: %s", output_webp)


def main():
    audio_file = os.path.join(AUDIO_DIR, "reebz_can_you_feel_my_heart.mp3")
    output_mp4 = os.path.join(OUTPUT_VIDEO_DIR, "hon_empath_3d_singing_10s.mp4")
    output_webp = os.path.join(OUTPUT_VIDEO_DIR, "hon_empath_3d_singing_10s_preview.webp")
    
    generate_3d_empath_singing_video(
        audio_path=audio_file,
        output_mp4=output_mp4,
        output_webp=output_webp
    )


if __name__ == "__main__":
    main()
