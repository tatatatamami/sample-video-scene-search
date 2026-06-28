#!/usr/bin/env python3
"""
Upload scene documents to Azure AI Search (documents only, no index schema update).
Computes embeddings using Azure AI Services, then uploads with mergeOrUpload.

Usage:
    python upload_scene_docs.py --file output/マリオ/mario_scene_docs_new.json

Requirements: pip install requests
"""
import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

import os

import requests

SEARCH_ENDPOINT = os.environ.get("AZURE_SEARCH_ENDPOINT", "https://<your-search-service>.search.windows.net")
SEARCH_API_KEY = os.environ.get("AZURE_SEARCH_ADMIN_KEY", "")
INDEX_NAME = os.environ.get("AZURE_SEARCH_INDEX_NAME", "video-scenes")
EMBEDDING_ENDPOINT = "https://ti-demo-ai-agents-swc-foundry.services.ai.azure.com"
EMBEDDING_DEPLOYMENT = "text-embedding-3-small"
EMBEDDING_DIMENSIONS = 1536
SEARCH_API_VERSION = "2024-07-01"
EMBEDDING_API_VERSION = "2024-02-01"

def get_token(resource: str) -> str:
    import shutil
    az = shutil.which("az") or "az"
    result = subprocess.run(
        [az, "account", "get-access-token", "--resource", resource,
         "--query", "accessToken", "-o", "tsv"],
        capture_output=True, text=True
    )
    token = result.stdout.strip()
    if not token:
        print(f"ERROR: az login required (resource: {resource})")
        sys.exit(1)
    return token

def compute_embeddings(texts: list[str], embed_headers: dict) -> list[list[float]]:
    url = (
        f"{EMBEDDING_ENDPOINT}/openai/deployments/{EMBEDDING_DEPLOYMENT}"
        f"/embeddings?api-version={EMBEDDING_API_VERSION}"
    )
    resp = requests.post(url, headers={**embed_headers, "Content-Type": "application/json"},
                         json={"input": texts})
    if not resp.ok:
        print(f"ERROR: Embedding failed {resp.status_code}: {resp.text[:300]}")
        sys.exit(1)
    data = resp.json()
    return [item["embedding"] for item in sorted(data["data"], key=lambda x: x["index"])]

def upload_batch(docs: list[dict], search_headers: dict) -> None:
    url = f"{SEARCH_ENDPOINT}/indexes/{INDEX_NAME}/docs/index?api-version={SEARCH_API_VERSION}"
    payload = {"value": [{"@search.action": "mergeOrUpload", **doc} for doc in docs]}
    resp = requests.post(url, headers={**search_headers, "Content-Type": "application/json"},
                         json=payload)
    if not resp.ok:
        print(f"ERROR: Upload failed {resp.status_code}: {resp.text[:300]}")
        sys.exit(1)
    result = resp.json()
    errors = [v for v in result.get("value", []) if not v.get("status")]
    if errors:
        print(f"  WARNING: {len(errors)} upload errors: {errors[0]}")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--file", "-f", required=True, help="Scene docs JSON file")
    ap.add_argument("--batch", type=int, default=16, help="Embedding batch size")
    args = ap.parse_args()

    file_path = Path(args.file)
    if not file_path.exists():
        print(f"ERROR: File not found: {file_path}")
        sys.exit(1)

    with open(file_path, encoding="utf-8") as f:
        docs = json.load(f)

    print(f"File: {file_path}")
    print(f"Documents: {len(docs)}")

    search_headers = {"api-key": SEARCH_API_KEY}
    embed_token = get_token("https://cognitiveservices.azure.com")
    embed_headers = {"Authorization": f"Bearer {embed_token}"}

    print(f"\n[1] Computing embeddings (batch={args.batch})...")
    texts = [doc.get("search_text", "") for doc in docs]
    all_vectors: list[list[float]] = []
    for i in range(0, len(texts), args.batch):
        batch = texts[i: i + args.batch]
        vecs = compute_embeddings(batch, embed_headers)
        all_vectors.extend(vecs)
        print(f"  {i + len(batch)}/{len(texts)} embeddings computed")
        if i + args.batch < len(texts):
            time.sleep(0.5)  # rate limiting

    for doc, vec in zip(docs, all_vectors):
        if len(vec) != EMBEDDING_DIMENSIONS:
            print(f"ERROR: Unexpected embedding dimension {len(vec)}, expected {EMBEDDING_DIMENSIONS}")
            sys.exit(1)
        doc["content_vector"] = vec

    print(f"\n[2] Uploading {len(docs)} documents to AI Search...")
    UPLOAD_BATCH = 100
    for i in range(0, len(docs), UPLOAD_BATCH):
        batch = docs[i: i + UPLOAD_BATCH]
        upload_batch(batch, search_headers)
        print(f"  Uploaded {i + len(batch)}/{len(docs)} documents")

    print(f"\nDONE: {len(docs)} documents uploaded to index '{INDEX_NAME}'")

if __name__ == "__main__":
    main()
