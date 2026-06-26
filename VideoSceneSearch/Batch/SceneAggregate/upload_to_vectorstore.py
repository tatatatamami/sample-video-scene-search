#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ナレッジJSONファイルをAzure AI Foundryのベクターストアにアップロードするスクリプト。

Usage:
    python upload_to_vectorstore.py --file output/マリオ/mario_keyframe_docs.json --vector-store-id vs_XXXX
    python upload_to_vectorstore.py --file output/マリオ/mario_keyframe_docs.json --vector-store-id vs_WD5J3B3J4uiDsmbTS0P0N9hM
"""

import argparse
import subprocess
import sys
import time

import requests


def get_access_token() -> str:
    result = subprocess.run(
        ["az", "account", "get-access-token", "--resource", "https://ai.azure.com", "--query", "accessToken", "-o", "tsv"],
        capture_output=True, text=True, shell=True
    )
    token = result.stdout.strip()
    if not token:
        print("ERROR: az login が必要です。`az login` を実行してください。")
        sys.exit(1)
    return token


def upload_file(base_url: str, headers: dict, file_path: str) -> str:
    print(f"ファイルをアップロード中: {file_path}")
    with open(file_path, "rb") as f:
        resp = requests.post(
            f"{base_url}/files",
            headers=headers,
            files={"file": (file_path.split("\\")[-1].split("/")[-1], f, "application/json")},
            data={"purpose": "assistants"},
        )
    if not resp.ok:
        print(f"ERROR: ファイルアップロード失敗 {resp.status_code}: {resp.text}")
        sys.exit(1)

    file_id = resp.json()["id"]
    print(f"  → ファイルID: {file_id}")
    return file_id


def add_file_to_vector_store(base_url: str, headers: dict, vector_store_id: str, file_id: str) -> None:
    print(f"ベクターストア {vector_store_id} にファイルを追加中...")
    resp = requests.post(
        f"{base_url}/vector_stores/{vector_store_id}/files",
        headers={**headers, "Content-Type": "application/json"},
        json={"file_id": file_id},
    )
    if not resp.ok:
        print(f"ERROR: ベクターストアへの追加失敗 {resp.status_code}: {resp.text}")
        sys.exit(1)
    print(f"  → 追加リクエスト送信完了")


def wait_for_processing(base_url: str, headers: dict, vector_store_id: str, file_id: str, timeout: int = 120) -> None:
    print("インデックス処理を待機中...", end="", flush=True)
    start = time.time()
    while time.time() - start < timeout:
        resp = requests.get(
            f"{base_url}/vector_stores/{vector_store_id}/files/{file_id}",
            headers=headers,
        )
        if resp.ok:
            status = resp.json().get("status", "")
            if status == "completed":
                print(f" 完了！")
                return
            elif status == "failed":
                print(f" 失敗: {resp.json()}")
                sys.exit(1)
        print(".", end="", flush=True)
        time.sleep(3)
    print(f"\nWARNING: タイムアウト ({timeout}秒). 処理は継続中の可能性があります。")


def list_vector_store_files(base_url: str, headers: dict, vector_store_id: str) -> None:
    resp = requests.get(f"{base_url}/vector_stores/{vector_store_id}/files", headers=headers)
    if resp.ok:
        files = resp.json().get("data", [])
        print(f"\n現在のベクターストアファイル一覧 ({len(files)}件):")
        for f in files:
            print(f"  - {f['id']}  status={f.get('status','?')}")
    else:
        print(f"ファイル一覧取得失敗: {resp.status_code}")


def main() -> None:
    ap = argparse.ArgumentParser(description="ナレッジJSONをベクターストアにアップロード")
    ap.add_argument("--file",             "-f", required=True, help="アップロードするJSONファイルパス")
    ap.add_argument("--vector-store-id",  "-v", default="vs_WD5J3B3J4uiDsmbTS0P0N9hM", help="ベクターストアID")
    ap.add_argument("--base-url",         "-b", default="https://se3-tamamiihori-1-1-resource.services.ai.azure.com/api/projects/se3-tamamiihori-1-1/openai/v1")
    args = ap.parse_args()

    token = get_access_token()
    headers = {"Authorization": f"Bearer {token}"}

    print(f"ベースURL: {args.base_url}")
    print(f"ベクターストアID: {args.vector_store_id}")

    # 現在のファイル一覧を表示
    list_vector_store_files(args.base_url, headers, args.vector_store_id)

    # ファイルをアップロード
    file_id = upload_file(args.base_url, headers, args.file)

    # ベクターストアに追加
    add_file_to_vector_store(args.base_url, headers, args.vector_store_id, file_id)

    # 処理完了を待機
    wait_for_processing(args.base_url, headers, args.vector_store_id, file_id)

    # 最終ファイル一覧を表示
    list_vector_store_files(args.base_url, headers, args.vector_store_id)
    print("\n✅ アップロード完了！検索が利用可能になりました。")


if __name__ == "__main__":
    main()
