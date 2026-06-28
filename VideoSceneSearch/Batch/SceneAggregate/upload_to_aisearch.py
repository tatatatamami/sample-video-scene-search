#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ナレッジJSONファイルを Azure AI Search インデックスにアップロードするスクリプト。
ドキュメントごとに Azure OpenAI でベクトルを計算し、ハイブリッド検索（テキスト＋ベクトル）
に対応したインデックスを構築します。

Usage:
    python upload_to_aisearch.py \\
        --file             output/マイクラ/keyframe_docs.json \\
        --search-endpoint  https://<name>.search.windows.net \\
        --index-name       video-scenes \\
        --embedding-endpoint   https://<resource>.services.ai.azure.com \\
        --embedding-deployment text-embedding-3-small

    # ベクトル計算をスキップしてキーワード検索のみの場合
    python upload_to_aisearch.py \\
        --file             output/マイクラ/keyframe_docs.json \\
        --search-endpoint  https://<name>.search.windows.net \\
        --index-name       video-scenes \\
        --skip-vectorization
"""

import argparse
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

import requests

SEARCH_API_VERSION = "2024-11-01"      # Azure AI Search 最新安定版 GA
EMBEDDING_API_VERSION = "2024-10-21"  # Azure OpenAI / AI Services 最新安定版 GA


_AZ_CMD: str = shutil.which("az") or "az"


def get_token(resource: str) -> str:
    """az CLI からアクセストークンを取得する。"""
    result = subprocess.run(
        [_AZ_CMD, "account", "get-access-token", "--resource", resource,
         "--query", "accessToken", "-o", "tsv"],
        capture_output=True, text=True, shell=False
    )
    token = result.stdout.strip()
    if not token:
        print(f"ERROR: az login が必要です。`az login` を実行してください。(resource: {resource})")
        sys.exit(1)
    return token


def create_or_update_index(
    search_endpoint: str,
    search_headers: dict,
    index_name: str,
    dims: int,
) -> None:
    """Azure AI Search インデックスを冪等に作成/更新する。"""
    index_def = {
        "name": index_name,
        "fields": [
            # --- キー・フィルタフィールド ---
            {"name": "id",           "type": "Edm.String", "key": True,  "filterable": True},
            {"name": "documentType", "type": "Edm.String", "key": False, "filterable": True, "facetable": True},
            {"name": "videoId",      "type": "Edm.String", "key": False, "filterable": True, "facetable": True},
            {"name": "sceneId",      "type": "Edm.String", "key": False, "filterable": True},
            {"name": "keyFrameId",   "type": "Edm.String", "key": False, "filterable": True},
            {"name": "imagePath",    "type": "Edm.String", "key": False, "filterable": False},
            # --- 時刻フィールド ---
            {"name": "timeMs",       "type": "Edm.Int32",  "key": False, "filterable": True, "sortable": True},
            {"name": "beginMs",      "type": "Edm.Int32",  "key": False, "filterable": True, "sortable": True},
            {"name": "endMs",        "type": "Edm.Int32",  "key": False, "filterable": True, "sortable": True},
            # --- scene 固有フィールド ---
            {"name": "representativeImagePath", "type": "Edm.String", "key": False, "retrievable": True},
            {
                "name": "scene_summary",
                "type": "Edm.String",
                "key": False,
                "searchable": True,
                "retrievable": True,
                "analyzer": "ja.microsoft",
            },
            # --- 人物フィールド (構造化フィルター対応) ---
            {
                "name": "scenePeople",
                "type": "Collection(Edm.String)",
                "key": False,
                "searchable": True,
                "filterable": True,
                "retrievable": True,
            },
            {
                "name": "visiblePeople",
                "type": "Collection(Edm.String)",
                "key": False,
                "searchable": True,
                "filterable": True,
                "retrievable": True,
            },
            # --- 検索テキスト ---
            {
                "name": "search_text",
                "type": "Edm.String",
                "key": False,
                "searchable": True,
                "analyzer": "ja.microsoft",
            },
            # --- ベクトルフィールド（skip-vectorization 時は未使用） ---
            {
                "name": "content_vector",
                "type": "Collection(Edm.Single)",
                "key": False,
                "searchable": True,
                "dimensions": dims,
                "vectorSearchProfile": "hnsw-profile",
            },
        ],
        "vectorSearch": {
            "algorithms": [
                {
                    "name": "hnsw-algo",
                    "kind": "hnsw",
                    "hnswParameters": {
                        "m": 4,
                        "efConstruction": 400,
                        "efSearch": 500,
                        "metric": "cosine",
                    },
                }
            ],
            "profiles": [
                {"name": "hnsw-profile", "algorithmConfigurationName": "hnsw-algo"}
            ],
        },
        "semantic": {
            "configurations": [
                {
                    "name": "semantic-config",
                    "prioritizedFields": {
                        "prioritizedContentFields": [{"fieldName": "search_text"}]
                    },
                }
            ]
        },
    }

    url = f"{search_endpoint}/indexes/{index_name}?api-version={SEARCH_API_VERSION}"
    resp = requests.put(
        url,
        headers={**search_headers, "Content-Type": "application/json"},
        json=index_def,
    )
    if resp.status_code in (200, 201, 204):
        action = "更新" if resp.status_code in (200, 204) else "作成"
        print(f"  → インデックス '{index_name}' を{action}しました")
    else:
        print(f"ERROR: インデックス作成/更新失敗 {resp.status_code}: {resp.text[:600]}")
        sys.exit(1)


def compute_embeddings(
    embedding_endpoint: str,
    embedding_deployment: str,
    texts: list[str],
    embed_headers: dict,
) -> list[list[float]]:
    """テキストリストのベクトルをバッチ計算する。"""
    url = (
        f"{embedding_endpoint}/openai/deployments/{embedding_deployment}"
        f"/embeddings?api-version={EMBEDDING_API_VERSION}"
    )
    resp = requests.post(
        url,
        headers={**embed_headers, "Content-Type": "application/json"},
        json={"input": texts},
    )
    if not resp.ok:
        print(f"ERROR: Embedding 計算失敗 {resp.status_code}: {resp.text[:600]}")
        sys.exit(1)
    data = resp.json()
    return [item["embedding"] for item in sorted(data["data"], key=lambda x: x["index"])]


def upload_documents(
    search_endpoint: str,
    search_headers: dict,
    index_name: str,
    docs: list[dict],
) -> None:
    """ドキュメントを Azure AI Search に mergeOrUpload でバッチ送信する。"""
    url = f"{search_endpoint}/indexes/{index_name}/docs/index?api-version={SEARCH_API_VERSION}"
    payload = {
        "value": [{"@search.action": "mergeOrUpload", **doc} for doc in docs]
    }
    resp = requests.post(
        url,
        headers={**search_headers, "Content-Type": "application/json"},
        json=payload,
    )
    if not resp.ok:
        print(f"ERROR: アップロード失敗 {resp.status_code}: {resp.text[:600]}")
        sys.exit(1)
    results = resp.json().get("value", [])
    failed = [r for r in results if not r.get("status", False)]
    if failed:
        print(f"  ⚠ {len(failed)} 件で登録エラー:")
        for f in failed[:5]:
            print(f"    - {f.get('key', '?')}: {f.get('errorMessage', '?')}")
        raise RuntimeError(
            f"{len(failed)} 件のドキュメントが登録に失敗しました。スキーマ不一致またはAI Searchのエラーを確認してください。"
        )


def list_index_stats(
    search_endpoint: str,
    search_headers: dict,
    index_name: str,
) -> None:
    """インデックスの統計情報を表示する。"""
    url = f"{search_endpoint}/indexes/{index_name}/stats?api-version={SEARCH_API_VERSION}"
    resp = requests.get(url, headers=search_headers)
    if resp.ok:
        stats = resp.json()
        doc_count = stats.get("documentCount", "?")
        storage = stats.get("storageSize", 0)
        print(f"  インデックス統計: ドキュメント数={doc_count}  ストレージ={storage:,} bytes")
    else:
        print(f"  統計取得失敗: {resp.status_code}")


def main() -> None:
    ap = argparse.ArgumentParser(
        description="ナレッジJSONを Azure AI Search インデックスにアップロードする"
    )
    ap.add_argument("--file",                 "-f", required=True,
                    help="アップロードするJSONファイルパス")
    ap.add_argument("--search-endpoint",      "-s", required=True,
                    help="Azure AI Search エンドポイント (例: https://<name>.search.windows.net)")
    ap.add_argument("--index-name",           "-i", required=True,
                    help="インデックス名 (例: video-scenes)")
    ap.add_argument("--embedding-endpoint",   "-e", default="",
                    help="Azure OpenAI エンドポイント (例: https://<resource>.services.ai.azure.com)")
    ap.add_argument("--embedding-deployment", "-d", default="text-embedding-3-small",
                    help="Embedding デプロイメント名 (デフォルト: text-embedding-3-small)")
    ap.add_argument("--dimensions",           type=int, default=1536,
                    help="ベクトル次元数 (デフォルト: 1536 = text-embedding-3-small)")
    ap.add_argument("--upload-batch",         type=int, default=100,
                    help="アップロードバッチサイズ (デフォルト: 100)")
    ap.add_argument("--embed-batch",          type=int, default=16,
                    help="Embedding バッチサイズ (デフォルト: 16)")
    ap.add_argument("--skip-vectorization",   action="store_true",
                    help="Embedding 計算をスキップしてキーワード検索のみにする")
    ap.add_argument("--search-api-key",       default="",
                    help="Azure AI Search API キー (省略時は Azure CLI の RBAC トークンを使用)"
                         " — セキュリティ上、RBAC 認証（用引数なし）を推奨。"
                         " CLI 引数で渡すとシェル履歴・プロセスリストに残るため、"
                         " 本番環境では環境変数等で渡すこと。")
    args = ap.parse_args()

    if not args.skip_vectorization and not args.embedding_endpoint:
        print("ERROR: --embedding-endpoint は --skip-vectorization なしで必須です。")
        ap.print_usage()
        sys.exit(1)

    # ---- 認証 ----
    if args.search_api_key:
        search_headers = {"api-key": args.search_api_key}
    else:
        search_token = get_token("https://search.azure.com")
        search_headers = {"Authorization": f"Bearer {search_token}"}

    if not args.skip_vectorization:
        embed_token = get_token("https://cognitiveservices.azure.com")
        embed_headers = {"Authorization": f"Bearer {embed_token}"}
    else:
        embed_headers = {}

    # ---- ファイル読み込み ----
    file_path = Path(args.file)
    if not file_path.exists():
        print(f"ERROR: ファイルが見つかりません: {file_path}")
        sys.exit(1)

    with open(file_path, encoding="utf-8") as f:
        docs: list[dict] = json.load(f)

    if not isinstance(docs, list):
        print("ERROR: JSONファイルはリスト形式である必要があります")
        sys.exit(1)

    print(f"\n=== Azure AI Search アップロード ===")
    print(f"  ファイル         : {file_path}")
    print(f"  ドキュメント数   : {len(docs)} 件")
    print(f"  Search エンドポイント: {args.search_endpoint}")
    print(f"  インデックス     : {args.index_name}")
    if not args.skip_vectorization:
        print(f"  Embedding モデル : {args.embedding_deployment} ({args.dimensions} 次元)")
    else:
        print(f"  Embedding       : スキップ（キーワード検索のみ）")

    # ---- [1/3] インデックス作成/更新 ----
    print("\n[1/3] インデックスを作成/更新中...")
    create_or_update_index(args.search_endpoint, search_headers, args.index_name, args.dimensions)

    # ---- [2/3] Embedding 計算 ----
    if not args.skip_vectorization:
        print(f"\n[2/3] Embedding を計算中 (バッチサイズ: {args.embed_batch})...")
        texts = [doc.get("search_text", "") for doc in docs]
        all_vectors: list[list[float]] = []
        for i in range(0, len(texts), args.embed_batch):
            batch = texts[i: i + args.embed_batch]
            vecs = compute_embeddings(
                args.embedding_endpoint, args.embedding_deployment, batch, embed_headers
            )
            # バッチ件数と次元数を検証する
            if len(vecs) != len(batch):
                raise ValueError(
                    f"Embedding バッチ件数不一致: 期待 {len(batch)} 件, 取得 {len(vecs)} 件"
                )
            if any(len(v) != args.dimensions for v in vecs):
                bad_dims = [len(v) for v in vecs if len(v) != args.dimensions][:3]
                raise ValueError(
                    f"Embedding 次元数不一致: 期待 {args.dimensions} 次元, 取得 {bad_dims} 次元"
                )
            all_vectors.extend(vecs)
            done = min(i + args.embed_batch, len(texts))
            print(f"  {done}/{len(texts)} 件処理済み", end="\r", flush=True)
        print(f"  {len(all_vectors)} 件の Embedding を計算しました          ")
        if len(all_vectors) != len(docs):
            raise ValueError(
                f"Embedding 総件数不一致: ドキュメント {len(docs)} 件に対し Embedding {len(all_vectors)} 件"
            )
        upload_docs = [dict(doc, content_vector=vec) for doc, vec in zip(docs, all_vectors)]
    else:
        print(f"\n[2/3] Embedding 計算をスキップ")
        upload_docs = list(docs)

    # ---- [3/3] ドキュメントアップロード ----
    print(f"\n[3/3] ドキュメントをアップロード中 (バッチサイズ: {args.upload_batch})...")
    total = 0
    for i in range(0, len(upload_docs), args.upload_batch):
        batch = upload_docs[i: i + args.upload_batch]
        try:
            upload_documents(args.search_endpoint, search_headers, args.index_name, batch)
        except RuntimeError as exc:
            print(f"\nERROR: {exc}")
            sys.exit(1)
        total += len(batch)
        print(f"  {total}/{len(upload_docs)} 件アップロード済み")

    # 少し待ってから統計確認
    time.sleep(2)
    list_index_stats(args.search_endpoint, search_headers, args.index_name)

    print(f"\n✓ 完了: {len(upload_docs)} 件のドキュメントをインデックス '{args.index_name}' にアップロードしました")


if __name__ == "__main__":
    main()
