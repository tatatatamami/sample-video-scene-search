#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
scene_facts.json + ContentUnderstanding output を統合して、
検索エージェントのナレッジ用ドキュメントを生成する。

- scene_facts.json   : extract_scene_facts.py の出力 (transcript/OCR/labels/faces/keyframes)
- cu_output_dir      : GPT-4.1 Vision によるキーフレーム画像解析 JSON が格納されたフォルダ
  ファイル名形式: KeyFrameThumbnail_{thumbnailId}.json

Output: knowledge_docs.json
  - 1 シーン = 1 ドキュメント
  - 各キーフレームに画像解析結果を統合
  - scene_summary フィールド (シーンの簡潔な自然言語要約)
  - search_text フィールド (全テキストを自然言語検索用に結合、OCRノイズ除去済み)
"""

import argparse
import json
import os
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional


# ---------------------------------------------------------------------------
# 人物名エイリアスマッピング
# Video Indexer が検出した名前 -> 表示名 (別名・読み仮名を追加)
# face_name_aliases.json が存在する場合はそちらを優先して読み込む
# (face_name_aliases.json は .gitignore 対象 - 個人名が含まれるため)
# ---------------------------------------------------------------------------
def _load_face_name_aliases() -> Dict[str, str]:
    aliases_path = Path(__file__).parent / "face_name_aliases.json"
    if aliases_path.exists():
        try:
            with aliases_path.open(encoding="utf-8") as f:
                return json.load(f)
        except Exception as e:
            print(f"Warning: Failed to load face_name_aliases.json: {e}", file=sys.stderr)
    return {}

FACE_NAME_ALIASES: Dict[str, str] = _load_face_name_aliases()

# OCR フィルタリング: この正規表現にマッチする行はノイズとして除去
_OCR_NOISE_LINE = re.compile(
    r"^("
    r"\d[\d/\.\-\s]*"           # 数字のみ / 数値表現 (17, 4/6, 1.5, ...)
    r"|[A-Z0-9]{1,3}"           # 短い英数字 (R1, L1, HT, ...)
    r"|[×△○□◇◎★▶▷].*"          # UI ボタン記号
    r"|[lLrR][12].*"            # L1 R2 等のゲームパッドボタン
    r"|(戻る|決定|リセット|スキップ|進む|確定|選択|設定|メニュー|ヘルプ|バックログ|タイム)$"  # UIボタンテキスト
    r")$",
    re.UNICODE,
)

# OCR で意味のある最小文字数 (これ未満の行は除去)
_OCR_MIN_CHARS = 3


# ---------------------------------------------------------------------------
# ContentUnderstanding output loader
# ---------------------------------------------------------------------------

def load_cu_index(cu_output_dir: str) -> Dict[str, Dict[str, Any]]:
    """
    thumbnailId -> analysis dict のインデックスを構築する。
    ファイル名: KeyFrameThumbnail_{thumbnailId}.json
    """
    index: Dict[str, Dict[str, Any]] = {}
    dirpath = Path(cu_output_dir)
    if not dirpath.exists():
        return index

    for fpath in dirpath.glob("KeyFrameThumbnail_*.json"):
        # thumbnailId を名前から抽出
        stem = fpath.stem  # KeyFrameThumbnail_<uuid>
        parts = stem.split("_", 1)
        if len(parts) < 2:
            continue
        thumbnail_id = parts[1]

        try:
            with fpath.open(encoding="utf-8") as f:
                data = json.load(f)
            analysis = data.get("analysis") or {}
            index[thumbnail_id] = analysis
        except (OSError, json.JSONDecodeError, ValueError) as ex:
            print(f"Warning: Failed to load {fpath.name}: {ex}", file=sys.stderr)

    return index


# ---------------------------------------------------------------------------
# OCR フィルタリング
# ---------------------------------------------------------------------------

def filter_ocr_text(raw_ocr: str) -> str:
    """
    ゲームUIノイズ（数字のみ行・ボタンラベル等）を除去した意味のあるOCRテキストを返す。
    """
    lines = raw_ocr.splitlines()
    filtered: List[str] = []
    for line in lines:
        line = line.strip()
        if not line:
            continue
        # 短すぎる行を除去
        if len(line) < _OCR_MIN_CHARS:
            continue
        # ノイズパターンにマッチする行を除去
        if _OCR_NOISE_LINE.match(line):
            continue
        filtered.append(line)
    # 重複除去しつつ順序保持
    seen: set = set()
    unique: List[str] = []
    for line in filtered:
        if line not in seen:
            seen.add(line)
            unique.append(line)
    return "\n".join(unique)


# ---------------------------------------------------------------------------
# 人物名正規化
# ---------------------------------------------------------------------------

def normalize_person_name(name: str) -> Optional[str]:
    """
    Unknown 系の名前を除外し、エイリアスがあれば展開して返す。
    """
    if not name or name.startswith("Unknown"):
        return None
    return FACE_NAME_ALIASES.get(name, name)


# ---------------------------------------------------------------------------
# scene_summary builder
# ---------------------------------------------------------------------------

def build_scene_summary(
    scene_doc: Dict[str, Any],
    keyframes_with_analysis: List[Dict[str, Any]],
) -> str:
    """
    シーンを一言で表す自然言語サマリーを生成する。
    エージェントが検索クエリと照合しやすい形式。
    """
    parts: List[str] = []

    # 人物
    people = []
    for f in (scene_doc.get("faces") or []):
        n = normalize_person_name(f.get("name", ""))
        if n:
            people.append(n)
    for p in (scene_doc.get("namedPeople") or []):
        n = normalize_person_name(p.get("name", ""))
        if n and n not in people:
            people.append(n)
    if people:
        parts.append(f"登場人物: {', '.join(people)}")

    # キーフレーム画像説明 (最初の1枚だけ)
    for kf in keyframes_with_analysis[:1]:
        desc = kf.get("image_description", "").strip()
        if desc:
            parts.append(f"映像: {desc}")

    # 音声の先頭80文字
    transcript = (scene_doc.get("transcript_text") or "").strip()
    if transcript:
        short = transcript[:80].replace("\n", " ")
        parts.append(f"音声: {short}")

    # ラベルから上位3件
    labels = (scene_doc.get("labels") or [])[:3]
    if labels:
        parts.append(f"シーン: {', '.join(labels)}")

    return " / ".join(parts)


# ---------------------------------------------------------------------------
# search_text builder
# ---------------------------------------------------------------------------

def build_search_text(
    scene_doc: Dict[str, Any],
    keyframes_with_analysis: List[Dict[str, Any]],
) -> str:
    """
    全テキストフィールドを結合した自然言語検索用テキストを生成する。
    OCRノイズを除去し、人物名にエイリアスを付加する。
    """
    parts: List[str] = []

    # 音声書き起こし
    transcript = (scene_doc.get("transcript_text") or "").strip()
    if transcript:
        parts.append(f"【音声】{transcript}")

    # OCR テキスト (ノイズ除去済み)
    ocr_raw = (scene_doc.get("ocr_text") or "").strip()
    ocr = filter_ocr_text(ocr_raw)
    if ocr:
        parts.append(f"【テキスト】{ocr}")

    # ラベル
    labels = scene_doc.get("labels") or []
    if labels:
        parts.append(f"【ラベル】{', '.join(labels)}")

    # 人物 (faces / namedPeople) - Unknown 除外・エイリアス付加
    people_names: List[str] = []
    seen_people: set = set()
    for f in (scene_doc.get("faces") or []):
        n = normalize_person_name(f.get("name", ""))
        if n and n not in seen_people:
            seen_people.add(n)
            people_names.append(n)
    for p in (scene_doc.get("namedPeople") or []):
        n = normalize_person_name(p.get("name", ""))
        if n and n not in seen_people:
            seen_people.add(n)
            people_names.append(n)
    if people_names:
        parts.append(f"【人物】{', '.join(people_names)}")

    # 検出オブジェクト
    objects = [o.get("name", "") for o in (scene_doc.get("detectedObjects") or []) if o.get("name")]
    if objects:
        parts.append(f"【オブジェクト】{', '.join(objects)}")

    # キーフレーム画像解析
    for kf in keyframes_with_analysis:
        desc = kf.get("image_description", "")
        subject = kf.get("subject", "")
        emotion = kf.get("emotion", "")
        scene_type = kf.get("scene_type", "")
        shot_type = kf.get("shot_type", "")
        kf_objects = kf.get("objects") or []

        kf_parts = []
        if desc:
            kf_parts.append(desc)
        if subject and subject not in ("UNKNOWN", "NO_CLEAR_SUBJECT"):
            kf_parts.append(f"被写体: {subject}")
        if emotion and emotion not in ("UNKNOWN", "NEUTRAL"):
            kf_parts.append(f"感情: {emotion}")
        if scene_type and scene_type != "UNKNOWN":
            kf_parts.append(f"シーンタイプ: {scene_type}")
        if shot_type and shot_type != "UNKNOWN":
            kf_parts.append(f"ショットタイプ: {shot_type}")
        if kf_objects:
            kf_parts.append(f"物体: {', '.join(kf_objects)}")

        if kf_parts:
            parts.append(f"【キーフレーム画像】{' / '.join(kf_parts)}")

    return "\n".join(parts)


# ---------------------------------------------------------------------------
# Face lookup helper
# ---------------------------------------------------------------------------

def build_face_lookup(scene_docs: List[Dict[str, Any]]) -> Dict[str, List[str]]:
    """
    thumbnailId -> [face_name, ...] のルックアップを作成。
    faces はシーン単位なので、シーン内のキーフレームthumbnailIdすべてに適用。
    """
    # キーフレームのthumbnailId -> シーン内の人物名リスト
    # (シーン集約レベルで既に含まれているため、ここでは補助情報として返す)
    lookup: Dict[str, List[str]] = {}
    for doc in scene_docs:
        people = []
        for f in (doc.get("faces") or []):
            n = f.get("name", "")
            if n:
                people.append(n)
        for p in (doc.get("namedPeople") or []):
            n = p.get("name", "")
            if n and n not in people:
                people.append(n)
        for kf in (doc.get("keyframes") or []):
            tid = kf.get("thumbnailId", "")
            if tid:
                lookup[tid] = people
    return lookup


# ---------------------------------------------------------------------------
# Main merge logic
# ---------------------------------------------------------------------------

def build_knowledge_docs(
    scene_facts_path: str,
    cu_output_dir: str,
    output_path: str,
) -> None:
    with open(scene_facts_path, encoding="utf-8") as f:
        scene_docs: List[Dict[str, Any]] = json.load(f)

    cu_index = load_cu_index(cu_output_dir)
    print(f"ContentUnderstanding index: {len(cu_index)} entries")

    knowledge_docs: List[Dict[str, Any]] = []

    for doc in scene_docs:
        # キーフレームに画像解析を統合
        enriched_keyframes: List[Dict[str, Any]] = []
        for kf in (doc.get("keyframes") or []):
            thumbnail_id = kf.get("thumbnailId", "")
            analysis = cu_index.get(thumbnail_id, {})

            enriched_kf = {
                "keyFrameId":    kf.get("keyFrameId", ""),
                "thumbnailId":   thumbnail_id,
                "timeMs":        kf.get("timeMs", 0),
                "imagePath":     kf.get("imagePath", ""),
                # GPT-4.1 Vision 解析結果
                "image_description": analysis.get("description", ""),
                "subject":           analysis.get("subject", ""),
                "shot_type":         analysis.get("shot_type", ""),
                "emotion":           analysis.get("emotion", ""),
                "scene_type":        analysis.get("scene_type", ""),
                "objects":           analysis.get("objects") or [],
            }
            enriched_keyframes.append(enriched_kf)

        # scene_summary と search_text を生成
        scene_summary = build_scene_summary(doc, enriched_keyframes)
        search_text = build_search_text(doc, enriched_keyframes)

        knowledge_doc = {
            # 識別子
            "id":       doc["sceneId"],
            "videoId":  doc["videoId"],
            "sceneId":  doc["sceneId"],
            "beginMs":  doc["beginMs"],
            "endMs":    doc["endMs"],
            # シーン要約 (自然言語検索用)
            "scene_summary": scene_summary,
            # テキスト情報
            "transcript_text": doc.get("transcript_text", ""),
            "ocr_text":        doc.get("ocr_text", ""),
            "labels":          doc.get("labels", []),
            # 人物情報
            "faces":           doc.get("faces", []),
            "namedPeople":     doc.get("namedPeople", []),
            "detectedObjects": doc.get("detectedObjects", []),
            # キーフレーム (画像解析統合済み)
            "keyframes":             enriched_keyframes,
            "representativeImagePath": doc.get("representativeImagePath"),
            # 自然言語検索用テキスト
            "search_text": search_text,
        }
        knowledge_docs.append(knowledge_doc)

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(knowledge_docs, f, ensure_ascii=False, indent=2)

    print(f"OK: wrote {len(knowledge_docs)} knowledge docs -> {output_path}")

    # サマリー表示
    cu_hit = sum(
        1 for doc in knowledge_docs
        for kf in doc.get("keyframes", [])
        if kf.get("image_description")
    )
    total_kf = sum(len(doc.get("keyframes", [])) for doc in knowledge_docs)
    print(f"  キーフレーム: {total_kf} 件中 {cu_hit} 件に画像解析あり")

    people_set = set()
    for doc in knowledge_docs:
        for f in doc.get("faces", []):
            if f.get("name"):
                people_set.add(f["name"])
        for p in doc.get("namedPeople", []):
            if p.get("name"):
                people_set.add(p["name"])
    if people_set:
        print(f"  登場人物: {sorted(people_set)}")


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main() -> None:
    ap = argparse.ArgumentParser(
        description="scene_facts.json と ContentUnderstanding 出力を統合してナレッジドキュメントを生成する"
    )
    ap.add_argument("--scene-facts",  "-s", required=True,  help="scene_aggregate.py の出力 JSON")
    ap.add_argument("--cu-output",    "-c", required=True,  help="ContentUnderstanding output フォルダ")
    ap.add_argument("--output",       "-o", required=True,  help="出力 knowledge_docs.json パス")
    args = ap.parse_args()

    build_knowledge_docs(
        scene_facts_path=args.scene_facts,
        cu_output_dir=args.cu_output,
        output_path=args.output,
    )


if __name__ == "__main__":
    main()
