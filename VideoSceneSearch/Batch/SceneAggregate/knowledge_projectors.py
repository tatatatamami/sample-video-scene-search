#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
検索ドキュメント Projector モジュール。

Canonical Scene Knowledge（knowledge_normalizer.py の出力）を受け取り、
検索単位（シーン / キーフレーム）に応じた最終検索ドキュメントを生成する。

このモジュールが担う責務:
  - ドキュメントの id / documentType の付与
  - キーフレームの beginMs / endMs 算出（前後キーフレームとの中間点）
  - search_text の組み立て呼び出し（knowledge_text へ委譲）

Azure AI Search の Index Projections における Parent-Child パターンに対応:
  - シーン: documentType = "scene"
  - キーフレーム: documentType = "keyframe"
  - documentType を filterable にすることで検索粒度を切り替え可能
"""

from typing import Any, Dict, List

from knowledge_text import build_scene_search_text, build_keyframe_search_text


def project_scene_document(scene: Dict[str, Any]) -> Dict[str, Any]:
    """
    Canonical Scene から 1 シーン = 1 検索ドキュメントを生成する。

    id 形式: {videoId}_scene_{sceneId}
    """
    video_id = scene["videoId"]
    scene_id = scene["sceneId"]

    return {
        "id":            f"{video_id}_scene_{scene_id}",
        "documentType":  "scene",
        "videoId":       video_id,
        "sceneId":       scene_id,
        "beginMs":       scene["beginMs"],
        "endMs":         scene["endMs"],
        "representativeImagePath": scene.get("representativeImagePath"),
        "scene_summary": scene.get("scene_summary", ""),
        "search_text":   build_scene_search_text(scene),
    }


def project_keyframe_documents(scene: Dict[str, Any]) -> List[Dict[str, Any]]:
    """
    Canonical Scene から 1 キーフレーム = 1 検索ドキュメントのリストを生成する。

    id 形式: {videoId}_scene_{sceneId}_keyframe_{keyFrameId}

    beginMs / endMs:
      - 最初のキーフレーム: beginMs = シーン開始
      - 最後のキーフレーム: endMs   = シーン終了
      - それ以外: 前後キーフレームとの中間点
    """
    documents: List[Dict[str, Any]] = []
    video_id = scene["videoId"]
    scene_id = scene["sceneId"]
    scene_begin = scene["beginMs"]
    scene_end = scene["endMs"]

    keyframes = sorted(
        scene.get("keyframes") or [],
        key=lambda kf: kf.get("timeMs", 0),
    )

    for index, keyframe in enumerate(keyframes):
        time_ms = keyframe.get("timeMs", 0)

        if index == 0:
            begin_ms = scene_begin
        else:
            begin_ms = (keyframes[index - 1].get("timeMs", scene_begin) + time_ms) // 2

        if index == len(keyframes) - 1:
            end_ms = scene_end
        else:
            end_ms = (time_ms + keyframes[index + 1].get("timeMs", scene_end)) // 2

        keyframe_id = (
            keyframe.get("keyFrameId")
            or keyframe.get("thumbnailId")
            or f"{scene_id}_{index}"
        )

        documents.append({
            "id":           f"{video_id}_scene_{scene_id}_keyframe_{keyframe_id}",
            "documentType": "keyframe",
            "videoId":      video_id,
            "sceneId":      scene_id,
            "keyFrameId":   keyframe_id,
            "timeMs":       time_ms,
            "beginMs":      begin_ms,
            "endMs":        end_ms,
            "imagePath":    keyframe.get("imagePath", ""),
            "search_text":  build_keyframe_search_text(scene, keyframe),
        })

    return documents
