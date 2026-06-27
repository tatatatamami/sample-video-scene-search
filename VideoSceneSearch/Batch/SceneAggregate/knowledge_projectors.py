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

設計の位置づけ:
  Azure AI Search の Parent-Child パターン（Index Projections）を参考に、
  アプリケーション側でシーン／キーフレーム単位の検索ドキュメントを生成する。

  現在のアップロード先は Foundry File Search（Vector Store）であり、
  Azure AI Search の Index Projections 機能そのものを使用しているわけではない。
  そのため以下の点に留意が必要:

  - documentType フィールドは File Search ではフィルター不可（単なる文字列）。
    "documentType eq 'keyframe'" 形式のフィルターは、将来 Azure AI Search の
    カスタムインデックスへ移行した場合にのみ成立する。

  - JSON 配列の 1 要素 = 1 検索チャンクになる保証はない。
    File Search はアップロードファイルを自動チャンク化（既定: 800 トークン、
    400 オーバーラップ）するため、実際の検索粒度は File Search 側が決定する。

  厳密な 1 Scene / 1 Keyframe 単位での検索・フィルターが必要な場合は、
  Azure AI Search のカスタムインデックスへの移行を検討すること。
"""

from typing import Any, Dict, List

from knowledge_text import build_scene_search_text, build_keyframe_search_text


def project_scene_document(scene: Dict[str, Any]) -> Dict[str, Any]:
    """
    Canonical Scene から 1 シーン = 1 検索ドキュメントを生成する。

    id: sceneId をそのまま使用する。
    sceneId は extract_scene_facts.py が {videoId}_scene_{n} 形式で生成済み。
    """
    video_id = scene["videoId"]
    scene_id = scene["sceneId"]

    return {
        "id":            scene_id,
        "documentType":  "scene",
        "videoId":       video_id,
        "sceneId":       scene_id,
        "beginMs":       scene["beginMs"],
        "endMs":         scene["endMs"],
        "representativeImagePath": scene.get("representativeImagePath"),
        "scene_summary": scene.get("scene_summary", ""),
        # 人物リスト（Azure AI Search の Collection(Edm.String) フィルター対応）
        "scenePeople":   list(scene.get("people") or []),
        "search_text":   build_scene_search_text(scene),
    }


def project_keyframe_documents(scene: Dict[str, Any]) -> List[Dict[str, Any]]:
    """
    Canonical Scene から 1 キーフレーム = 1 検索ドキュメントのリストを生成する。

    id: {sceneId}_keyframe_{keyFrameId}
    sceneId は extract_scene_facts.py が {videoId}_scene_{n} 形式で生成済み。

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
            "id":           f"{scene_id}_keyframe_{keyframe_id}",
            "documentType": "keyframe",
            "videoId":      video_id,
            "sceneId":      scene_id,
            "keyFrameId":   keyframe_id,
            "timeMs":       time_ms,
            "beginMs":      begin_ms,
            "endMs":        end_ms,
            "imagePath":    keyframe.get("imagePath", ""),
            # 人物リスト
            # scenePeople: 親シーンで検出された人物（シーン単位の people リストから）
            # visiblePeople: フレームに実際に映っている人物
            #   ※ Video Indexer の出現区間とキーフレーム時刻の重なりで判定すべきだが、
            #      現時点は extract_scene_facts.py が時間レンジを保持しないため実装不可。
            #      誤った意味でシーン人物をコピーするより空配列が安全。
            "scenePeople":   list(scene.get("people") or []),
            "visiblePeople": [],
            "search_text":  build_keyframe_search_text(scene, keyframe),
        })

    return documents
