import os
import sys
import json
import re
import math
import subprocess
import tempfile
import argparse
import base64
import urllib.request
from typing import List, Dict, Tuple, Optional, Any
import requests

# Dam bao xuat Unicode/UTF-8 tren moi moi truong Windows/Linux
if sys.stdout.encoding != 'utf-8':
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

try:
    import cv2
    import numpy as np
    from PIL import Image, ImageDraw, ImageFont
    from rapidocr_onnxruntime import RapidOCR
    import imageio_ffmpeg
except ImportError as e:
    pass

CHINESE_PATTERN = re.compile(r'[\u4e00-\u9fa5]')

def get_ffmpeg_path() -> str:
    """Lay duong dan binary ffmpeg tu imageio_ffmpeg"""
    try:
        return imageio_ffmpeg.get_ffmpeg_exe()
    except Exception:
        return "ffmpeg"

def contains_chinese(text: str) -> bool:
    """Kiem tra xem chuoi co chua bat ky ky tu chu Han (Trung Quoc) nao khong"""
    if not text:
        return False
    return bool(CHINESE_PATTERN.search(text))

def get_video_info(video_path: str):
    cap = cv2.VideoCapture(video_path)
    if not cap.isOpened():
        raise ValueError(f"Khong the mo video: {video_path}")
    total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    fps = cap.get(cv2.CAP_PROP_FPS) or 25.0
    width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    cap.release()
    return total_frames, fps, width, height

def check_chinese_in_video(video_path: str, sample_count: int = 12) -> Dict:
    """
    Quet nhanh 10-12 keyframes tren toan bo video de phat hien chu Trung Quoc.
    Thoi gian phan hoi chi tu 1 - 2 giay.
    """
    if not os.path.exists(video_path):
        return {"status": "error", "message": f"File không tồn tại: {video_path}"}

    total_frames, fps, width, height = get_video_info(video_path)
    if total_frames <= 0:
        return {"status": "error", "message": "Video không hợp lệ hoặc 0 frames"}

    engine = RapidOCR()
    cap = cv2.VideoCapture(video_path)

    step = max(1, total_frames // (sample_count + 1))
    sample_indices = [step * i for i in range(1, sample_count + 1) if step * i < total_frames]
    if not sample_indices:
        sample_indices = [0]

    detected_texts = []
    has_chinese = False

    for idx in sample_indices:
        cap.set(cv2.CAP_PROP_POS_FRAMES, idx)
        ret, frame = cap.read()
        if not ret or frame is None:
            continue

        result, _ = engine(frame)
        if result:
            for item in result:
                text = item[1]
                score = float(item[2])
                if score >= 0.5 and contains_chinese(text):
                    has_chinese = True
                    detected_texts.append(text)
                    break
        if has_chinese:
            break

    cap.release()
    return {
        "status": "success",
        "has_chinese": has_chinese,
        "sample_count": len(sample_indices),
        "detected_samples": detected_texts[:5]
    }

def translate_texts_with_gemini(texts: List[str], api_key: str) -> Dict[str, str]:
    """
    Dich danh sach text tieng Trung sang tieng Anh bang Gemini API,
    toi uu ngu canh phu de video mang xa hoi va tu long Douyin / Rednote.
    """
    if not texts:
        return {}

    unique_texts = list(set([t.strip() for t in texts if contains_chinese(t)]))
    if not unique_texts:
        return {}

    prompt = (
        "You are an expert subtitle translator for Chinese social media videos (Douyin and Xiaohongshu).\n"
        "Translate the following Chinese phrases/sentences into natural, concise, and modern English suited for video overlays.\n"
        "Keep the translation short so it easily fits inside a video subtitle box.\n"
        "Return ONLY a valid JSON object where keys are the original Chinese strings and values are the English translations.\n"
        "Do not include markdown code fences or any extra commentary.\n\n"
        f"Input texts to translate:\n{json.dumps(unique_texts, ensure_ascii=False)}"
    )

    models_to_try = ["gemini-3.5-flash", "gemini-3-flash-preview", "gemini-flash-latest"]
    payload = {
        "contents": [
            {
                "parts": [
                    {"text": prompt}
                ]
            }
        ],
        "generationConfig": {
            "temperature": 0.2,
            "responseMimeType": "application/json"
        }
    }

    for model_name in models_to_try:
        try:
            url = f"https://generativelanguage.googleapis.com/v1beta/models/{model_name}:generateContent?key={api_key}"
            resp = requests.post(url, json=payload, timeout=20)
            if resp.status_code == 200:
                data = resp.json()
                raw_text = data["candidates"][0]["content"]["parts"][0]["text"].strip()
                raw_text = re.sub(r'^```json\s*', '', raw_text)
                raw_text = re.sub(r'```$', '', raw_text).strip()
                return json.loads(raw_text)
            else:
                print(f"Model {model_name} returned {resp.status_code}, trying next...", file=sys.stderr)
        except Exception as ex:
            print(f"Model {model_name} error: {ex}", file=sys.stderr)

    # Fallback tu dong dich chat luong neu Gemini API tam thoi qua tai 503
    fallback_map = {}
    for t in unique_texts:
        try:
            gtx_url = f"https://translate.googleapis.com/translate_a/single?client=gtx&sl=zh-CN&tl=en&dt=t&q={requests.utils.quote(t)}"
            r = requests.get(gtx_url, timeout=5)
            if r.status_code == 200:
                eng = "".join([part[0] for part in r.json()[0] if part and part[0]]).strip()
                fallback_map[t] = eng if eng else t
            else:
                fallback_map[t] = t
        except Exception:
            fallback_map[t] = t
    return fallback_map

def extract_audio(ffmpeg_exe: str, video_path: str, audio_out_path: str) -> bool:
    """Tach audio tu video goc"""
    cmd = [
        ffmpeg_exe, "-y",
        "-i", video_path,
        "-vn",
        "-acodec", "copy",
        audio_out_path
    ]
    res = subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return res.returncode == 0 and os.path.exists(audio_out_path) and os.path.getsize(audio_out_path) > 0

def merge_video_audio(ffmpeg_exe: str, video_no_audio: str, audio_path: Optional[str], output_path: str):
    """Ghep video voi audio goc bang FFmpeg"""
    if audio_path and os.path.exists(audio_path) and os.path.getsize(audio_path) > 0:
        cmd = [
            ffmpeg_exe, "-y",
            "-i", video_no_audio,
            "-i", audio_path,
            "-c:v", "libx264",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-shortest",
            output_path
        ]
    else:
        cmd = [
            ffmpeg_exe, "-y",
            "-i", video_no_audio,
            "-c:v", "libx264",
            "-pix_fmt", "yuv420p",
            output_path
        ]
    subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)

class TrackedBox:
    """
    Lop quan ly va on dinh toa do hop chu (Spatial Stabilization & Temporal Tracking).
    Khoa cung vi tri de tranh rung lac va giai quyet triet de van de bi dinh chu.
    """
    def __init__(self, box: Tuple[int, int, int, int], raw_text: str, eng_text: str = "", created_frame: int = 0):
        self.box = list(box)  # [x1, y1, x2, y2]
        self.raw_text = raw_text
        self.eng_text = eng_text
        self.created_frame = created_frame
        self.missing_frames = 0
        self.cached_layout = None

    @property
    def center(self) -> Tuple[float, float]:
        return ((self.box[0] + self.box[2]) / 2.0, (self.box[1] + self.box[3]) / 2.0)

    def matches(self, other_box: Tuple[int, int, int, int], other_text: str) -> bool:
        # Khoang cach tam
        c1x, c1y = self.center
        c2x = (other_box[0] + other_box[2]) / 2.0
        c2y = (other_box[1] + other_box[3]) / 2.0
        dist = math.hypot(c1x - c2x, c1y - c2y)

        # Do trung lap van ban
        t1, t2 = self.raw_text.strip(), other_text.strip()
        same_text = (t1 == t2) or (t1 in t2) or (t2 in t1)

        # Neu text giong nhau va tam gan nhau (< 60px) -> Cung 1 cau dang chay
        if same_text and dist < 60.0:
            return True
        # Neu khoang cach rat gan (< 20px) va text co do dai tuong duong
        if dist < 20.0 and same_text:
            return True
        return False

    def refresh(self):
        self.missing_frames = 0

def check_vertical_conflict(box1, box2) -> bool:
    """
    Kiem tra xem 2 hop co nam cung dong phu de / giao nhau khong gian hay khong.
    Dung de thay the ngay lap tuc chu cu khi chu moi xuat hien tren cung mot vi tri.
    """
    c1_y = (box1[1] + box1[3]) / 2.0
    c2_y = (box2[1] + box2[3]) / 2.0
    h1 = box1[3] - box1[1]
    h2 = box2[3] - box2[1]
    min_h = max(1, min(h1, h2))

    # Kiem tra do lech theo truc Y
    y_dist = abs(c1_y - c2_y)

    y1_max = max(box1[1], box2[1])
    y2_min = min(box1[3], box2[3])
    overlap_y = max(0, y2_min - y1_max)

    # 1. Neu 2 hop nam cung mot dong (tam Y cach nhau < 30px hoac chieu doc giao nhau > 35%)
    if y_dist < 32.0 or (overlap_y / min_h) > 0.35:
        return True

    # 2. Kiem tra giao nhau 2D truc tiep
    x1_max = max(box1[0], box2[0])
    x2_min = min(box1[2], box2[2])
    overlap_x = max(0, x2_min - x1_max)
    if overlap_x > 5 and overlap_y > 5:
        return True

    return False

def expand_to_subtitle_bubble(img, x1: int, y1: int, x2: int, y2: int) -> Tuple[int, int, int, int]:
    """
    Tu dong mo rong bounding box de bao tron icon / sticker / emoji va khung vien phu de.
    Dam bao can doi, khong bi lech hop sang mot ben khi khong co icon,
    va mo rong du theo chieu doc de che het toan bo net chu goc.
    """
    h_img, w_img, _ = img.shape
    H = y2 - y1
    if H <= 0 or x2 <= x1:
        return x1, y1, x2, y2

    # Lay mau nen ben trong hop chu
    inner_sample = img[y1:y2, x1:x2]
    gray_inner = cv2.cvtColor(inner_sample, cv2.COLOR_BGR2GRAY)
    bright_85 = np.percentile(gray_inner, 85)

    new_x1, new_x2 = x1, x2
    new_y1, new_y2 = y1, y2

    # Neu phu de nam tren container sang mau (nhu hop trang cua Douyin/TikTok)
    if bright_85 > 210:
        # Tim diem ket thuc ben phai (bao gom emoji/sticker sat canh chu)
        max_r = min(w_img, x2 + int(H * 1.8))
        last_white_r = x2
        for x in range(x2, max_r):
            col = img[y1:y2, x]
            is_bubble = (col[:, 0] > 230) & (col[:, 1] > 230) & (col[:, 2] > 230)
            is_emoji = (col[:, 2] > 200) & (col[:, 1] > 150)
            if np.mean(is_bubble | is_emoji) >= 0.35:
                last_white_r = x
            else:
                if x - last_white_r > 4:
                    break
        new_x2 = last_white_r

        # Tim diem bat dau ben trai (bao gom icon trang tri ben trai nhu ngoac kep, bieu tuong)
        min_l = max(0, x1 - int(H * 1.2))
        last_white_l = x1
        for x in range(x1, min_l, -1):
            col = img[y1:y2, x]
            is_bubble = (col[:, 0] > 230) & (col[:, 1] > 230) & (col[:, 2] > 230)
            is_icon = (col[:, 0] < 50) & (col[:, 1] < 50) & (col[:, 2] < 50)
            if np.mean(is_bubble | is_icon) >= 0.35:
                last_white_l = x
            else:
                if last_white_l - x > 4:
                    break
        new_x1 = last_white_l

        # Tim diem bat dau tren dinh (mo rong theo chieu doc de che het net chu)
        max_up = int(H * 0.45)
        for y in range(y1, max(0, y1 - max_up), -1):
            row = img[y, x1:x2]
            is_bubble = (row[:, 0] > 230) & (row[:, 1] > 230) & (row[:, 2] > 230)
            if np.mean(is_bubble) >= 0.50:
                new_y1 = y
            else:
                if new_y1 - y > 2:
                    break

        # Tim diem ket thuc duoi day
        max_down = int(H * 0.45)
        for y in range(y2, min(h_img, y2 + max_down)):
            row = img[y, x1:x2]
            is_bubble = (row[:, 0] > 230) & (row[:, 1] > 230) & (row[:, 2] > 230)
            if np.mean(is_bubble) >= 0.50:
                new_y2 = y
            else:
                if y - new_y2 > 2:
                    break
    else:
        # Neu khong co container trang, them margin doc an toan
        pad_y = max(4, int(H * 0.15))
        new_y1 = max(0, y1 - pad_y)
        new_y2 = min(h_img, y2 + pad_y)

    return new_x1, new_y1, new_x2, new_y2

def get_system_font(font_size: int):
    """
    Uu tien font ho tro toan dien Unicode (tieng Anh, tieng Trung, bieu tuong) tren he dieu hanh.
    Ngan chan tuyet doi hien tuong bi o vuong (tofu □□□□).
    """
    font_candidates = ["msyh.ttc", "msyhl.ttc", "simhei.ttf", "segoeui.ttf", "arial.ttf"]
    for f in font_candidates:
        try:
            return ImageFont.truetype(f, font_size)
        except Exception:
            continue
    return ImageFont.load_default()

def compute_text_layout(draw: ImageDraw.ImageDraw, text: str, box_w: int, box_h: int):
    """
    Tinh toan font size va word-wrap 1 lan duy nhat.
    """
    font_size = min(36, max(14, int(box_h * 0.75)))
    font = None
    best_lines = [text]

    while font_size >= 10:
        font = get_system_font(font_size)

        words = text.split()
        lines = []
        curr_line = ""

        for word in words:
            test_line = f"{curr_line} {word}".strip()
            bbox = draw.textbbox((0, 0), test_line, font=font)
            line_w = bbox[2] - bbox[0]
            if line_w <= box_w - 8:
                curr_line = test_line
            else:
                if curr_line:
                    lines.append(curr_line)
                curr_line = word
        if curr_line:
            lines.append(curr_line)

        total_h = sum([draw.textbbox((0, 0), l, font=font)[3] - draw.textbbox((0, 0), l, font=font)[1] + 2 for l in lines])
        if total_h <= box_h - 4 or font_size <= 10:
            best_lines = lines
            break
        font_size -= 2

    return font, best_lines, total_h

def process_video(video_path: str, output_path: str, mode: str, api_key: str = "", frame_sample_rate: int = 1) -> Dict:
    """
    Xu ly video voi 2 che do:
    - 'blur': Lam mo cac vung chu tieng Trung
    - 'translate': Che phu hop mau trang va chen chu tieng Anh (chong dinh chu, dung yen tuyet doi)
    """
    if not os.path.exists(video_path):
        return {"status": "error", "message": f"Video khong ton tai: {video_path}"}

    ffmpeg_exe = get_ffmpeg_path()
    total_frames, fps, width, height = get_video_info(video_path)

    temp_dir = tempfile.mkdtemp(prefix="trans_proc_")
    audio_path = os.path.join(temp_dir, "temp_audio.m4a")
    has_audio = extract_audio(ffmpeg_exe, video_path, audio_path)

    temp_video_out = os.path.join(temp_dir, "temp_no_audio.mp4")
    fourcc = cv2.VideoWriter_fourcc(*'mp4v')
    out_writer = cv2.VideoWriter(temp_video_out, fourcc, fps, (width, height))

    engine = RapidOCR()
    cap = cv2.VideoCapture(video_path)

    # 1. Neu che do translate: quet lay toan bo text can dich truoc de goi Gemini 1 lan duy nhat
    translations = {}
    if mode == "translate":
        all_chinese_texts = []
        sample_step = max(1, int(fps * 0.4))
        f_idx = 0
        while cap.isOpened():
            ret, frame = cap.read()
            if not ret or frame is None:
                break
            if f_idx % sample_step == 0:
                res, _ = engine(frame)
                if res:
                    for it in res:
                        t = it[1]
                        if contains_chinese(t):
                            all_chinese_texts.append(t)
            f_idx += 1

        translations = translate_texts_with_gemini(all_chinese_texts, api_key)
        cap.set(cv2.CAP_PROP_POS_FRAMES, 0)

    # 2. Danh sach cac track dang hoat dong (Active Tracks)
    active_tracks: List[TrackedBox] = []
    frame_idx = 0

    dummy_img = Image.new("RGB", (100, 100))
    dummy_draw = ImageDraw.Draw(dummy_img)

    while cap.isOpened():
        ret, frame = cap.read()
        if not ret or frame is None:
            break

        # Quet OCR moi 2 frames de phan ung sieu nhanh voi viec chuyen cau
        if frame_idx % 2 == 0:
            res, _ = engine(frame)
            detected_items = []
            if res:
                for item in res:
                    pts = item[0]
                    text = item[1]
                    score = float(item[2])
                    if score >= 0.4 and contains_chinese(text):
                        xs = [int(p[0]) for p in pts]
                        ys = [int(p[1]) for p in pts]
                        xmin, xmax = max(0, min(xs)), min(width, max(xs))
                        ymin, ymax = max(0, min(ys)), min(height, max(ys))
                        xmin = max(0, xmin - 4)
                        ymin = max(0, ymin - 3)
                        xmax = min(width, xmax + 4)
                        ymax = min(height, ymax + 3)
                        # Tu dong bao trum ca icon / sticker neu co (coi icon nhu mot phan cua dong phu de)
                        xmin, ymin, xmax, ymax = expand_to_subtitle_bubble(frame, xmin, ymin, xmax, ymax)
                        detected_items.append(((xmin, ymin, xmax, ymax), text))

            matched_track_ids = set()
            new_tracks = []

            for (box, text) in detected_items:
                found = False
                for idx, track in enumerate(active_tracks):
                    if track.matches(box, text):
                        track.refresh()
                        matched_track_ids.add(id(track))
                        found = True
                        break

                if not found:
                    eng = translations.get(text, "")
                    if not eng:
                        for k, v in translations.items():
                            if k in text or text in k:
                                eng = v
                                break
                    new_track = TrackedBox(box, text, eng, created_frame=frame_idx)
                    new_tracks.append(new_track)

            # NGUYEN TAC QUAN TRONG: CHU CU BIEN MAT THI CHU MOI MOI HIEN THI
            # 1. Khi co track moi xuat hien cung vi tri Y/giao nhau voi bat ky track cu nao,
            #    LOAI BO NGAY LAP TUC TOAN BO TRACK CU DO!
            if new_tracks:
                for n_track in new_tracks:
                    active_tracks = [t for t in active_tracks if not check_vertical_conflict(t.box, n_track.box)]
                active_tracks.extend(new_tracks)
                for n_track in new_tracks:
                    matched_track_ids.add(id(n_track))

            # 2. Neu o frame nay co phat hien bat ky text nao, nhung track cu khong con tim thay
            #    lap tuc danh dau tang missing_frames
            for track in active_tracks:
                if id(track) not in matched_track_ids:
                    track.missing_frames += 1

            # 3. Neu co track moi xuat hien trong frame nay, xoa ngay bat ky track nao khong duoc match
            if new_tracks:
                active_tracks = [t for t in active_tracks if id(t) in matched_track_ids]
            else:
                # Neu khong co track moi, chi giu toi da 1 frame chuyen giao
                active_tracks = [t for t in active_tracks if t.missing_frames <= 1]

            # 4. Bo loc chong de hop (Non-Overlapping Filter)
            # Dam bao khong bao gio co 2 hop nam cung mot dong Y hoac de len nhau
            filtered_tracks = []
            for t in sorted(active_tracks, key=lambda x: x.created_frame, reverse=True):
                conflict = False
                for kept in filtered_tracks:
                    if check_vertical_conflict(t.box, kept.box):
                        conflict = True
                        break
                if not conflict:
                    filtered_tracks.append(t)
            active_tracks = filtered_tracks

        # 3. Render frame dua tren active_tracks
        if active_tracks:
            if mode == "blur":
                for track in active_tracks:
                    x1, y1, x2, y2 = track.box
                    if x2 > x1 and y2 > y1:
                        # Giu nguyen vi tri can doi cua hop (da bao gom ca icon), khong lech sang mot ben
                        bx1 = max(0, x1 - 2)
                        bx2 = min(width, x2 + 2)
                        by1 = max(0, y1 - 2)
                        by2 = min(height, y2 + 2)

                        roi = frame[by1:by2, bx1:bx2].copy()
                        rh, rw = roi.shape[:2]
                        if rh > 5 and rw > 5:
                            # Tang ban kinh (radius) lam mo de che phu triet de va min mang
                            kw = max(25, (rw // 3) | 1)
                            kh = max(25, rh | 1)
                            blurred = cv2.GaussianBlur(roi, (kw, kh), 40)
                            blurred = cv2.GaussianBlur(blurred, (kw, kh), 40)

                            # Lam mo vien (feathering) o 4 mep de chuyen tiep tu nhien, khong bi cung
                            feather_x = min(10, max(3, rw // 10))
                            feather_y = min(6, max(3, rh // 6))

                            mask = np.zeros((rh, rw), dtype=np.float32)
                            mask[feather_y:rh - feather_y, feather_x:rw - feather_x] = 1.0

                            k_mask_x = (feather_x * 2) | 1
                            k_mask_y = (feather_y * 2) | 1
                            mask = cv2.GaussianBlur(mask, (k_mask_x, k_mask_y), 0)
                            mask = np.clip(mask, 0.0, 1.0)
                            mask_3d = mask[:, :, np.newaxis]

                            blended = (blurred.astype(np.float32) * mask_3d + roi.astype(np.float32) * (1.0 - mask_3d)).astype(np.uint8)
                            frame[by1:by2, bx1:bx2] = blended

            elif mode == "translate":
                rgb_frame = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
                pil_img = Image.fromarray(rgb_frame)
                draw = ImageDraw.Draw(pil_img)

                for track in active_tracks:
                    x1, y1, x2, y2 = track.box
                    box_w = x2 - x1
                    box_h = y2 - y1

                    if box_w > 10 and box_h > 10:
                        # 1. Che phu hop trang co dinh
                        draw.rectangle([x1, y1, x2, y2], fill=(255, 255, 255))

                        # 2. Chen chu tieng Anh can giua
                        if track.eng_text:
                            if track.cached_layout is None:
                                track.cached_layout = compute_text_layout(dummy_draw, track.eng_text, box_w, box_h)

                            font, lines, total_h = track.cached_layout
                            curr_y = y1 + (box_h - total_h) / 2
                            for line in lines:
                                bbox = draw.textbbox((0, 0), line, font=font)
                                lw = bbox[2] - bbox[0]
                                lh = bbox[3] - bbox[1]
                                lx = x1 + (box_w - lw) / 2
                                draw.text((lx, curr_y), line, fill=(0, 0, 0), font=font)
                                curr_y += lh + 2

                frame = cv2.cvtColor(np.array(pil_img), cv2.COLOR_RGB2BGR)

        out_writer.write(frame)
        frame_idx += 1

    cap.release()
    out_writer.release()

    # Ghep video voi Audio goc
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    merge_video_audio(ffmpeg_exe, temp_video_out, audio_path if has_audio else None, output_path)

    # Don dep temp dir
    try:
        if os.path.exists(temp_video_out): os.remove(temp_video_out)
        if os.path.exists(audio_path): os.remove(audio_path)
        os.rmdir(temp_dir)
    except Exception:
        pass

    return {
        "status": "success",
        "mode": mode,
        "output_path": output_path,
        "total_frames": frame_idx
    }

def strip_emojis(text: str) -> str:
    """
    Loai bo triet de tat ca cac bieu tuong emoji, icon, unicode symbols
    de dam bao tieu de Hook chi chua van ban thuan tuy phong cach Reels / TikTok.
    """
    if not text:
        return ""
    # Unicode blocks for emojis and pictographs
    emoji_pattern = re.compile(
        "["
        "\U0001F600-\U0001F64F"  # emoticons
        "\U0001F300-\U0001F5FF"  # symbols & pictographs
        "\U0001F680-\U0001F6FF"  # transport & map symbols
        "\U0001F700-\U0001F77F"  # alchemical symbols
        "\U0001F780-\U0001F7FF"  # Geometric Shapes Extended
        "\U0001F800-\U0001F8FF"  # Supplemental Arrows-C
        "\U0001F900-\U0001F9FF"  # Supplemental Symbols and Pictographs
        "\U0001FA00-\U0001FA6F"  # Chess Symbols
        "\U0001FA70-\U0001FAFF"  # Symbols and Pictographs Extended-A
        "\U00002600-\U000027BF"  # Miscellaneous Symbols, Dingbats
        "\U00002B50-\U00002B55"  # Stars, etc.
        "\U0000FE00-\U0000FE0F"  # Variation Selectors
        "\U0001F1E6-\U0001F1FF"  # Flags
        "]+",
        flags=re.UNICODE
    )
    cleaned = emoji_pattern.sub('', text)
    cleaned = re.sub(r'[\u25A0-\u25FF\u2600-\u27BF]', '', cleaned)
    return re.sub(r'\s+', ' ', cleaned).strip()

def generate_hooks_with_gemini(video_path: str, api_key: str) -> Dict[str, Any]:
    """
    Trich xuat cac frame tieu bieu tu video va su dung Gemini 3.5 Flash de phan tich
    noi dung video, tu do de xuat 4-5 tieu de Hook tieng Anh viral phu hop.
    Luu y: Hoan toan chi sinh text thuan tuy khong kem icon/emoji.
    Tuyet doi khong chen cau example cu the vao prompt.
    """
    fallback_hooks = [
        "Wait until you see how this turns out",
        "You will never believe how easy this is",
        "The ultimate hack you wish you knew earlier",
        "Stop scrolling! You need to see this right now",
        "Best decision I have ever made this year"
    ]

    if not api_key or not os.path.exists(video_path):
        return {"status": "success", "hooks": fallback_hooks}

    try:
        cap = cv2.VideoCapture(video_path)
        total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
        if total_frames <= 0:
            cap.release()
            return {"status": "success", "hooks": fallback_hooks}

        # Lay 3 khung hinh tieu bieu o cac giai doan video (15%, 50%, 85%)
        frames_b64 = []
        for ratio in [0.15, 0.50, 0.85]:
            f_idx = int(total_frames * ratio)
            cap.set(cv2.CAP_PROP_POS_FRAMES, f_idx)
            ret, frame = cap.read()
            if ret and frame is not None:
                h, w = frame.shape[:2]
                if w > 720:
                    frame = cv2.resize(frame, (720, int(h * 720 / w)))
                _, buf = cv2.imencode('.jpg', frame, [cv2.IMWRITE_JPEG_QUALITY, 80])
                frames_b64.append(base64.b64encode(buf).decode('utf-8'))
        cap.release()

        if not frames_b64:
            return {"status": "success", "hooks": fallback_hooks}

        url = f"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash:generateContent?key={api_key}"

        prompt = (
            "You are an expert social media content strategist specializing in viral TikTok and Instagram Reels. "
            "Analyze these video frames. Generate 4 to 5 short, catchy, high-converting hook titles in English "
            "that capture attention within the first 3 seconds. Each hook title must be under 10 words and strictly plain text only without any emojis, icons, or symbols. "
            "Respond ONLY with a valid JSON array of strings, without any Markdown backticks or commentary."
        )

        parts = [{"text": prompt}]
        for b64 in frames_b64:
            parts.append({
                "inlineData": {
                    "mimeType": "image/jpeg",
                    "data": b64
                }
            })

        req_data = {"contents": [{"parts": parts}]}
        req = urllib.request.Request(
            url,
            data=json.dumps(req_data).encode("utf-8"),
            headers={"Content-Type": "application/json"}
        )

        with urllib.request.urlopen(req, timeout=20) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            raw_text = data["candidates"][0]["content"]["parts"][0]["text"].strip()
            if raw_text.startswith("```"):
                raw_text = re.sub(r"^```[a-zA-Z]*\n?", "", raw_text)
                raw_text = re.sub(r"\n?```$", "", raw_text)
            hooks = json.loads(raw_text)
            if isinstance(hooks, list) and len(hooks) > 0:
                cleaned_hooks = [strip_emojis(str(h)) for h in hooks if strip_emojis(str(h))]
                if cleaned_hooks:
                    return {"status": "success", "hooks": cleaned_hooks}
    except Exception as ex:
        sys.stderr.write(f"Gemini generate hooks error: {ex}\n")

    return {"status": "success", "hooks": fallback_hooks}

def add_hook_to_video(input_path: str, output_path: str, hook_text: str) -> Dict[str, Any]:
    """
    Dong chu Hook Title phong cach TikTok / Instagram Reels len video:
    - Font chu dam (Bold), chu trang, vien den (stroke_width).
    - Can giua o vi tri 1/4 phia tren cua video.
    - Giu nguyen am thanh goc cua video.
    - Hoan toan la text thuan tuy, khong co icon/emoji, can dong chuan xac.
    """
    if not os.path.exists(input_path):
        raise FileNotFoundError(f"Input video not found: {input_path}")

    # Loai bo triet de emoji neu co
    hook_text = strip_emojis(hook_text)
    if not hook_text:
        hook_text = "Wait until you see how this turns out"

    cap = cv2.VideoCapture(input_path)
    width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

    temp_dir = tempfile.mkdtemp()
    temp_video_out = os.path.join(temp_dir, "temp_hook_video.mp4")
    audio_path = os.path.join(temp_dir, "temp_audio.aac")

    ffmpeg_exe = get_ffmpeg_path()
    has_audio = False
    if ffmpeg_exe:
        has_audio = extract_audio(ffmpeg_exe, input_path, audio_path)

    fourcc = cv2.VideoWriter_fourcc(*'mp4v')
    out_writer = cv2.VideoWriter(temp_video_out, fourcc, fps, (width, height))

    # Tinh toan layout font va xuong dong 1 lan duy nhat
    max_w = int(width * 0.82)
    font_size = max(24, int(width * 0.054))
    font = None
    for f_name in ["segoeuib.ttf", "arialbd.ttf", "trebucbd.ttf", "msyhbd.ttc", "arial.ttf"]:
        try:
            font = ImageFont.truetype(f_name, font_size)
            break
        except Exception:
            continue
    if font is None:
        font = ImageFont.load_default()

    stroke_w = max(3, int(font_size * 0.08))

    dummy_img = Image.new("RGB", (100, 100))
    dummy_draw = ImageDraw.Draw(dummy_img)

    words = hook_text.strip().split()
    lines = []
    curr = ""
    for word in words:
        test_line = f"{curr} {word}".strip()
        bbox = dummy_draw.textbbox((0, 0), test_line, font=font, stroke_width=stroke_w)
        if (bbox[2] - bbox[0]) <= max_w:
            curr = test_line
        else:
            if curr:
                lines.append(curr)
            curr = word
    if curr:
        lines.append(curr)

    line_heights = []
    line_widths = []
    for line in lines:
        bbox = dummy_draw.textbbox((0, 0), line, font=font, stroke_width=stroke_w)
        line_widths.append(bbox[2] - bbox[0])
        line_heights.append(bbox[3] - bbox[1])

    start_y = int(height * 0.22)

    # Tinh toa do ve co dinh cho cac dong
    draw_instructions = []
    curr_y = start_y
    for idx, line in enumerate(lines):
        lw = line_widths[idx]
        lx = (width - lw) // 2
        draw_instructions.append((lx, curr_y, line))
        curr_y += line_heights[idx] + max(6, int(font_size * 0.22))

    frame_idx = 0
    while cap.isOpened():
        ret, frame = cap.read()
        if not ret or frame is None:
            break

        rgb_frame = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        pil_img = Image.fromarray(rgb_frame)
        draw = ImageDraw.Draw(pil_img)

        for lx, ly, line in draw_instructions:
            draw.text(
                (lx, ly),
                line,
                font=font,
                fill=(255, 255, 255),
                stroke_width=stroke_w,
                stroke_fill=(0, 0, 0)
            )

        frame = cv2.cvtColor(np.array(pil_img), cv2.COLOR_RGB2BGR)
        out_writer.write(frame)
        frame_idx += 1

    cap.release()
    out_writer.release()

    # Ghep video voi Audio goc
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    merge_video_audio(ffmpeg_exe, temp_video_out, audio_path if has_audio else None, output_path)

    # Don dep temp dir
    try:
        if os.path.exists(temp_video_out): os.remove(temp_video_out)
        if os.path.exists(audio_path): os.remove(audio_path)
        os.rmdir(temp_dir)
    except Exception:
        pass

    return {
        "status": "success",
        "mode": "add_hook",
        "output_path": output_path,
        "total_frames": frame_idx
    }

def main():
    parser = argparse.ArgumentParser(description="Video Chinese Text Inspector and Processor")
    parser.add_argument("--action", choices=["check", "blur", "translate", "generate_hooks", "add_hook"], required=True, help="Hanh dong can thuc hien")
    parser.add_argument("--input", required=True, help="Duong dan video dau vao")
    parser.add_argument("--output", help="Duong dan video dau ra (cho action blur, translate, add_hook)")
    parser.add_argument("--api-key", default="", help="Gemini API Key cho action translate hoac generate_hooks")
    parser.add_argument("--hook-text", default="", help="Noi dung tieu de Hook cho action add_hook")

    args = parser.parse_args()

    try:
        if args.action == "check":
            res = check_chinese_in_video(args.input)
            print(json.dumps(res, ensure_ascii=False))

        elif args.action == "generate_hooks":
            res = generate_hooks_with_gemini(args.input, args.api_key)
            print(json.dumps(res, ensure_ascii=False))

        elif args.action == "add_hook":
            if not args.output:
                print(json.dumps({"status": "error", "message": "--output la bat buoc cho action add_hook"}))
                sys.exit(1)
            res = add_hook_to_video(args.input, args.output, args.hook_text or "Wait until you see how this turns out")
            print(json.dumps(res, ensure_ascii=False))

        elif args.action in ["blur", "translate"]:
            if not args.output:
                print(json.dumps({"status": "error", "message": "--output la bat buoc cho action blur/translate"}))
                sys.exit(1)
            res = process_video(args.input, args.output, args.action, args.api_key)
            print(json.dumps(res, ensure_ascii=False))

    except Exception as ex:
        print(json.dumps({"status": "error", "message": str(ex)}, ensure_ascii=False))
        sys.exit(1)

if __name__ == "__main__":
    main()
