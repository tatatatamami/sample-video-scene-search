#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Canonical Scene Knowledge をシーン・キーフレーム単位の検索ドキュメントへ変換する。

パイプライン:
  extract_scene_facts.py
      → (scene_facts.json)
  build_knowledge.py                  ← このスクリプト
      → (scene_docs.json)             シーン単位検索ドキュメント
      → (keyframe_docs.json)          キーフレーム単位検索ドキュメント
  upload_to_vectorstore.py

CU 結果の読み込み・OCR フィルタリング・人物名正規化を 1 回だけ行い、
シーン単位とキーフレーム単位の出力を用途別 Projection として生成する。

Usage:
    # 両方生成（推奨）
    python build_knowledge.py \\
        --scene-facts  output/your-video/scene_facts.json \\
        --cu-output    ../ContentUnderstanding/output/YourVideo \\
        --scene-output output/your-video/scene_docs.json \\
        --keyframe-output output/your-video/keyframe_docs.json

    # キーフレームのみ
    python build_knowledge.py \\
        --scene-facts  output/your-video/scene_facts.json \\
        --cu-output    ../ContentUnderstanding/output/YourVideo \\
        --keyframe-output output/your-video/keyframe_docs.json
"""

import argparse
import json
import os
import sys
from typing import Any, List, Optional

from knowledge_normalizer import build_canonical_scenes
from knowledge_projectors import project_scene_document, project_keyframe_documents


def write_json(path: str, data: Any) -> None:
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


def build_documents(
    scene_facts_path: str,
    cu_output_dir: str,
    scene_output_path: Optional[str],
    keyframe_output_path: Optional[str],
) -> None:
    """
    Canonical Scene Knowledge を 1 回生成し、
    シーン・キーフレームそれぞれの検索ドキュメントを出力する。
    """
    canonical_scenes = build_canonical_scenes(scene_facts_path, cu_output_dir)

    if scene_output_path:
        scene_docs = [project_scene_document(s) for s in canonical_scenes]
        write_json(scene_output_path, scene_docs)
        print(f"OK: {len(scene_docs)} scene docs -> {scene_output_path}")

    if keyframe_output_path:
        keyframe_docs: List[Any] = [
            doc
            for scene in canonical_scenes
            for doc in project_keyframe_documents(scene)
        ]
        write_json(keyframe_output_path, keyframe_docs)
        print(f"OK: {len(canonical_scenes)} シーン -> {len(keyframe_docs)} keyframe docs -> {keyframe_output_path}")


def main() -> None:
    ap = argparse.ArgumentParser(
        description=(
            "scene_facts.json と ContentUnderstanding 出力から"
            "シーン・キーフレーム単位の検索ドキュメントを生成する"
        )
    )
    ap.add_argument("--scene-facts",      "-s", required=True, help="extract_scene_facts.py の出力 JSON")
    ap.add_argument("--cu-output",        "-c", required=True, help="ContentUnderstanding output フォルダ")
    ap.add_argument("--scene-output",           help="シーン単位 scene_docs.json の出力パス")
    ap.add_argument("--keyframe-output",        help="キーフレーム単位 keyframe_docs.json の出力パス")
    args = ap.parse_args()

    if not args.scene_output and not args.keyframe_output:
        ap.error("--scene-output か --keyframe-output のいずれか一方以上を指定してください。")

    build_documents(
        scene_facts_path=args.scene_facts,
        cu_output_dir=args.cu_output,
        scene_output_path=args.scene_output,
        keyframe_output_path=args.keyframe_output,
    )


if __name__ == "__main__":
    main()
