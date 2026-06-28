#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
検索テキスト構築モジュール。

Canonical Scene Knowledge（knowledge_normalizer.py の出力）を受け取り、
検索用テキストの「部品」と「組み立て」を提供する。

責務分離:
  - build_scene_context_parts()   : シーン・キーフレーム共通のコンテキスト部品
  - build_keyframe_visual_parts() : キーフレーム固有の画像情報部品
  - build_scene_search_text()     : シーン単位の search_text
  - build_keyframe_search_text()  : キーフレーム単位の search_text
"""

from typing import Any, Dict, List, Optional


# CU フィールドで「意味なし」と見なす値
_SENTINEL_VALUES = frozenset({"UNKNOWN", "NO_CLEAR_SUBJECT", "NEUTRAL", ""})
# Analyzer固有フィールドの検索テキスト出力設定
# field_name: display_label のマッピング。
# 追加・削除することで、動画ごとの検索対象フィールドを調整できる。
# 例: 他ジャンル向けに "weather" フィールドを追加する場合:
#   SEARCHABLE_ANALYSIS_FIELDS["weather"] = "天候"
SEARCHABLE_ANALYSIS_FIELDS: Dict[str, str] = {
    "scene_type": "シーンタイプ",
    "biome":      "バイオーム",
    "game_mode":  "ゲームモード",
}

# ---------------------------------------------------------------------------
# 共通ユーティリティ
# ---------------------------------------------------------------------------

def append_text(parts: List[str], label: str, value: Optional[str]) -> None:
    """値が空でなければ 〖label〗value を parts に追加する。

    〖〗（隙付き白鵒括弧）は AI Search のベクトル化テキスト内のセクション区切りマーカー。
    ラテン文字が少な日本語テキストでもトークナイザが認識しやすい記号。
    """
    text = (value or "").strip()
    if text:
        parts.append(f"〖{label}〗{text}")


def append_values(parts: List[str], label: str, values: Optional[List[str]]) -> None:
    """値リストが空でなければ 〖label〗v1, v2, ... を parts に追加する。"""
    normalized = [v.strip() for v in (values or []) if v and v.strip()]
    if normalized:
        parts.append(f"〖{label}〗{', '.join(normalized)}")


# ---------------------------------------------------------------------------
# シーン要約
# ---------------------------------------------------------------------------

def build_scene_summary(scene: Dict[str, Any]) -> str:
    """
    シーンを一言で表す自然言語サマリーを生成する。
    Canonical Scene を受け取る（people は正規化済み・OCR はフィルタ済み）。
    エージェントが検索クエリと照合しやすい形式。
    """
    parts: List[str] = []

    if scene.get("people"):
        parts.append(f"登場人物: {', '.join(scene['people'])}")

    # 最初のキーフレームの画像説明
    for kf in (scene.get("keyframes") or [])[:1]:
        desc = (kf.get("image_description") or "").strip()
        if desc:
            parts.append(f"映像: {desc}")

    # 音声の先頤80文字（サマリーの簡潔さを保つための上限）
    transcript = (scene.get("transcript_text") or "").strip()
    if transcript:
        short = transcript[:80].replace("\n", " ")
        parts.append(f"音声: {short}")

    # ラベルから上位3件
    labels = (scene.get("labels") or [])[:3]
    if labels:
        parts.append(f"シーン: {', '.join(labels)}")

    return " / ".join(parts)


# ---------------------------------------------------------------------------
# 共通部品
# ---------------------------------------------------------------------------

def build_scene_context_parts(
    scene: Dict[str, Any],
    *,
    include_transcript: bool = True,
    include_ocr: bool = True,
) -> List[str]:
    """
    シーンとキーフレームの両方で利用する共通コンテキスト部品を返す。

    include_ocr=False を指定することで、キーフレーム検索テキストにおいて
    シーン全体のOCRを繰り返し付加することを避けられる。
    （同一シーン内のキーフレームが類似ベクトルになることを防ぐため）
    """
    parts: List[str] = []

    if include_transcript:
        append_text(parts, "音声", scene.get("transcript_text"))

    if include_ocr:
        append_text(parts, "テキスト", scene.get("normalized_ocr_text"))

    append_values(parts, "ラベル", scene.get("labels"))
    append_values(parts, "人物", scene.get("people"))
    append_values(parts, "オブジェクト", scene.get("detected_objects"))

    return parts


def build_keyframe_visual_parts(keyframe: Dict[str, Any]) -> List[str]:
    """キーフレーム固有の画像情報部品を返す。"""
    parts: List[str] = []

    append_text(parts, "画像", keyframe.get("image_description"))

    metadata: List[str] = []

    subject = (keyframe.get("subject") or "")
    if subject not in _SENTINEL_VALUES:
        metadata.append(f"被写体: {subject}")

    shot_type = (keyframe.get("shot_type") or "")
    if shot_type not in _SENTINEL_VALUES:
        metadata.append(f"ショットタイプ: {shot_type}")

    emotion = (keyframe.get("emotion") or "")
    if emotion not in _SENTINEL_VALUES:
        metadata.append(f"感情: {emotion}")

    objects = keyframe.get("objects") or []
    if objects:
        metadata.append(f"物体: {', '.join(objects)}")

    # Analyzer固有フィールド（SEARCHABLE_ANALYSIS_FIELDS に列挙されたフィールドのみ）
    analysis_fields = keyframe.get("analysis_fields") or {}
    for field_name, display_name in SEARCHABLE_ANALYSIS_FIELDS.items():
        value = analysis_fields.get(field_name)
        if isinstance(value, str) and value not in _SENTINEL_VALUES:
            metadata.append(f"{display_name}: {value}")

    if metadata:
        parts.append("〖画像メタ〗" + " / ".join(metadata))

    return parts


# ---------------------------------------------------------------------------
# search_text 組み立て
# ---------------------------------------------------------------------------

def build_scene_search_text(scene: Dict[str, Any]) -> str:
    """
    シーン検索ドキュメント用 search_text を生成する。

    先頭に [文書メタデータ] ブロックを追加することで、
    Foundry Toolbox の Azure AI Search MCP ツールが返す content テキストから
    Hosted Agent が videoId / beginMs / endMs / documentType などを抽出できる。

    シーン全体の内容（全キーフレームの画像説明を含む）を検索対象とする。
    会話・出来事を探す用途に適している。
    """
    parts: List[str] = []

    # --- 構造化メタデータヘッダー (MCP ツール応答からエージェントが抽出する) ---
    parts.append("[文書メタデータ]")
    parts.append(f"id: {scene.get('sceneId', '')}")
    parts.append(f"videoId: {scene.get('videoId', '')}")
    parts.append(f"beginMs: {scene.get('beginMs', 0)}")
    parts.append(f"endMs: {scene.get('endMs', 0)}")
    parts.append(f"documentType: visual")  # シーンドキュメントは常に visual（映像）型
    parts.append("[/文書メタデータ]")

    append_text(parts, "シーン要約", scene.get("scene_summary"))
    parts.extend(build_scene_context_parts(scene, include_transcript=True, include_ocr=True))

    for kf in (scene.get("keyframes") or []):
        parts.extend(build_keyframe_visual_parts(kf))

    return "\n".join(parts)


def build_keyframe_search_text(scene: Dict[str, Any], keyframe: Dict[str, Any]) -> str:
    """
    キーフレーム検索ドキュメント用 search_text を生成する。

    対象画像の説明を最優先にし、親シーンのコンテキストを補完する。
    特定の画面・映像を探す用途に適している。

    OCR はシーン全体のものを全キーフレームへ繰り返し付加すると
    同一シーン内のキーフレームが類似ベクトルになりやすいため除外する。
    """
    parts: List[str] = []

    # キーフレーム固有情報を先頭に（最重要）
    parts.extend(build_keyframe_visual_parts(keyframe))

    # 親シーン要約
    append_text(parts, "シーン要約", scene.get("scene_summary"))

    # 共通コンテキスト（OCR は除外）
    parts.extend(
        build_scene_context_parts(scene, include_transcript=True, include_ocr=False)
    )

    return "\n".join(parts)
