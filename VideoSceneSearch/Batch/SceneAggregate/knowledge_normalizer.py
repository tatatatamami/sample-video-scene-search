#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
データ統合・正規化モジュール。

- Video Indexer の scene_facts.json と ContentUnderstanding 出力を統合
- OCR ノイズ除去・人物名エイリアス展開
- Canonical Scene Knowledge の構築（検索単位に依存しない中間データ）

出力スキーマ (1 シーン):
{
    "videoId": str,
    "sceneId": str,
    "beginMs": int,
    "endMs": int,
    "transcript_text": str,
    "normalized_ocr_text": str,       # filter_ocr_text() 適用済み
    "labels": List[str],
    "people": List[str],              # Unknown 除外・エイリアス展開済み
    "detected_objects": List[str],    # detectedObjects から name を抽出
    "scene_summary": str,
    "representativeImagePath": Optional[str],
    "keyframes": List[{
        "keyFrameId": str,
        "thumbnailId": str,
        "timeMs": int,
        "imagePath": str,
        "image_description": str,
        "subject": str,
        "shot_type": str,
        "emotion": str,
        "scene_type": str,
        "objects": List[str],
    }]
}
"""

import json
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional


# ---------------------------------------------------------------------------
# 人物名エイリアスマッピング
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


# ---------------------------------------------------------------------------
# OCR フィルタリング
# ---------------------------------------------------------------------------

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
_OCR_MIN_CHARS = 3


def filter_ocr_text(raw_ocr: str) -> str:
    """ゲームUIノイズ（数字のみ行・ボタンラベル等）を除去した意味のあるOCRテキストを返す。"""
    lines = raw_ocr.splitlines()
    filtered: List[str] = []
    for line in lines:
        line = line.strip()
        if not line:
            continue
        if len(line) < _OCR_MIN_CHARS:
            continue
        if _OCR_NOISE_LINE.match(line):
            continue
        filtered.append(line)
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
    """Unknown 系の名前を除外し、エイリアスがあれば展開して返す。"""
    if not name or name.startswith("Unknown"):
        return None
    return FACE_NAME_ALIASES.get(name, name)


# ---------------------------------------------------------------------------
# ContentUnderstanding output loader
# ---------------------------------------------------------------------------

def load_cu_index(cu_output_dir: str) -> Dict[str, Dict[str, Any]]:
    """
    thumbnailId -> analysis dict のインデックスを構築する。
    ファイル名形式: KeyFrameThumbnail_{thumbnailId}.json
    """
    index: Dict[str, Dict[str, Any]] = {}
    dirpath = Path(cu_output_dir)
    if not dirpath.exists():
        return index

    for fpath in dirpath.glob("KeyFrameThumbnail_*.json"):
        stem = fpath.stem  # "KeyFrameThumbnail_<uuid>"
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
# 内部ヘルパー
# ---------------------------------------------------------------------------

def _normalize_people(scene_doc: Dict[str, Any]) -> List[str]:
    """faces / namedPeople から正規化済み人物名リストを生成する。"""
    people: List[str] = []
    seen: set = set()
    for f in (scene_doc.get("faces") or []):
        n = normalize_person_name(f.get("name", ""))
        if n and n not in seen:
            seen.add(n)
            people.append(n)
    for p in (scene_doc.get("namedPeople") or []):
        n = normalize_person_name(p.get("name", ""))
        if n and n not in seen:
            seen.add(n)
            people.append(n)
    return people


def _normalize_objects(scene_doc: Dict[str, Any]) -> List[str]:
    """detectedObjects から名前リストを生成する。"""
    return [
        o.get("name", "")
        for o in (scene_doc.get("detectedObjects") or [])
        if o.get("name")
    ]


def _enrich_keyframes(
    keyframes: List[Dict[str, Any]],
    cu_index: Dict[str, Dict[str, Any]],
) -> List[Dict[str, Any]]:
    """キーフレームに ContentUnderstanding 解析結果を統合する。

    アプリケーションが明示的に使用する安定フィールド（subject/shot_type等）はトップレベルに保持する。
    Analyzer固有のカスタムフィールド（biome/game_mode/scene_type 等）は
    analysis_fields に全て保持し失わないようにする。
    """
    enriched: List[Dict[str, Any]] = []
    for kf in keyframes:
        thumbnail_id = kf.get("thumbnailId", "")
        analysis = cu_index.get(thumbnail_id, {})
        enriched.append({
            "keyFrameId":        kf.get("keyFrameId", ""),
            "thumbnailId":       thumbnail_id,
            "timeMs":            kf.get("timeMs", 0),
            "imagePath":         kf.get("imagePath", ""),
            # アプリケーションが明示的に使用する安定フィールド
            "image_description": analysis.get("description", ""),
            "subject":           analysis.get("subject", ""),
            "shot_type":         analysis.get("shot_type", ""),
            "emotion":           analysis.get("emotion", ""),
            "objects":           analysis.get("objects") or [],
            # Analyzer固有フィールドを全て保持（scene_type / biome / game_mode 等）
            "analysis_fields":   dict(analysis),
        })
    return enriched


# ---------------------------------------------------------------------------
# Canonical Scene Knowledge builder
# ---------------------------------------------------------------------------

def build_canonical_scenes(
    scene_facts_path: str,
    cu_output_dir: str,
) -> List[Dict[str, Any]]:
    """
    scene_facts.json と ContentUnderstanding 出力から
    検索単位に依存しない Canonical Scene Knowledge のリストを生成する。
    """
    from knowledge_text import build_scene_summary  # 循環インポート回避のため関数内でインポート

    with open(scene_facts_path, encoding="utf-8") as f:
        scene_facts: List[Dict[str, Any]] = json.load(f)

    cu_index = load_cu_index(cu_output_dir)
    print(f"ContentUnderstanding index: {len(cu_index)} entries", file=sys.stderr)

    canonical: List[Dict[str, Any]] = []
    for doc in scene_facts:
        keyframes = _enrich_keyframes(doc.get("keyframes") or [], cu_index)
        people = _normalize_people(doc)
        detected_objects = _normalize_objects(doc)
        normalized_ocr = filter_ocr_text((doc.get("ocr_text") or "").strip())

        scene: Dict[str, Any] = {
            "videoId":               doc["videoId"],
            "sceneId":               doc["sceneId"],
            "beginMs":               doc["beginMs"],
            "endMs":                 doc["endMs"],
            "transcript_text":       (doc.get("transcript_text") or "").strip(),
            "normalized_ocr_text":   normalized_ocr,
            "labels":                doc.get("labels") or [],
            "people":                people,
            "detected_objects":      detected_objects,
            "representativeImagePath": doc.get("representativeImagePath"),
            "keyframes":             keyframes,
        }
        # scene_summary は正規化済みデータから生成
        scene["scene_summary"] = build_scene_summary(scene)
        canonical.append(scene)

    # 統計サマリー
    cu_hit = sum(1 for s in canonical for kf in s["keyframes"] if kf.get("image_description"))
    total_kf = sum(len(s["keyframes"]) for s in canonical)
    print(f"  シーン: {len(canonical)} 件", file=sys.stderr)
    print(f"  キーフレーム: {total_kf} 件中 {cu_hit} 件に画像解析あり", file=sys.stderr)

    people_set: set = set()
    for s in canonical:
        people_set.update(s["people"])
    if people_set:
        print(f"  登場人物: {sorted(people_set)}", file=sys.stderr)

    return canonical
