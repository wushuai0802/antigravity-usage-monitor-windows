#!/usr/bin/env python3
"""Optional Antigravity CLI statusLine bridge to sync official dual-pool quota.

Reads the official `agy` CLI statusLine JSON payload from stdin, extracts only
sanitized dual-pool quota (Gemini pool + Claude/third-party pool) and plan tier
(never stores email or credentials), writes `quota.slot{0,1}.json` for the
Windows desktop overlay, and prints a compact terminal status line on stdout.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import sys
from pathlib import Path
from typing import Any

from collect import (
    SCHEMA_VERSION,
    detect_default_state_dir,
    format_iso_utc,
    parse_iso_utc,
    write_dual_slot,
)


def classify_quota_bucket(bucket_id: str) -> str | None:
    low = bucket_id.strip().lower()
    if not low:
        return None
    if any(k in low for k in ("claude", "anthropic", "opus", "sonnet", "haiku", "gpt", "third")):
        return "claude"
    if any(k in low for k in ("gemini", "google", "flash", "neon", "argon", "carbon", "hydrogen", "pro")):
        return "gemini"
    return None


def extract_remaining_pct(entry: dict[str, Any]) -> float | None:
    frac = entry.get("remaining_fraction")
    if isinstance(frac, (int, float)):
        val = float(frac)
        if 0.0 <= val <= 1.0:
            return round(val * 100.0, 2)
        if 1.0 < val <= 100.0:
            return round(val, 2)
    pct = entry.get("remaining_percentage")
    if isinstance(pct, (int, float)):
        return round(max(0.0, min(100.0, float(pct))), 2)
    return None


def extract_reset_iso(entry: dict[str, Any], now_utc: dt.datetime) -> str | None:
    reset_time = entry.get("reset_time")
    if isinstance(reset_time, str):
        parsed = parse_iso_utc(reset_time)
        if parsed is not None:
            return format_iso_utc(parsed)
    reset_secs = entry.get("reset_in_seconds")
    if isinstance(reset_secs, (int, float)) and reset_secs >= 0:
        return format_iso_utc(now_utc + dt.timedelta(seconds=float(reset_secs)))
    return None


def parse_statusline_quota(payload: dict[str, Any], now_utc: dt.datetime | None = None) -> dict[str, Any] | None:
    now = now_utc or dt.datetime.now(dt.timezone.utc)
    quota_obj = payload.get("quota")
    if not isinstance(quota_obj, dict) or not quota_obj:
        return None

    pools: dict[str, dict[str, Any]] = {}
    for bucket_id, raw_entry in quota_obj.items():
        if not isinstance(raw_entry, dict):
            continue
        pool = classify_quota_bucket(str(bucket_id))
        if pool is None:
            continue
        pct = extract_remaining_pct(raw_entry)
        if pct is None:
            continue
        reset_at = extract_reset_iso(raw_entry, now)
        # Conservative aggregation: keep the lowest remaining percentage within a pool
        existing = pools.get(pool)
        if existing is None or pct < existing["remainingPct"]:
            pools[pool] = {
                "remainingPct": pct,
                "resetAt": reset_at,
                "bucketId": str(bucket_id),
            }

    if not pools:
        return None

    doc: dict[str, Any] = {
        "schemaVersion": SCHEMA_VERSION,
        "updatedAt": format_iso_utc(now),
        "source": "agy-statusline",
        "pools": pools,
    }
    plan_tier = payload.get("plan_tier")
    if isinstance(plan_tier, str) and plan_tier.strip():
        doc["planTier"] = plan_tier.strip()
    return doc


def render_cli_line(payload: dict[str, Any], quota_doc: dict[str, Any] | None) -> str:
    parts: list[str] = []
    model = payload.get("model")
    if isinstance(model, dict):
        label = model.get("display_name") or model.get("id")
        if isinstance(label, str) and label.strip():
            parts.append(label.strip())
    ctx = payload.get("context_window")
    if isinstance(ctx, dict) and isinstance(ctx.get("used_percentage"), (int, float)):
        parts.append(f"ctx {ctx['used_percentage']:.0f}%")
    if quota_doc and isinstance(quota_doc.get("pools"), dict):
        q_parts: list[str] = []
        g = quota_doc["pools"].get("gemini")
        c = quota_doc["pools"].get("claude")
        if g:
            q_parts.append(f"G {g['remainingPct']:.0f}%")
        if c:
            q_parts.append(f"C {c['remainingPct']:.0f}%")
        if q_parts:
            parts.append(" · ".join(q_parts))
    return " | ".join(parts) if parts else "Antigravity"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Antigravity CLI statusLine quota bridge")
    parser.add_argument("--state-dir", help="Override state output directory")
    parser.add_argument("--now", help="Override current UTC timestamp (ISO8601) for testing")
    args = parser.parse_args(argv)

    raw = sys.stdin.read()
    try:
        payload = json.loads(raw) if raw.strip() else {}
    except ValueError:
        payload = {}
    if not isinstance(payload, dict):
        payload = {}

    now_utc = parse_iso_utc(args.now) if args.now else None
    quota_doc = parse_statusline_quota(payload, now_utc=now_utc)
    if quota_doc is not None:
        try:
            state_dir = Path(os.path.expanduser(args.state_dir)) if args.state_dir else detect_default_state_dir()
            write_dual_slot(state_dir, "quota", quota_doc)
        except Exception:
            pass

    sys.stdout.write(render_cli_line(payload, quota_doc) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
