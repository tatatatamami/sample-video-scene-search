#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Azure Content Understanding を使用してキーフレーム画像をバッチ解析するスクリプト。

FeldSchema_*.json で定義したフィールドを Content Understanding Analyzer に自動登録し、
入力ディレクトリ内のキーフレーム JPG を一括解析する。
出力は KeyFrameThumbnail_{thumbnailId}.json 形式で保存され、build_knowledge.py により読み込むことができる。

Prerequisites:
  pip install azure-identity requests

Usage:
  python run-cu-batch.py \\
    --input-dir  "input/_KeyFrameThumbnail" \\
    --output-dir "output/MyVideo" \\
    [--schema-file  "FeldSchema_sample.json"] \\
    [--endpoint     "https://myresource.cognitiveservices.azure.com"] \\
    [--analyzer-id  "keyframe-scene-analyzer"] \\
    [--force]
"""

import argparse
import json
import os
import sys
import time
import uuid
from pathlib import Path

# ---------------------------------------------------------------------------
# 依存パッケージの存在チェック
# ---------------------------------------------------------------------------
try:
    import requests
except ImportError:
    print("ERROR: 'requests' が必要です。  pip install requests", file=sys.stderr)
    sys.exit(1)

try:
    from azure.identity import DefaultAzureCredential
except ImportError:
    print("ERROR: 'azure-identity' が必要です。  pip install azure-identity", file=sys.stderr)
    sys.exit(1)

# ---------------------------------------------------------------------------
# 定数
# ---------------------------------------------------------------------------
API_VERSION = "2025-11-01"
TOKEN_SCOPE = "https://cognitiveservices.azure.com/.default"
SUPPORTED_EXTS = {".jpg", ".jpeg", ".png"}
TOKEN_REFRESH_EVERY = 50   # N 枚ごとにトークンを再取得


# ---------------------------------------------------------------------------
# 認証
# ---------------------------------------------------------------------------

def get_access_token() -> str:
    credential = DefaultAzureCredential()
    token = credential.get_token(TOKEN_SCOPE)
    return token.token


# ---------------------------------------------------------------------------
# スキーマ変換: FeldSchema_*.json → Content Understanding fieldSchema
# ---------------------------------------------------------------------------

def load_schema(schema_file: str) -> dict:
    with open(schema_file, encoding="utf-8-sig") as f:
        return json.load(f)


def build_cu_fieldschema(schema: dict) -> dict:
    """
    FeldSchema_*.json の fieldSchema.fields を Content Understanding fieldSchema 形式に変換する。

    対応ルール:
      - type: string  → type: string, method: generate
      - enum あり     → type: string, method: classify, enum: [...]
      - type: array   → type: array,  method: generate, items: {type: string}
    """
    source_fields: dict = schema.get("fieldSchema", {}).get("fields", {})
    cu_fields: dict = {}

    for name, fdef in source_fields.items():
        if name == "imagePath":
            continue

        ftype = fdef.get("type", "string")
        desc = fdef.get("description", name)
        enum_vals = fdef.get("enum")

        if enum_vals:
            cu_fields[name] = {
                "type": "string",
                "description": desc,
                "method": "classify",
                "enum": enum_vals,
            }
        elif ftype in ("array", "list"):
            cu_fields[name] = {
                "type": "array",
                "description": desc,
                "method": "generate",
                "items": {"type": "string"},
            }
        else:
            cu_fields[name] = {
                "type": "string",
                "description": desc,
                "method": "generate",
            }

    return {"fields": cu_fields}


# ---------------------------------------------------------------------------
# Analyzer 作成 / 更新
# ---------------------------------------------------------------------------

def put_analyzer(endpoint: str, token: str, analyzer_id: str, fieldschema: dict) -> None:
    """
    Content Understanding Analyzer を作成または更新する（PUT は idempotent）。
    201 Created の場合は Operation-Location をポーリングして作成完了を待つ。
    """
    url = f"{endpoint}/contentunderstanding/analyzers/{analyzer_id}?api-version={API_VERSION}"
    headers = {
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json",
    }
    body = {
        "description": "Video keyframe scene analyzer",
        "baseAnalyzerId": "prebuilt-image",
        "models": {
            "completion": "gpt-5.2"
        },
        "fieldSchema": fieldschema,
    }
    resp = requests.put(url, json=body, headers=headers, timeout=60)
    if resp.status_code not in (200, 201):
        raise RuntimeError(
            f"Analyzer の作成/更新に失敗しました: HTTP {resp.status_code}\n{resp.text}"
        )

    op_url = (
        resp.headers.get("Operation-Location")
        or resp.headers.get("operation-location")
    )
    if op_url:
        print(f"  Analyzer '{analyzer_id}' の作成完了を待機中...", end="", flush=True)
        _poll_analyzer_creation(op_url, token)
        print(" 完了")

    print(f"  Analyzer '{analyzer_id}' を登録しました。")


def _poll_analyzer_creation(op_url: str, token: str, poll_interval: float = 3.0) -> None:
    """Analyzer 作成の非同期ジョブが完了するまでポーリングする。"""
    headers = {"Authorization": f"Bearer {token}"}
    while True:
        time.sleep(poll_interval)
        resp = requests.get(op_url, headers=headers, timeout=30)
        resp.raise_for_status()
        data = resp.json()
        status = data.get("status", "").lower()
        if status == "succeeded":
            return
        if status in ("failed", "canceled"):
            raise RuntimeError(
                f"Analyzer の作成が失敗しました: {json.dumps(data, ensure_ascii=False)}"
            )
        print(".", end="", flush=True)


# ---------------------------------------------------------------------------
# 画像解析
# ---------------------------------------------------------------------------

def analyze_image(
    endpoint: str,
    token: str,
    analyzer_id: str,
    image_path: Path,
    poll_interval: float = 2.0,
) -> dict:
    """
    1 枚の画像を Content Understanding で解析し、フィールド辞書を返す。
    非同期ジョブ（202 Accepted）をポーリングして完了を待つ。
    """
    url = f"{endpoint}/contentunderstanding/analyzers/{analyzer_id}:analyze?api-version={API_VERSION}"
    operation_id = uuid.uuid4().hex

    # 画像を multipart/form-data で送信
    with image_path.open("rb") as f:
        mime = "image/jpeg" if image_path.suffix.lower() in (".jpg", ".jpeg") else "image/png"
        files = {"file": (image_path.name, f, mime)}
        headers = {
            "Authorization": f"Bearer {token}",
            "Operation-Id": operation_id,
        }
        resp = requests.post(url, headers=headers, files=files, timeout=60)

    if resp.status_code == 200:
        return _extract_fields(resp.json())

    if resp.status_code != 202:
        raise RuntimeError(
            f"解析リクエストが失敗しました: HTTP {resp.status_code}\n{resp.text}"
        )

    # 202: 非同期。Operation-Location をポーリング
    op_url = (
        resp.headers.get("Operation-Location")
        or resp.headers.get("operation-location")
    )
    if not op_url:
        raise RuntimeError("Operation-Location ヘッダーが見つかりません。")

    return _poll_operation(op_url, token, poll_interval)


def _poll_operation(op_url: str, token: str, poll_interval: float) -> dict:
    headers = {"Authorization": f"Bearer {token}"}
    while True:
        time.sleep(poll_interval)
        resp = requests.get(op_url, headers=headers, timeout=30)
        resp.raise_for_status()
        data = resp.json()
        status = data.get("status", "").lower()

        if status == "succeeded":
            return _extract_fields(data.get("result", {}))
        if status in ("failed", "canceled"):
            raise RuntimeError(f"解析が失敗しました: {json.dumps(data, ensure_ascii=False)}")
        # running / notStarted → 継続ポーリング


def _extract_fields(result: dict) -> dict:
    """
    Content Understanding レスポンスから fields を抽出し、
    {field_name: value} の辞書として返す。
    """
    extracted: dict = {}

    # result.contents[0].fields または result.fields を探索
    contents = result.get("contents") or [result]
    for content in contents:
        fields: dict = content.get("fields", {})
        for name, fval in fields.items():
            # CU のフィールド値は {valueString: "..."} / {valueArray: [...]} 形式
            if "valueString" in fval:
                extracted[name] = fval["valueString"]
            elif "valueArray" in fval:
                extracted[name] = [
                    (item.get("valueString") or item.get("content") or str(item))
                    for item in fval["valueArray"]
                ]
            elif "content" in fval:
                extracted[name] = fval["content"]
        break  # 最初の content のみ使用

    return extracted


# ---------------------------------------------------------------------------
# 出力: build_knowledge.py と互換の JSON 形式
# ---------------------------------------------------------------------------

def thumbnail_id_from_path(image_path: Path) -> str:
    """
    'KeyFrameThumbnail_<uuid>.jpg' → '<uuid>' を返す。
    形式が異なる場合はファイル名ステムをそのまま返す。
    """
    stem = image_path.stem  # e.g. KeyFrameThumbnail_cfe02bc1-b238-4732-89db-5e5d7d4305ff
    parts = stem.split("_", 1)
    return parts[1] if len(parts) == 2 else stem


def save_result(
    output_dir: Path,
    image_path: Path,
    fields: dict,
    analyzer_id: str,
) -> Path:
    """
    build_knowledge.py の load_cu_index が期待する形式で JSON を保存する。

    期待形式:
      {
        "imagePath": "KeyFrameThumbnail_<id>.jpg",
        "analysis": { field_name: value, ... },
        "usage": { "source": "content-understanding", "analyzer_id": "..." }
      }
    """
    thumbnail_id = thumbnail_id_from_path(image_path)
    out_path = output_dir / f"KeyFrameThumbnail_{thumbnail_id}.json"

    payload = {
        "imagePath": image_path.name,
        "analysis": fields,
        "usage": {
            "source": "content-understanding",
            "analyzer_id": analyzer_id,
        },
    }
    with out_path.open("w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)

    return out_path


# ---------------------------------------------------------------------------
# メイン
# ---------------------------------------------------------------------------

def parse_args() -> argparse.Namespace:
    ap = argparse.ArgumentParser(
        description="Azure Content Understanding でキーフレーム画像をバッチ解析する"
    )
    ap.add_argument(
        "--input-dir",
        default=str(Path(__file__).parent / "input" / "_KeyFrameThumbnail"),
        help="キーフレーム JPG が格納されたディレクトリ",
    )
    ap.add_argument(
        "--output-dir",
        default=str(Path(__file__).parent / "output"),
        help="解析結果 JSON を保存するディレクトリ",
    )
    ap.add_argument(
        "--schema-file",
        default=str(Path(__file__).parent / "FeldSchema_sample.json"),
        help="FeldSchema_*.json のパス",
    )
    ap.add_argument(
        "--endpoint",
        default=os.environ.get("AZURE_AI_ENDPOINT"),
        help="Azure AI Services エンドポイント (例: https://myresource.cognitiveservices.azure.com)",
    )
    ap.add_argument(
        "--analyzer-id",
        default="keyframe-scene-analyzer",
        help="Content Understanding Analyzer の ID",
    )
    ap.add_argument(
        "--poll-interval",
        type=float,
        default=2.0,
        help="非同期ジョブのポーリング間隔（秒）",
    )
    ap.add_argument(
        "--force",
        action="store_true",
        help="出力済みファイルを上書きして再処理する",
    )
    return ap.parse_args()


def main() -> None:
    args = parse_args()

    if not args.endpoint:
        print(
            "ERROR: --endpoint または環境変数 AZURE_AI_ENDPOINT を指定してください。",
            file=sys.stderr,
        )
        sys.exit(1)

    endpoint = args.endpoint.rstrip("/")
    input_dir = Path(args.input_dir)
    output_dir = Path(args.output_dir)

    print("=== Azure Content Understanding キーフレームバッチ解析 ===")
    print(f"入力ディレクトリ : {input_dir}")
    print(f"出力ディレクトリ : {output_dir}")
    print(f"スキーマファイル : {args.schema_file}")
    print(f"エンドポイント  : {endpoint}")
    print(f"Analyzer ID     : {args.analyzer_id}")
    print()

    # 入力チェック
    if not Path(args.schema_file).exists():
        print(f"ERROR: スキーマファイルが見つかりません: {args.schema_file}", file=sys.stderr)
        sys.exit(1)
    if not input_dir.exists():
        print(f"ERROR: 入力ディレクトリが見つかりません: {input_dir}", file=sys.stderr)
        sys.exit(1)

    images = sorted(
        f for f in input_dir.iterdir()
        if f.is_file() and f.suffix.lower() in SUPPORTED_EXTS
    )
    if not images:
        print(f"画像ファイルが見つかりません: {input_dir}")
        sys.exit(0)

    print(f"対象画像: {len(images)} 枚")
    output_dir.mkdir(parents=True, exist_ok=True)

    # [1] 認証
    print("\n[1/3] 認証中...")
    token = get_access_token()
    print("  OK")

    # [2] Analyzer 作成（初回のみ時間がかかる）
    print(f"\n[2/3] Analyzer を登録中 (ID: {args.analyzer_id})...")
    schema = load_schema(args.schema_file)
    fieldschema = build_cu_fieldschema(schema)
    put_analyzer(endpoint, token, args.analyzer_id, fieldschema)

    # [3] 画像を 1 枚ずつ解析
    print(f"\n[3/3] 画像を解析中...")
    success = 0
    skipped = 0
    failed = 0

    for idx, image_path in enumerate(images):
        thumbnail_id = thumbnail_id_from_path(image_path)
        out_path = output_dir / f"KeyFrameThumbnail_{thumbnail_id}.json"

        if out_path.exists() and not args.force:
            skipped += 1
            print(f"  [{idx+1}/{len(images)}] スキップ (既存): {image_path.name}")
            continue

        # 定期的にトークンを更新
        if idx > 0 and idx % TOKEN_REFRESH_EVERY == 0:
            token = get_access_token()

        print(f"  [{idx+1}/{len(images)}] 解析中: {image_path.name}", end="", flush=True)
        try:
            fields = analyze_image(
                endpoint, token, args.analyzer_id, image_path, args.poll_interval
            )
            saved = save_result(output_dir, image_path, fields, args.analyzer_id)
            print(f" → {saved.name}")
            success += 1
        except Exception as exc:
            print(f" → ERROR: {exc}")
            failed += 1

    # サマリー
    print()
    print("=" * 50)
    print(f"完了: 成功={success}  スキップ={skipped}  失敗={failed}")
    print(f"出力先: {output_dir}")

    if failed > 0:
        sys.exit(1)


if __name__ == "__main__":
    main()
