#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
Video Indexer (insights) JSON から Scene 単位の事実ドキュメントを生成する。

追加対応:
- transcript_text 集約
- keyframes に thumbnailId / imagePath を追加
- representativeImagePath を追加
- faces / namedPeople / detectedObjects を集約

Input : Video Indexer export JSON
Output: scene_facts.json  (1 scene = 1 doc)
"""

import argparse
import json
from typing import Any, Dict, List, Optional, Tuple


def parse_time_to_ms(t: str) -> int:
    """
    Video Indexer の time 文字列 (例: "0:00:19.6333333") をミリ秒に変換。
    想定: H:MM:SS(.fraction)
    """
    if "." in t:
        base, frac = t.split(".", 1)
        frac = "".join(ch for ch in frac if ch.isdigit())
        frac_sec = float(f"0.{frac}") if frac else 0.0
    else:
        base, frac_sec = t, 0.0

    parts = base.split(":")
    if len(parts) != 3:
        raise ValueError(f"Unexpected time format: {t}")

    h = int(parts[0])
    m = int(parts[1])
    s = int(parts[2])
    total_sec = h * 3600 + m * 60 + s + frac_sec
    return int(round(total_sec * 1000))


def overlaps(a: Tuple[int, int], b: Tuple[int, int]) -> bool:
    """[a0,a1] と [b0,b1] が重なるか（境界含む）"""
    a0, a1 = a
    b0, b1 = b
    return not (a1 < b0 or b1 < a0)


def normalize_space(s: str) -> str:
    return " ".join(str(s).split()).strip()


def distinct_preserve_order(items: List[str]) -> List[str]:
    seen = set()
    out = []
    for x in items:
        if x not in seen:
            seen.add(x)
            out.append(x)
    return out


def make_thumbnail_path(thumbnail_id: str, thumbnail_dir: Optional[str]) -> Optional[str]:
    if not thumbnail_id or not thumbnail_dir:
        return None
    return f"{thumbnail_dir}\\KeyFrameThumbnail_{thumbnail_id}.jpg"


def extract_instance_range(inst: Dict[str, Any]) -> Optional[Tuple[int, int]]:
    start = inst.get("start")
    end = inst.get("end")
    if start is None or end is None:
        return None
    return parse_time_to_ms(start), parse_time_to_ms(end)


def collect_text_items_for_scene(
    items: List[Dict[str, Any]],
    scene_range: Tuple[int, int],
    text_field: str,
    max_items: int,
) -> List[str]:
    out: List[str] = []

    for item in items:
        text = normalize_space(item.get(text_field, ""))
        if not text:
            continue

        for inst in item.get("instances", []) or []:
            rng = extract_instance_range(inst)
            if rng and overlaps(scene_range, rng):
                out.append(text)
                break

    return distinct_preserve_order(out)[:max_items]


def collect_named_entities_for_scene(
    items: List[Dict[str, Any]],
    scene_range: Tuple[int, int],
    name_field: str = "name",
    confidence_field: str = "confidence",
    max_items: int = 50,
) -> List[Dict[str, Any]]:
    results: List[Dict[str, Any]] = []
    seen = set()

    for item in items:
        name = normalize_space(item.get(name_field, ""))
        if not name:
            continue

        hit = False
        for inst in item.get("instances", []) or []:
            rng = extract_instance_range(inst)
            if rng and overlaps(scene_range, rng):
                hit = True
                break

        if not hit:
            continue

        if name in seen:
            continue
        seen.add(name)

        entry = {"name": name}
        if confidence_field in item and item.get(confidence_field) is not None:
            entry["confidence"] = item.get(confidence_field)

        if item.get("thumbnailId"):
            entry["thumbnailId"] = item["thumbnailId"]

        results.append(entry)

        if len(results) >= max_items:
            break

    return results


def collect_detected_objects_for_scene(
    items: List[Dict[str, Any]],
    scene_range: Tuple[int, int],
    max_items: int = 50,
) -> List[Dict[str, Any]]:
    results: List[Dict[str, Any]] = []
    seen = set()

    for item in items:
        name = normalize_space(item.get("name", ""))
        if not name:
            continue

        matched_ranges: List[Tuple[int, int]] = []
        for inst in item.get("instances", []) or []:
            rng = extract_instance_range(inst)
            if rng and overlaps(scene_range, rng):
                matched_ranges.append(rng)

        if not matched_ranges:
            continue

        if name in seen:
            continue
        seen.add(name)

        entry = {
            "name": name,
            "count": len(matched_ranges),
        }
        if item.get("thumbnailId"):
            entry["thumbnailId"] = item["thumbnailId"]

        results.append(entry)

        if len(results) >= max_items:
            break

    return results


def pick_keyframes_for_scene(
    scene_range: Tuple[int, int],
    shots: List[Dict[str, Any]],
    max_frames: int = 2,
    thumbnail_dir: Optional[str] = None,
) -> List[Dict[str, Any]]:
    """
    shots[].keyFrames[].instances の start/end が scene と重なるものから代表を選ぶ。
    出力には thumbnailId / imagePath を含める。
    代表: 先頭 + 中央(近いもの) で最大2枚。
    """
    candidates: List[Dict[str, Any]] = []

    for shot in shots:
        shot_hit = False
        for inst in shot.get("instances", []) or []:
            rng = extract_instance_range(inst)
            if rng and overlaps(scene_range, rng):
                shot_hit = True
                break
        if not shot_hit:
            continue

        for kf in shot.get("keyFrames", []) or []:
            kf_id = str(kf.get("id", ""))
            if not kf_id:
                continue

            for inst in kf.get("instances", []) or []:
                rng = extract_instance_range(inst)
                if not rng or not overlaps(scene_range, rng):
                    continue

                time_ms = rng[0]
                thumbnail_id = inst.get("thumbnailId") or kf.get("thumbnailId")
                candidate = {
                    "timeMs": time_ms,
                    "keyFrameId": kf_id,
                }
                if thumbnail_id:
                    candidate["thumbnailId"] = thumbnail_id
                    image_path = make_thumbnail_path(thumbnail_id, thumbnail_dir)
                    if image_path:
                        candidate["imagePath"] = image_path

                candidates.append(candidate)
                break

    if not candidates:
        return []

    candidates.sort(key=lambda x: x["timeMs"])

    unique: List[Dict[str, Any]] = []
    seen = set()
    for c in candidates:
        kid = c["keyFrameId"]
        if kid not in seen:
            seen.add(kid)
            unique.append(c)

    if len(unique) <= max_frames:
        return unique

    first = unique[0]
    mid_target = (scene_range[0] + scene_range[1]) // 2
    mid = min(unique, key=lambda x: abs(x["timeMs"] - mid_target))

    picked_ids = distinct_preserve_order([first["keyFrameId"], mid["keyFrameId"]])

    out: List[Dict[str, Any]] = []
    for c in unique:
        if c["keyFrameId"] in picked_ids:
            out.append(c)
        if len(out) >= max_frames:
            break

    return out


def aggregate_scene_facts(
    vi_json: Dict[str, Any],
    video_index: int = 0,
    max_ocr_items: int = 200,
    max_labels: int = 50,
    max_keyframes: int = 2,
    max_transcript_items: int = 200,
    max_faces: int = 20,
    max_people: int = 20,
    max_objects: int = 50,
    thumbnail_dir: Optional[str] = None,
) -> List[Dict[str, Any]]:
    video = vi_json["videos"][video_index]
    video_id = str(video.get("id", ""))

    insights = video["insights"]
    scenes = insights.get("scenes", []) or []
    shots = insights.get("shots", []) or []
    ocr_items = insights.get("ocr", []) or []
    label_items = insights.get("labels", []) or []

    transcript_items = insights.get("transcript", []) or []
    face_items = insights.get("faces", []) or []
    named_people_items = insights.get("namedPeople", []) or []
    detected_object_items = insights.get("detectedObjects", []) or []

    docs: List[Dict[str, Any]] = []

    for sc in scenes:
        scene_id = f"{video_id}_scene_{sc.get('id')}"
        inst = (sc.get("instances") or [{}])[0]
        begin_ms = parse_time_to_ms(inst["start"])
        end_ms = parse_time_to_ms(inst["end"])
        scene_range = (begin_ms, end_ms)

        # OCR
        ocr_texts = collect_text_items_for_scene(
            ocr_items, scene_range, text_field="text", max_items=max_ocr_items
        )
        ocr_text_joined = "\n".join(ocr_texts)

        # Transcript
        transcript_texts = collect_text_items_for_scene(
            transcript_items, scene_range, text_field="text", max_items=max_transcript_items
        )
        transcript_text_joined = "\n".join(transcript_texts)

        # Labels
        labels: List[str] = []
        for lb in label_items:
            name = normalize_space(lb.get("name", ""))
            if not name:
                continue
            for li in lb.get("instances", []) or []:
                rng = extract_instance_range(li)
                if rng and overlaps(scene_range, rng):
                    labels.append(name)
                    break
        labels = distinct_preserve_order(labels)[:max_labels]

        # Keyframes
        keyframes = pick_keyframes_for_scene(
            scene_range=scene_range,
            shots=shots,
            max_frames=max_keyframes,
            thumbnail_dir=thumbnail_dir,
        )

        representative_image_path = None
        if keyframes:
            representative_image_path = keyframes[0].get("imagePath")

        # Faces / NamedPeople / DetectedObjects
        faces = collect_named_entities_for_scene(
            face_items,
            scene_range,
            name_field="name",
            confidence_field="confidence",
            max_items=max_faces,
        )

        named_people = collect_named_entities_for_scene(
            named_people_items,
            scene_range,
            name_field="name",
            confidence_field="confidence",
            max_items=max_people,
        )

        detected_objects = collect_detected_objects_for_scene(
            detected_object_items,
            scene_range,
            max_items=max_objects,
        )

        doc = {
            "videoId": video_id,
            "sceneId": scene_id,
            "beginMs": begin_ms,
            "endMs": end_ms,
            "ocr_text": ocr_text_joined,
            "transcript_text": transcript_text_joined,
            "labels": labels,
            "keywords": [],
            "keyframes": keyframes,
            "representativeImagePath": representative_image_path,
            "faces": faces,
            "namedPeople": named_people,
            "detectedObjects": detected_objects,
        }
        docs.append(doc)

    return docs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--input", "-i", required=True, help="Video Indexer export JSON path")
    ap.add_argument("--output", "-o", required=True, help="Output JSON path")
    ap.add_argument("--video-index", type=int, default=0, help="videos[] index")
    ap.add_argument("--max-ocr-items", type=int, default=200)
    ap.add_argument("--max-labels", type=int, default=50)
    ap.add_argument("--max-keyframes", type=int, default=2)
    ap.add_argument("--max-transcript-items", type=int, default=200)
    ap.add_argument("--max-faces", type=int, default=20)
    ap.add_argument("--max-people", type=int, default=20)
    ap.add_argument("--max-objects", type=int, default=50)
    ap.add_argument(
        "--thumbnail-dir",
        default=None,
        help="KeyFrameThumbnail フォルダの親相対パス。例: chapter3_20260131_194157\\_KeyFrameThumbnail の親となる chapter3_20260131_194157"
    )
    args = ap.parse_args()

    with open(args.input, "r", encoding="utf-8") as f:
        vi_json = json.load(f)

    docs = aggregate_scene_facts(
        vi_json=vi_json,
        video_index=args.video_index,
        max_ocr_items=args.max_ocr_items,
        max_labels=args.max_labels,
        max_keyframes=args.max_keyframes,
        max_transcript_items=args.max_transcript_items,
        max_faces=args.max_faces,
        max_people=args.max_people,
        max_objects=args.max_objects,
        thumbnail_dir=args.thumbnail_dir,
    )

    with open(args.output, "w", encoding="utf-8") as f:
        json.dump(docs, f, ensure_ascii=False, indent=2)

    print(f"OK: wrote {len(docs)} scene docs -> {args.output}")


if __name__ == "__main__":
    main()