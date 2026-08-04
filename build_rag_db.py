#!/usr/bin/env python3
"""
Pre-build script: processes PDFs via the Render parsing service,
embeds all chunks via OpenAI, and outputs the VectorDatabase JSON.

Run once on PC, then delete PDFs from StreamingAssets.
The resulting JSON ships with the app instead.

Usage:
    python build_rag_db.py
"""

import json
import os
import sys
import time
import datetime
import csv
import re
import uuid
import requests

# ── Config ──────────────────────────────────────────────────────────────
PDF_DIR = os.path.join(os.path.dirname(__file__),
                       "Assets", "StreamingAssets", "MedicalKnowledge")

PDF_FILES = [
    "Buch_Deutsch_fuer_Aerztinnen_und_Aerzte.pdf",
    "Buch_Fachsprachprüfung_Erfolgreich_Bestehen.pdf",
]

PARSE_SERVICE_URL = "https://ragsystempdf.onrender.com/api/v1/parse-pdf"
OPENAI_EMBEDDING_URL = "https://api.openai.com/v1/embeddings"
EMBEDDING_MODEL = "text-embedding-3-small"
EMBEDDING_DIMENSIONS = 1536
BATCH_SIZE = 100  # OpenAI allows up to 2048 inputs per call

CATEGORY = "Fachsprachprüfung"
SCENARIO_TAGS = "fsp,medical_german"

OUTPUT_PATH = os.path.join(os.path.dirname(__file__),
                           "Assets", "StreamingAssets", "PrebuiltRAG",
                           "medical_knowledge_db.json")

CASE_CSV_PATH = os.path.join(os.path.dirname(__file__),
                             "Assets", "Prompts", "prompt_keys_runtime_only.csv")

GENERATED_CASES_DIR = os.path.join(os.path.dirname(__file__),
                                   "Assets", "StreamingAssets", "MedicalKnowledge", "GeneratedCases")

CASE_CHUNK_MAX_CHARS = 1600
CASE_CHUNK_OVERLAP_CHARS = 180

# ── API Key ─────────────────────────────────────────────────────────────
OPENAI_API_KEY = os.environ.get("OPENAI_API_KEY", "").strip()
if not OPENAI_API_KEY:
    print("ERROR: Set the OPENAI_API_KEY environment variable before running this script.")
    sys.exit(1)


def parse_pdf(filepath: str) -> list[dict]:
    """Upload PDF to Render service, return list of chunk dicts."""
    filename = os.path.basename(filepath)
    file_size_mb = os.path.getsize(filepath) / (1024 * 1024)
    print(f"  Uploading {filename} ({file_size_mb:.1f} MB) to parse service...")

    with open(filepath, "rb") as f:
        resp = requests.post(
            PARSE_SERVICE_URL,
            files={"file": (filename, f, "application/pdf")},
            data={"category": CATEGORY, "scenario_tags": SCENARIO_TAGS},
            timeout=600,
        )
    resp.raise_for_status()
    data = resp.json()
    chunks = data.get("chunks", [])
    print(f"  → {len(chunks)} chunks parsed")
    return chunks


def sanitize_filename(value: str) -> str:
    raw = (value or "").strip().lower()
    if not raw:
        return "unknown"
    raw = re.sub(r"[^a-z0-9_-]+", "_", raw)
    return raw.strip("_") or "unknown"


def estimate_token_count(text: str) -> int:
    if not text:
        return 0
    return max(1, int(round(len(text) / 4.0)))


def split_text_with_overlap(text: str, max_chars: int, overlap_chars: int) -> list[str]:
    content = (text or "").strip()
    if not content:
        return []

    if len(content) <= max_chars:
        return [content]

    chunks: list[str] = []
    start = 0
    n = len(content)

    while start < n:
        end = min(start + max_chars, n)
        if end < n:
            # Prefer to break at paragraph/sentence boundaries for cleaner chunks.
            paragraph_break = content.rfind("\n\n", start, end)
            sentence_break = content.rfind(". ", start, end)
            line_break = content.rfind("\n", start, end)
            split_at = max(paragraph_break, sentence_break, line_break)
            if split_at > start + int(max_chars * 0.6):
                end = split_at + (2 if split_at == sentence_break else 1)

        piece = content[start:end].strip()
        if piece:
            chunks.append(piece)

        if end >= n:
            break

        start = max(0, end - overlap_chars)

    return chunks


def extract_case_chunks_from_csv(csv_path: str) -> list[dict]:
    if not os.path.isfile(csv_path):
        print(f"WARNING: case CSV not found at {csv_path}; skipping case ingestion")
        return []

    os.makedirs(GENERATED_CASES_DIR, exist_ok=True)

    all_case_chunks: list[dict] = []
    generated_case_files = 0

    with open(csv_path, "r", encoding="utf-8", newline="") as f:
        reader = csv.DictReader(f)
        for row in reader:
            if not row:
                continue

            cases_json_raw = (row.get("cases_json") or "").strip()
            if not cases_json_raw:
                continue

            scenario_id = (row.get("scenario_id") or "default").strip() or "default"
            scenario_name = (row.get("scenario_name") or "").strip()
            theme = (row.get("theme") or "").strip()
            rag_tags_raw = (row.get("rag_tags") or "").strip()

            try:
                cases_payload = json.loads(cases_json_raw)
            except json.JSONDecodeError as ex:
                print(f"WARNING: invalid cases_json for scenario '{scenario_id}': {ex}")
                continue

            cases = cases_payload.get("cases", []) if isinstance(cases_payload, dict) else []
            if not isinstance(cases, list) or not cases:
                continue

            for idx, case in enumerate(cases):
                if not isinstance(case, dict):
                    continue

                case_id = str(case.get("case_id") or f"case_{idx + 1}").strip()
                role = str(case.get("role") or "").strip().lower()
                title = str(case.get("title") or case_id).strip()
                text = str(case.get("text") or "").strip()

                if not text:
                    continue

                # Generate a human-readable case text file (for traceability and manual review).
                file_stem = f"{sanitize_filename(scenario_id)}__{sanitize_filename(case_id)}"
                out_path = os.path.join(GENERATED_CASES_DIR, file_stem + ".txt")
                rendered = (
                    f"SCENARIO_ID: {scenario_id}\n"
                    f"SCENARIO_NAME: {scenario_name}\n"
                    f"CASE_ID: {case_id}\n"
                    f"ROLE: {role}\n"
                    f"TITLE: {title}\n"
                    f"THEME: {theme}\n"
                    f"RAG_TAGS: {rag_tags_raw}\n"
                    f"\n---\n\n"
                    f"{text}\n"
                )
                with open(out_path, "w", encoding="utf-8") as cf:
                    cf.write(rendered)
                generated_case_files += 1

                scenario_tags = []
                for part in rag_tags_raw.replace(";", " ").replace(",", " ").split():
                    tag = part.strip().lower()
                    if tag and tag not in scenario_tags:
                        scenario_tags.append(tag)

                extra_tags = [
                    f"scenario:{scenario_id.lower()}",
                    f"case:{case_id.lower()}",
                    f"role:{role or 'unknown'}",
                    "source:csv_case",
                ]
                if theme:
                    extra_tags.append(f"theme:{theme.lower()}")
                for t in extra_tags:
                    if t not in scenario_tags:
                        scenario_tags.append(t)

                parts = split_text_with_overlap(text, CASE_CHUNK_MAX_CHARS, CASE_CHUNK_OVERLAP_CHARS)
                for p_i, piece in enumerate(parts):
                    all_case_chunks.append(
                        {
                            "id": str(uuid.uuid4()),
                            "document_id": f"csv_case::{scenario_id}::{case_id}",
                            "category": "CaseLibrary",
                            "content": piece,
                            "section_title": f"{title} (Teil {p_i + 1}/{len(parts)})",
                            "clinical_context": scenario_name or scenario_id,
                            "scenario_tags": scenario_tags,
                            "token_count": estimate_token_count(piece),
                            "source_file": out_path,
                            "created_at": datetime.datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%SZ"),
                        }
                    )

    print(f"CSV case ingestion: generated {generated_case_files} case text files, {len(all_case_chunks)} case chunks")
    return all_case_chunks


def embed_texts(texts: list[str]) -> list[list[float]]:
    """Call OpenAI embeddings API in batches, return list of embedding vectors."""
    all_embeddings: list[list[float]] = []
    headers = {
        "Authorization": f"Bearer {OPENAI_API_KEY}",
        "Content-Type": "application/json",
    }

    for start in range(0, len(texts), BATCH_SIZE):
        batch = texts[start: start + BATCH_SIZE]
        print(f"  Embedding batch {start // BATCH_SIZE + 1} "
              f"({start+1}–{start+len(batch)} of {len(texts)})...")

        payload = {
            "model": EMBEDDING_MODEL,
            "input": batch,
            "dimensions": EMBEDDING_DIMENSIONS,
        }

        for attempt in range(3):
            resp = requests.post(OPENAI_EMBEDDING_URL, headers=headers,
                                 json=payload, timeout=120)
            if resp.status_code == 429:
                wait = 2 ** (attempt + 1)
                print(f"    Rate limited, waiting {wait}s...")
                time.sleep(wait)
                continue
            resp.raise_for_status()
            break

        result = resp.json()
        batch_embeddings = [item["embedding"] for item in result["data"]]
        all_embeddings.extend(batch_embeddings)

    return all_embeddings


def main():
    print("=" * 60)
    print("RAG Database Builder — Pre-build PDF → Embeddings")
    print("=" * 60)

    all_chunks = []

    # Step 1: Parse all PDFs
    for pdf_name in PDF_FILES:
        pdf_path = os.path.join(PDF_DIR, pdf_name)
        if not os.path.isfile(pdf_path):
            print(f"WARNING: {pdf_path} not found, skipping")
            continue
        chunks = parse_pdf(pdf_path)
        all_chunks.extend(chunks)

    # Step 1b: Extract cases from prompt CSV, write them as text files, and add as chunks.
    case_chunks = extract_case_chunks_from_csv(CASE_CSV_PATH)
    all_chunks.extend(case_chunks)

    if not all_chunks:
        print("ERROR: No chunks parsed from any PDF. Aborting.")
        sys.exit(1)

    print(f"\nTotal chunks: {len(all_chunks)} (including {len(case_chunks)} from CSV cases)")

    # Step 2: Embed all chunks
    print("\nEmbedding all chunks via OpenAI...")
    texts = [c["content"] for c in all_chunks]
    embeddings = embed_texts(texts)

    if len(embeddings) != len(all_chunks):
        print(f"ERROR: Got {len(embeddings)} embeddings for {len(all_chunks)} chunks")
        sys.exit(1)

    # Step 3: Assign embeddings back to chunks
    for chunk, emb in zip(all_chunks, embeddings):
        chunk["embedding"] = emb

    # Step 4: Write DB JSON in the exact format VectorDatabase.Load() expects
    db_data = {
        "version": 1,
        "created_at": datetime.datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%SZ"),
        "chunk_count": len(all_chunks),
        "chunks": all_chunks,
    }

    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    with open(OUTPUT_PATH, "w", encoding="utf-8") as f:
        json.dump(db_data, f, ensure_ascii=False, indent=2)

    file_size_mb = os.path.getsize(OUTPUT_PATH) / (1024 * 1024)
    print(f"\n{'=' * 60}")
    print(f"SUCCESS: {len(all_chunks)} chunks with embeddings saved")
    print(f"Output:  {OUTPUT_PATH}")
    print(f"Size:    {file_size_mb:.1f} MB")
    print(f"{'=' * 60}")
    print(f"\nNext steps:")
    print(f"  1. Delete PDFs from Assets/StreamingAssets/MedicalKnowledge/")
    print(f"  2. The pre-built DB in Assets/StreamingAssets/PrebuiltRAG/ ships with the app")
    print(f"  3. Build your app — it will be ~{file_size_mb:.0f} MB instead of ~269 MB smaller")


if __name__ == "__main__":
    main()
