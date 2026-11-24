#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth - Audio-Reactive Music Video Generator (High Performance)
Orchestrates ComfyUI visuals, in-game assets, and audio-reactive 60 FPS motion synthesis
to generate high-fidelity cinematic music videos set to REEBZ - Can You Feel My Heart.
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

# Configure rich verbose logging
logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s] [%(levelname)s] [HoN-MusicVideo] %(message)s",
    datefmt="%H:%M:%S",
    handlers=[logging.StreamHandler(sys.stdout)]
)
logger = logging.getLogger("hon_mv")

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


def extract_audio_segment(input_audio: str, output_wav: str, duration: float = 10.0) -> Tuple[np.ndarray, int]:
    """Extract first N seconds of audio and return audio samples and sample rate."""
    logger.info("Extracting %ss audio segment from %s...", duration, os.path.basename(input_audio))
    cmd = [
        "ffmpeg", "-y",
        "-i", input_audio,
        "-ss", "0.0",
        "-t", str(duration),
        "-ar", "44100",
        "-ac", "2",
        output_wav
    ]
    subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
    data, sr = sf.read(output_wav)
    logger.info("Audio segment extracted successfully: %d samples at %d Hz (stereo)", len(data), sr)
    return data, sr


def compute_audio_envelope(audio_data: np.ndarray, sr: int, total_frames: int) -> np.ndarray:
    """Compute per-frame RMS energy and transient response (0.0 to 1.0)."""
    mono = np.mean(audio_data, axis=1) if audio_data.ndim == 2 else audio_data
    frame_samples = len(mono) / total_frames
    
    envelope = np.zeros(total_frames, dtype=np.float32)
    for f in range(total_frames):
        start = int(f * frame_samples)
        end = int((f + 1) * frame_samples)
        chunk = mono[start:end]
        if len(chunk) > 0:
            rms = np.sqrt(np.mean(chunk**2))
            envelope[f] = rms
            
    max_e = np.max(envelope)
    if max_e > 1e-6:
        envelope = envelope / max_e
        envelope = np.power(envelope, 1.4)
        
    logger.info("Audio envelope computed for %d frames (peak: %.3f)", total_frames, np.max(envelope))
    return envelope


class FastParticleSystem:
    """Vectorized particle system with glowing soft blur for high-speed 60 FPS rendering."""
    def __init__(self, count: int, ptype: str = "ember"):
        self.count = count
        self.ptype = ptype
        self.x = np.random.uniform(0, WIDTH, count).astype(np.float32)
        self.y = np.random.uniform(0, HEIGHT, count).astype(np.float32)
        self.vx = np.random.uniform(-1.5, 1.5, count).astype(np.float32)
        if ptype in ["ember", "spore"]:
            self.vy = np.random.uniform(-4.0, -1.0, count).astype(np.float32)
        else:
            self.vy = np.random.uniform(-2.0, 2.0, count).astype(np.float32)
        self.size = np.random.uniform(3.0, 8.0, count).astype(np.float32)
        self.phase = np.random.uniform(0, math.pi * 2, count).astype(np.float32)
        self.pulse_speed = np.random.uniform(0.06, 0.18, count).astype(np.float32)
        
        if ptype == "ember":
            self.color = (255, 160, 40)
        elif ptype == "spore":
            self.color = (60, 255, 150)
        else:
            self.color = (210, 110, 255)

    def update(self, energy: float):
        speed_mult = 1.0 + energy * 2.2
        self.x += self.vx * speed_mult
        self.y += self.vy * speed_mult
        self.phase += self.pulse_speed
        
        # Wrap bounds
        self.y = np.where(self.y < -10, HEIGHT + 10, self.y)
        self.y = np.where(self.y > HEIGHT + 10, -10, self.y)
        self.x = np.where(self.x < -10, WIDTH + 10, self.x)
        self.x = np.where(self.x > WIDTH + 10, -10, self.x)

    def draw(self, frame: np.ndarray, energy: float):
        overlay = np.zeros_like(frame)
        r, g, b = self.color
        
        for i in range(self.count):
            cur_alpha = min(1.0, max(0.2, 0.5 + 0.4 * math.sin(self.phase[i]) + energy * 0.4))
            rad = max(1, int(self.size[i] * (1.0 + energy * 0.6)))
            cx, cy = int(self.x[i]), int(self.y[i])
            if 0 <= cx < WIDTH and 0 <= cy < HEIGHT:
                c = (int(r * cur_alpha), int(g * cur_alpha), int(b * cur_alpha))
                cv2.circle(overlay, (cx, cy), rad, c, -1)
                
        # Fast blur for soft glowing bloom
        blurred = cv2.GaussianBlur(overlay, (15, 15), 0)
        # Additive blend
        cv2.add(frame, overlay, dst=frame)
        cv2.add(frame, blurred, dst=frame)


def transform_cv2_frame(
    img_bgr: np.ndarray,
    scale: float,
    pan_x: float,
    pan_y: float,
    rotation: float = 0.0,
    target_w: int = WIDTH,
    target_h: int = HEIGHT
) -> np.ndarray:
    """Apply high-performance 2.5D camera zoom, pan, and rotation using OpenCV."""
    h, w = img_bgr.shape[:2]
    new_w = int(w * scale)
    new_h = int(h * scale)
    
    resized = cv2.resize(img_bgr, (new_w, new_h), interpolation=cv2.INTER_LINEAR)
    
    if abs(rotation) > 0.01:
        center = (new_w / 2.0, new_h / 2.0)
        M = cv2.getRotationMatrix2D(center, rotation, 1.0)
        resized = cv2.warpAffine(resized, M, (new_w, new_h), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT)
        
    center_x = new_w / 2.0 + pan_x * (new_w - target_w) * 0.5
    center_y = new_h / 2.0 + pan_y * (new_h - target_h) * 0.5
    
    left = int(center_x - target_w / 2.0)
    top = int(center_y - target_h / 2.0)
    right = left + target_w
    bottom = top + target_h
    
    left = max(0, min(new_w - target_w, left))
    top = max(0, min(new_h - target_h, top))
    
    cropped = resized[top:top+target_h, left:left+target_w]
    if cropped.shape[:2] != (target_h, target_w):
        cropped = cv2.resize(cropped, (target_w, target_h), interpolation=cv2.INTER_LINEAR)
        
    return cropped


def apply_chromatic_aberration_cv2(frame: np.ndarray, shift_px: int) -> np.ndarray:
    """Apply fast chromatic aberration."""
    if shift_px <= 0:
        return frame
    h, w = frame.shape[:2]
    b, g, r = cv2.split(frame)
    
    # Shift R left, B right
    r_shifted = np.zeros_like(r)
    b_shifted = np.zeros_like(b)
    
    r_shifted[:, 0:w-shift_px] = r[:, shift_px:w]
    r_shifted[:, w-shift_px:w] = r[:, w-1:w]
    
    b_shifted[:, shift_px:w] = b[:, 0:w-shift_px]
    b_shifted[:, 0:shift_px] = b[:, 0:1]
    
    return cv2.merge([b_shifted, g, r_shifted])


def render_music_video_fast(
    scene_images: List[str],
    audio_path: str,
    output_mp4: str,
    output_preview_webp: str
):
    logger.info("Initializing high-performance music video render pipeline...")
    logger.info("Target: %s (%dx%d @ %d FPS, Duration: %ss)", output_mp4, WIDTH, HEIGHT, FPS, DURATION_SEC)
    
    # 1. Prepare Audio
    temp_audio_wav = os.path.join(AUDIO_DIR, "mv_temp_audio.wav")
    audio_data, sr = extract_audio_segment(audio_path, temp_audio_wav, DURATION_SEC)
    envelope = compute_audio_envelope(audio_data, sr, TOTAL_FRAMES)
    
    # 2. Load Scene Visuals as BGR NumPy arrays
    loaded_bgr = []
    for idx, path in enumerate(scene_images):
        logger.info("Loading scene %d: %s", idx + 1, os.path.basename(path))
        im = cv2.imread(path)
        if im is None:
            raise FileNotFoundError(f"Failed to read image at {path}")
        loaded_bgr.append(im)
        
    # Particle Systems
    spore_sys = FastParticleSystem(count=60, ptype="spore")
    ember_sys = FastParticleSystem(count=75, ptype="ember")
    soul_sys = FastParticleSystem(count=70, ptype="soul")
    
    # 3. Setup FFmpeg Raw Video Pipe (BGR24 -> H264)
    ffmpeg_cmd = [
        "ffmpeg", "-y",
        "-f", "rawvideo",
        "-vcodec", "rawvideo",
        "-s", f"{WIDTH}x{HEIGHT}",
        "-pix_fmt", "bgr24",
        "-r", str(FPS),
        "-i", "-",
        "-i", temp_audio_wav,
        "-c:v", "libx264",
        "-preset", "fast",
        "-crf", "18",
        "-pix_fmt", "yuv420p",
        "-c:a", "aac",
        "-b:a", "320k",
        "-shortest",
        output_mp4
    ]
    
    logger.info("Launching FFmpeg master pipe...")
    proc = subprocess.Popen(ffmpeg_cmd, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    
    start_time = time.time()
    preview_frames = []
    
    for f in range(TOTAL_FRAMES):
        t_sec = f / FPS
        energy = float(envelope[f])
        
        # Timeline:
        # Scene 1: 0 - 120 (0.0s - 2.0s) -> Caldavar Grove (Ophelia & Wolves)
        # Scene 2: 120 - 240 (2.0s - 4.0s) -> Ophelia Queen Portrait
        # Scene 3: 240 - 360 (4.0s - 6.0s) -> Hellbourne Cataclysm
        # Scene 4: 360 - 480 (6.0s - 8.0s) -> Soulstealer & Kongor Abyss
        # Scene 5: 480 - 600 (8.0s - 10.0s) -> Grand Battle & Project Kongor Title
        
        if f < 120:  # Scene 1: Caldavar Sanctuary
            prog = f / 120.0
            scale = 1.05 + 0.16 * prog + energy * 0.03
            pan_x = math.sin(prog * math.pi * 0.5) * 0.2
            pan_y = -0.1 + prog * 0.15
            rot = math.sin(prog * math.pi) * 0.5
            frame = transform_cv2_frame(loaded_bgr[0], scale, pan_x, pan_y, rot)
            
            if f > 100:
                blend = (f - 100) / 20.0
                next_f = transform_cv2_frame(loaded_bgr[1], 1.05, 0.0, 0.0)
                frame = cv2.addWeighted(frame, 1.0 - blend, next_f, blend, 0)
                
            spore_sys.update(energy)
            spore_sys.draw(frame, energy)
            
        elif f < 240:  # Scene 2: Ophelia Queen Portrait
            prog = (f - 120) / 120.0
            scale = 1.08 + 0.22 * prog + energy * 0.05
            pan_x = math.cos(prog * math.pi * 0.5) * -0.15
            pan_y = 0.1 - prog * 0.12
            rot = -0.4 + prog * 0.7
            frame = transform_cv2_frame(loaded_bgr[1], scale, pan_x, pan_y, rot)
            
            if f > 220:
                blend = (f - 220) / 20.0
                next_f = transform_cv2_frame(loaded_bgr[2], 1.10, 0.0, 0.0)
                frame = cv2.addWeighted(frame, 1.0 - blend, next_f, blend, 0)
                
            spore_sys.update(energy)
            spore_sys.draw(frame, energy)
            
        elif f < 360:  # Scene 3: Hellbourne Cataclysm
            prog = (f - 240) / 120.0
            scale = 1.10 + 0.20 * (1.0 - prog) + energy * 0.06
            pan_x = -0.3 + prog * 0.6
            pan_y = 0.2 - prog * 0.3
            rot = math.sin(prog * math.pi * 2.0) * 0.8
            frame = transform_cv2_frame(loaded_bgr[2], scale, pan_x, pan_y, rot)
            
            if energy > 0.6:
                flash = int((energy - 0.6) * 110)
                frame = cv2.add(frame, np.full_like(frame, flash))
                
            if f > 340:
                blend = (f - 340) / 20.0
                next_f = transform_cv2_frame(loaded_bgr[3], 1.12, 0.0, 0.0)
                frame = cv2.addWeighted(frame, 1.0 - blend, next_f, blend, 0)
                
            ember_sys.update(energy)
            ember_sys.draw(frame, energy)
            
        elif f < 480:  # Scene 4: Soulstealer & Kongor's Abyssal Maw
            prog = (f - 360) / 120.0
            scale = 1.12 + 0.24 * prog + energy * 0.08
            pan_x = 0.2 * math.sin(prog * math.pi * 3.0)
            pan_y = -0.15 + prog * 0.2
            rot = math.sin(prog * math.pi * 2) * 1.2
            frame = transform_cv2_frame(loaded_bgr[3], scale, pan_x, pan_y, rot)
            
            # Vocal stutter lightning flash ("Can you feel... can you feel...")
            if energy > 0.55:
                flash = int((energy - 0.55) * 130)
                flash_layer = np.zeros_like(frame)
                flash_layer[:, :, 0] = flash  # Blue channel in BGR
                flash_layer[:, :, 2] = flash // 2  # Red channel
                frame = cv2.add(frame, flash_layer)
                
            if f > 460:
                blend = (f - 460) / 20.0
                next_f = transform_cv2_frame(loaded_bgr[4], 1.10, 0.0, 0.0)
                frame = cv2.addWeighted(frame, 1.0 - blend, next_f, blend, 0)
                
            soul_sys.update(energy)
            soul_sys.draw(frame, energy)
            
        else:  # Scene 5: The Grand Battle of Newerth & Project Kongor Title Climax
            prog = (f - 480) / 120.0
            scale = 1.05 + 0.26 * prog + energy * 0.08
            pan_x = math.sin(prog * math.pi) * 0.2
            pan_y = prog * 0.15
            rot = math.sin(prog * math.pi) * 0.6
            frame = transform_cv2_frame(loaded_bgr[4], scale, pan_x, pan_y, rot)
            
            ember_sys.update(energy)
            ember_sys.draw(frame, energy)
            
            # Title Overlay
            if f >= 505:
                title_prog = min(1.0, (f - 505) / 30.0)
                # Convert BGR to RGB for PIL text rendering
                rgb_frame = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
                pil_frame = Image.fromarray(rgb_frame)
                draw = ImageDraw.Draw(pil_frame, "RGBA")
                
                # Dark translucent banner
                banner_alpha = int(150 * title_prog)
                draw.rectangle([0, HEIGHT // 2 - 135, WIDTH, HEIGHT // 2 + 135], fill=(0, 0, 0, banner_alpha))
                
                title_text = "HEROES OF NEWERTH"
                subtitle_text = "PROJECT KONGOR"
                music_text = "CAN YOU FEEL MY HEART  |  REEBZ"
                
                try:
                    font_title = ImageFont.truetype("arialbd.ttf", 74)
                    font_sub = ImageFont.truetype("arialbd.ttf", 36)
                    font_music = ImageFont.truetype("arial.ttf", 22)
                except Exception:
                    font_title = ImageFont.load_default()
                    font_sub = ImageFont.load_default()
                    font_music = ImageFont.load_default()
                    
                t_bbox = draw.textbbox((0, 0), title_text, font=font_title)
                s_bbox = draw.textbbox((0, 0), subtitle_text, font=font_sub)
                m_bbox = draw.textbbox((0, 0), music_text, font=font_music)
                
                tx = (WIDTH - (t_bbox[2] - t_bbox[0])) // 2
                sx = (WIDTH - (s_bbox[2] - s_bbox[0])) // 2
                mx = (WIDTH - (m_bbox[2] - m_bbox[0])) // 2
                
                alpha_val = int(255 * title_prog)
                gold_color = (255, 215, 20, alpha_val)
                cyan_color = (0, 225, 255, alpha_val)
                white_color = (240, 240, 240, int(200 * title_prog))
                
                # Shadow
                draw.text((tx + 3, HEIGHT // 2 - 95 + 3), title_text, font=font_title, fill=(0, 0, 0, alpha_val))
                draw.text((tx, HEIGHT // 2 - 95), title_text, font=font_title, fill=gold_color)
                
                draw.text((sx + 2, HEIGHT // 2 - 5 + 2), subtitle_text, font=font_sub, fill=(0, 0, 0, alpha_val))
                draw.text((sx, HEIGHT // 2 - 5), subtitle_text, font=font_sub, fill=cyan_color)
                
                draw.text((mx, HEIGHT // 2 + 65), music_text, font=font_music, fill=white_color)
                
                # Convert back to BGR
                frame = cv2.cvtColor(np.array(pil_frame.convert("RGB")), cv2.COLOR_RGB2BGR)
                
        # Audio-Reactive Chromatic Aberration on Beat Drops
        ca_shift = int(energy * 15) if energy > 0.45 else 0
        if ca_shift > 0:
            frame = apply_chromatic_aberration_cv2(frame, ca_shift)
            
        # 2.39:1 Cinematic Letterbox
        bar_h = 75
        frame[:bar_h, :, :] = 0
        frame[-bar_h:, :, :] = 0
        
        # Write BGR bytes directly to FFmpeg stdin
        proc.stdin.write(frame.tobytes())
        
        # Collect preview frames (every 6th frame = 10 FPS preview)
        if f % 6 == 0:
            rgb_small = cv2.cvtColor(cv2.resize(frame, (480, 270)), cv2.COLOR_BGR2RGB)
            preview_frames.append(Image.fromarray(rgb_small))
            
        if f % 60 == 0 or f == TOTAL_FRAMES - 1:
            elapsed = time.time() - start_time
            fps_render = (f + 1) / max(0.001, elapsed)
            logger.info("Rendered frame %d/%d (%.1f%%) | %.1f FPS | Energy: %.2f", f + 1, TOTAL_FRAMES, (f + 1) / TOTAL_FRAMES * 100, fps_render, energy)
            
    proc.stdin.close()
    proc.wait()
    logger.info("FFmpeg master render complete: %s in %.2fs!", output_mp4, time.time() - start_time)
    
    if preview_frames:
        logger.info("Generating animated WebP preview (%d frames)...", len(preview_frames))
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
    logger.info("=== Heroes of Newerth Fast Music Video Pipeline ===")
    
    scenes = [
        os.path.join(COMFY_OUTPUT_DIR, "KongorLore_00024_.png"),  # Scene 1: Caldavar Sanctuary
        os.path.join(COMFY_OUTPUT_DIR, "KongorLore_00028_.png"),  # Scene 2: Ophelia Queen Portrait
        os.path.join(COMFY_OUTPUT_DIR, "KongorLore_00025_.png"),  # Scene 3: Hellbourne Cataclysm
        os.path.join(COMFY_OUTPUT_DIR, "KongorLore_00026_.png"),  # Scene 4: Soulstealer & Kongor Abyss
        os.path.join(COMFY_OUTPUT_DIR, "KongorLore_00027_.png"),  # Scene 5: Grand Battle & Climax
    ]
    
    audio_file = os.path.join(AUDIO_DIR, "reebz_can_you_feel_my_heart.mp3")
    output_mp4 = os.path.join(OUTPUT_VIDEO_DIR, "hon_reebz_music_video_10s.mp4")
    output_webp = os.path.join(OUTPUT_VIDEO_DIR, "hon_reebz_music_video_10s_preview.webp")
    
    render_music_video_fast(
        scene_images=scenes,
        audio_path=audio_file,
        output_mp4=output_mp4,
        output_preview_webp=output_webp
    )
    
    logger.info("=== Music Video Render Successfully Completed! ===")


if __name__ == "__main__":
    main()
