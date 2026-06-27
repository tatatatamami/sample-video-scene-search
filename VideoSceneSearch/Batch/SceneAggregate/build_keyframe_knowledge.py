#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
prospi_knowledge_docs.json (シーン単位) をキーフレーム単位に変換する。

- 1 キーフレーム = 1 ドキュメント
- beginMs / endMs: 前後のキーフレームとの中間点で算出
  (最初のキーフレームはシーン開始から、最後はシーン終了まで)
- search_text: キーフレーム固有の画像説明 + 親シーンのトランスクリプト・OCR・ラベル・人物

Usage:
    python build_keyframe_knowledge.py \
        --input  output/prospi_knowledge_docs.json \
        --output output/prospi_keyframe_docs.json
"""

import argparse
import json
from pathlib import Path
from typing import Any, Dict, List, Optional

from build_scene_knowledge import filter_ocr_text, normalize_person_name


def build_keyframe_search_text(kf: Dict[str, Any], scene: Dict[str, Any]) -> str:
    parts: List[str] = []

    # 1. このキーフレーム固有の画像説明（最重要 - 先頭に置く）
    desc = kf.get("image_description", "")
    if desc:
        parts.append(f"【画像】{desc}")

    # scene_type / shot_type / emotion / objects など画像メタ
    meta_parts = []
    if kf.get("subject") and kf["subject"] not in ("NO_CLEAR_SUBJECT", "UNKNOWN"):
        meta_parts.append(f"被写体: {kf['subject']}")
    if kf.get("scene_type") and kf["scene_type"] != "UNKNOWN":
        meta_parts.append(f"シーンタイプ: {kf['scene_type']}")
    if kf.get("shot_type") and kf["shot_type"] != "UNKNOWN":
        meta_parts.append(f"ショットタイプ: {kf['shot_type']}")
    if kf.get("emotion") and kf["emotion"] not in ("NEUTRAL", "UNKNOWN"):
        meta_parts.append(f"感情: {kf['emotion']}")
    objects = kf.get("objects", [])
    if objects:
        meta_parts.append(f"物体: {', '.join(objects)}")
    if meta_parts:
        parts.append("【画像メタ】" + " / ".join(meta_parts))

    # 2. 親シーンのトランスクリプト
    transcript = scene.get("transcript_text", "").strip()
    if transcript:
        parts.append(f"【音声】{transcript}")

    # 3. 親シーンのOCR（ノイズが多いが含める）
    ocr = scene.get("ocr_text", "").strip()
    if ocr:
        filtered_ocr = filter_ocr_text(ocr)
        if filtered_ocr:
            parts.append("【テキスト】" + filtered_ocr)

    # 4. ラベル
    labels = scene.get("labels", [])
    if labels:
        parts.append("【ラベル】" + ", ".join(labels))

    # 5. 人物
    people: List[str] = []
    for f in scene.get("faces", []):
        name = normalize_person_name(f.get("name", ""))
        if name:
            people.append(name)
    for p in scene.get("namedPeople", []):
        name = normalize_person_name(p.get("name", ""))
        if name and name not in people:
            people.append(name)
    if people:
        parts.append("【人物】" + ", ".join(people))

    return "\n".join(parts)


def convert(input_path: str, output_path: str) -> None:
    with open(input_path, encoding="utf-8-sig") as f:
        scenes: List[Dict[str, Any]] = json.load(f)

    keyframe_docs: List[Dict[str, Any]] = []

    for scene in scenes:
        video_id = scene.get("videoId", "")
        scene_begin = scene.get("beginMs", 0)
        scene_end = scene.get("endMs", 0)
        keyframes = scene.get("keyframes", [])

        for idx, kf in enumerate(keyframes):
            kf_time = kf.get("timeMs", 0)

            # beginMs: 前キーフレームとの中間点 (最初はシーン開始)
            if idx == 0:
                begin_ms = scene_begin
            else:
                prev_time = keyframes[idx - 1].get("timeMs", scene_begin)
                begin_ms = (prev_time + kf_time) // 2

            # endMs: 次キーフレームとの中間点 (最後はシーン終了)
            if idx == len(keyframes) - 1:
                end_ms = scene_end
            else:
                next_time = keyframes[idx + 1].get("timeMs", scene_end)
                end_ms = (kf_time + next_time) // 2

            kf_id = kf.get("keyFrameId", str(idx))
            doc_id = f"{video_id}_kf_{kf_id}"

            doc: Dict[str, Any] = {
                "id": doc_id,
                "videoId": video_id,
                "keyFrameId": kf_id,
                "sceneId": scene.get("sceneId", ""),
                "timeMs": kf_time,
                "beginMs": begin_ms,
                "endMs": end_ms,
                "image_description": kf.get("image_description", ""),
                "scene_type": kf.get("scene_type", ""),
                "shot_type": kf.get("shot_type", ""),
                "emotion": kf.get("emotion", ""),
                "subject": kf.get("subject", ""),
                "objects": kf.get("objects", []),
                "transcript_text": scene.get("transcript_text", ""),
                "labels": scene.get("labels", []),
                "faces": scene.get("faces", []),
                "namedPeople": scene.get("namedPeople", []),
                "search_text": build_keyframe_search_text(kf, scene),
            }
            keyframe_docs.append(doc)

    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(keyframe_docs, f, ensure_ascii=False, indent=2)

    print(f"変換完了: {len(scenes)} シーン → {len(keyframe_docs)} キーフレームドキュメント")
    print(f"出力: {output_path}")


def main() -> None:
    parser = argparse.ArgumentParser(description="シーン単位 → キーフレーム単位 knowledge docs 変換")
    parser.add_argument("--input",  default="output/prospi_knowledge_docs.json")
    parser.add_argument("--output", default="output/prospi_keyframe_docs.json")
    args = parser.parse_args()

    convert(args.input, args.output)


if __name__ == "__main__":
    main()
