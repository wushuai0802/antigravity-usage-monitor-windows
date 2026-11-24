#!/usr/bin/env python3
"""Antigravity Usage Monitor - Read-only local transcript & hook collector.

Zero third-party dependencies (Python 3.10+ standard library only).
Never modifies Antigravity data, never stores conversation prose or credentials,
and always prints `{}` on stdout in hook mode so host turns are never disrupted.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import platform
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Any

SCHEMA_VERSION = 1
MAX_SESSIONS_IN_SNAPSHOT = 16
MAX_TURN_DURATION_MS = 30 * 60 * 1000
MIN_TURN_DURATION_MS = 200
MAX_ESTIMATED_GAP_MS = 5 * 60 * 1000


def parse_iso_utc(raw: str | None) -> dt.datetime | None:
    if not raw or not isinstance(raw, str):
        return None
    text = raw.strip()
    if not text:
        return None
    # Handle Go/SQLite nanosecond timestamps like "2026-11-24 02:56:07.241057713+00:00"
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    if " " in text and "T" not in text:
        text = text.replace(" ", "T", 1)
    # Trim fractional seconds beyond 6 digits for Python <= 3.10 compatibility
    if "." in text:
        head, rest = text.split(".", 1)
        tz_idx = -1
        for sep in ("+", "-"):
            idx = rest.find(sep)
            if idx != -1 and (tz_idx == -1 or idx < tz_idx):
                tz_idx = idx
        if tz_idx != -1:
            frac, tz = rest[:tz_idx], rest[tz_idx:]
            text = f"{head}.{frac[:6]}{tz}"
        else:
            text = f"{head}.{rest[:6]}"
    try:
        parsed = dt.datetime.fromisoformat(text)
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=dt.timezone.utc)
    return parsed.astimezone(dt.timezone.utc)


def format_iso_utc(value: dt.datetime) -> str:
    return value.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def classify_model_family(model: str | None) -> str:
    name = (model or "").strip().lower()
    if not name:
        return "unknown"
    if any(k in name for k in ("claude", "opus", "sonnet", "haiku", "gpt-oss", "gpt")):
        return "claude"
    if any(k in name for k in ("gemini", "orchestrator", "neon", "argon", "carbon", "hydrogen", "flash")):
        return "gemini"
    return "unknown"


def context_window_for_model(model: str | None) -> int:
    name = (model or "").strip().lower()
    if not name:
        return 1_048_576
    if "gpt-oss" in name:
        return 128_000
    if any(k in name for k in ("claude", "opus", "sonnet", "haiku")):
        if "1m" in name:
            return 1_000_000
        return 200_000
    return 1_048_576


def is_wsl() -> bool:
    if os.name != "posix":
        return False
    if os.environ.get("WSL_DISTRO_NAME") or os.environ.get("WSL_INTEROP"):
        return True
    try:
        return "microsoft" in platform.release().lower()
    except OSError:
        return False


def detect_default_state_dir() -> Path:
    env_override = os.environ.get("AGY_MONITOR_STATE_DIR", "").strip()
    if env_override:
        return Path(env_override)
    if os.name == "nt":
        local_app = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
        return Path(local_app) / "AntigravityUsageMonitor"
    if is_wsl():
        cache_file = Path.home() / ".cache" / "antigravity-usage-monitor-statedir"
        if cache_file.is_file():
            try:
                cached = cache_file.read_text(encoding="utf-8").strip()
                if cached and Path(cached).parent.exists():
                    return Path(cached)
            except OSError:
                pass
        resolved: Path | None = None
        # Prefer probing Windows %LOCALAPPDATA% via cmd.exe once and caching it.
        cmd_exe = shutil.which("cmd.exe") or "/mnt/c/Windows/System32/cmd.exe"
        wslpath = shutil.which("wslpath")
        if os.path.exists(cmd_exe) and wslpath:
            try:
                out = subprocess.check_output(
                    [cmd_exe, "/c", "echo", "%LOCALAPPDATA%"],
                    stderr=subprocess.DEVNULL,
                    timeout=3,
                    text=True,
                ).strip()
                if out and "%" not in out:
                    posix = subprocess.check_output(
                        [wslpath, "-u", out],
                        stderr=subprocess.DEVNULL,
                        timeout=3,
                        text=True,
                    ).strip()
                    if posix:
                        resolved = Path(posix) / "AntigravityUsageMonitor"
            except (OSError, subprocess.SubprocessError):
                resolved = None
        if resolved is None:
            users_dir = Path("/mnt/c/Users")
            if users_dir.is_dir():
                skip = {"Public", "Default", "Default User", "All Users"}
                candidates = [
                    p / "AppData" / "Local" / "AntigravityUsageMonitor"
                    for p in users_dir.iterdir()
                    if p.is_dir() and p.name not in skip and (p / "AppData" / "Local").is_dir()
                ]
                # Prefer Administrator or user dir containing .gemini
                candidates.sort(
                    key=lambda p: (
                        0 if (p.parents[2] / ".gemini").exists() else 1,
                        p.parents[2].name.lower(),
                    )
                )
                if candidates:
                    resolved = candidates[0]
        if resolved is not None:
            try:
                cache_file.parent.mkdir(parents=True, exist_ok=True)
                cache_file.write_text(str(resolved), encoding="utf-8")
            except OSError:
                pass
            return resolved
    return Path.home() / ".local" / "state" / "AntigravityUsageMonitor"


def detect_default_app_data_dir(payload: dict[str, Any] | None = None) -> Path:
    if payload:
        transcript = payload.get("transcriptPath")
        if isinstance(transcript, str) and transcript:
            p = Path(os.path.expanduser(transcript))
            # <app_data_dir>/brain/<cid>/.system_generated/logs/transcript.jsonl
            for parent in p.parents:
                if parent.name == "brain":
                    return parent.parent
    env_dir = os.environ.get("AGY_APP_DATA_DIR", "").strip()
    if env_dir:
        return Path(os.path.expanduser(env_dir))
    return Path.home() / ".gemini" / "antigravity"


def read_latest_slot(state_dir: Path, prefix: str) -> tuple[int, dict[str, Any] | None]:
    best_seq = 0
    best_doc: dict[str, Any] | None = None
    for idx in (0, 1):
        slot = state_dir / f"{prefix}.slot{idx}.json"
        if not slot.is_file():
            continue
        try:
            doc = json.loads(slot.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        if not isinstance(doc, dict):
            continue
        seq = doc.get("sequence", 0)
        if isinstance(seq, int) and seq >= best_seq:
            best_seq = seq
            best_doc = doc
    return best_seq, best_doc


def write_dual_slot(state_dir: Path, prefix: str, doc: dict[str, Any]) -> Path:
    state_dir.mkdir(parents=True, exist_ok=True)
    prev_seq, _ = read_latest_slot(state_dir, prefix)
    seq = prev_seq + 1
    doc["sequence"] = seq
    target = state_dir / f"{prefix}.slot{seq % 2}.json"
    serialized = json.dumps(doc, ensure_ascii=False, separators=(",", ":"))
    fd, tmp_name = tempfile.mkstemp(prefix=f".{prefix}.", suffix=".tmp", dir=str(state_dir))
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(serialized)
        os.replace(tmp_name, target)
    except OSError:
        try:
            os.unlink(tmp_name)
        except OSError:
            pass
        raise
    return target


def load_timing_state(state_dir: Path) -> dict[str, Any]:
    path = state_dir / "timing.json"
    if not path.is_file():
        return {"pending": {}, "turns": {}}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        if isinstance(data, dict):
            data.setdefault("pending", {})
            data.setdefault("turns", {})
            return data
    except (OSError, ValueError):
        pass
    return {"pending": {}, "turns": {}}


def save_timing_state(state_dir: Path, timing: dict[str, Any]) -> None:
    state_dir.mkdir(parents=True, exist_ok=True)
    # Prune old turn entries (keep at most 64 conversations)
    turns = timing.get("turns", {})
    if isinstance(turns, dict) and len(turns) > 64:
        ordered = sorted(
            turns.items(),
            key=lambda kv: kv[1].get("updatedMs", 0) if isinstance(kv[1], dict) else 0,
            reverse=True,
        )[:64]
        timing["turns"] = dict(ordered)
    pending = timing.get("pending", {})
    if isinstance(pending, dict) and len(pending) > 32:
        ordered_p = sorted(
            pending.items(),
            key=lambda kv: kv[1].get("startedMs", 0) if isinstance(kv[1], dict) else 0,
            reverse=True,
        )[:32]
        timing["pending"] = dict(ordered_p)
    target = state_dir / "timing.json"
    fd, tmp_name = tempfile.mkstemp(prefix=".timing.", suffix=".tmp", dir=str(state_dir))
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(timing, fh, ensure_ascii=False, separators=(",", ":"))
        os.replace(tmp_name, target)
    except OSError:
        try:
            os.unlink(tmp_name)
        except OSError:
            pass


def read_conversation_db(app_data_dir: Path) -> dict[str, dict[str, Any]]:
    db_path = app_data_dir / "conversation_summaries.db"
    if not db_path.is_file():
        return {}
    result: dict[str, dict[str, Any]] = {}
    conn: sqlite3.Connection | None = None
    try:
        uri = f"file:{db_path.as_posix()}?mode=ro"
        conn = sqlite3.connect(uri, uri=True, timeout=1.5)
        cur = conn.cursor()
        cur.execute(
            """
            SELECT conversation_id, title, step_count, last_modified_time,
                   status, parent_conversation_id, nesting_depth, not_fully_idle
            FROM conversation_summaries
            ORDER BY last_modified_time DESC
            LIMIT 120
            """
        )
        for row in cur.fetchall():
            cid = str(row[0] or "").strip()
            if not cid:
                continue
            result[cid] = {
                "conversationId": cid,
                "title": str(row[1] or ""),
                "stepCount": int(row[2] or 0),
                "lastModifiedAt": row[3],
                "status": str(row[4] or ""),
                "parentConversationId": str(row[5] or ""),
                "nestingDepth": int(row[6] or 0),
                "notFullyIdle": bool(row[7]),
            }
    except sqlite3.Error:
        return {}
    finally:
        if conn is not None:
            try:
                conn.close()
            except sqlite3.Error:
                pass
    return result


def parse_transcript(transcript_path: Path, today_local_date: dt.date, local_tz: dt.tzinfo) -> dict[str, Any] | None:
    if not transcript_path.is_file():
        return None

    # Deduplicate PLANNER_RESPONSE by step_index (last valid entry wins).
    planner_steps: dict[int, dict[str, Any]] = {}
    step_timestamps: list[tuple[int, dt.datetime]] = []
    latest_ts: dt.datetime | None = None

    try:
        with transcript_path.open("r", encoding="utf-8", errors="replace") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    entry = json.loads(line)
                except ValueError:
                    continue
                if not isinstance(entry, dict):
                    continue
                ts = parse_iso_utc(entry.get("created_at"))
                idx_raw = entry.get("step_index")
                if ts is not None:
                    if latest_ts is None or ts > latest_ts:
                        latest_ts = ts
                    if isinstance(idx_raw, int):
                        step_timestamps.append((idx_raw, ts))
                if entry.get("type") != "PLANNER_RESPONSE":
                    continue
                if not isinstance(idx_raw, int):
                    continue
                in_tok = int(entry.get("input_tokens") or 0)
                cache_tok = int(entry.get("cache_read_tokens") or 0)
                out_tok = int(entry.get("output_tokens") or 0)
                if in_tok < 0:
                    in_tok = 0
                if cache_tok < 0:
                    cache_tok = 0
                if out_tok < 0:
                    out_tok = 0
                planner_steps[idx_raw] = {
                    "stepIndex": idx_raw,
                    "createdAt": ts,
                    "model": str(entry.get("model") or ""),
                    "inputTokens": in_tok,
                    "cacheReadTokens": cache_tok,
                    "outputTokens": out_tok,
                }
    except OSError:
        return None

    if not planner_steps:
        return {
            "sessionTokens": 0,
            "inputTokens": 0,
            "cacheReadTokens": 0,
            "outputTokens": 0,
            "todayInputTokens": 0,
            "todayCacheReadTokens": 0,
            "todayOutputTokens": 0,
            "todayTotalTokens": 0,
            "lastStepIndex": -1,
            "lastModel": "",
            "lastTurnTokens": 0,
            "lastTurnOutputTokens": 0,
            "estimatedTurnDurationMs": None,
            "contextTokens": 0,
            "lastModifiedAt": latest_ts,
        }

    ordered_steps = [planner_steps[k] for k in sorted(planner_steps.keys())]
    step_ts_map = dict(step_timestamps)

    total_in = 0
    total_cache = 0
    total_out = 0
    today_in = 0
    today_cache = 0
    today_out = 0

    for st in ordered_steps:
        i_t = st["inputTokens"]
        c_t = st["cacheReadTokens"]
        o_t = st["outputTokens"]
        total_in += i_t
        total_cache += c_t
        total_out += o_t
        ts = st["createdAt"]
        if ts is not None and ts.astimezone(local_tz).date() == today_local_date:
            today_in += i_t
            today_cache += c_t
            today_out += o_t

    last = ordered_steps[-1]
    last_idx = last["stepIndex"]
    last_ts = last["createdAt"]
    estimated_ms: int | None = None
    if last_ts is not None:
        # Find the closest preceding step timestamp to estimate turn duration when hook timing is absent.
        prev_candidates = [t for idx, t in step_ts_map.items() if idx < last_idx and t <= last_ts]
        if prev_candidates:
            prev_ts = max(prev_candidates)
            delta_ms = int((last_ts - prev_ts).total_seconds() * 1000)
            if MIN_TURN_DURATION_MS <= delta_ms <= MAX_ESTIMATED_GAP_MS:
                estimated_ms = delta_ms

    return {
        "sessionTokens": total_in + total_cache + total_out,
        "inputTokens": total_in,
        "cacheReadTokens": total_cache,
        "outputTokens": total_out,
        "todayInputTokens": today_in,
        "todayCacheReadTokens": today_cache,
        "todayOutputTokens": today_out,
        "todayTotalTokens": today_in + today_cache + today_out,
        "lastStepIndex": last_idx,
        "lastModel": last["model"],
        "lastTurnTokens": last["inputTokens"] + last["cacheReadTokens"] + last["outputTokens"],
        "lastTurnOutputTokens": last["outputTokens"],
        "estimatedTurnDurationMs": estimated_ms,
        "contextTokens": last["inputTokens"] + last["cacheReadTokens"],
        "lastModifiedAt": last_ts or latest_ts,
    }


def resolve_root_conversation(cid: str, db_meta: dict[str, dict[str, Any]]) -> str:
    visited: set[str] = set()
    cur = cid
    while cur and cur not in visited:
        visited.add(cur)
        meta = db_meta.get(cur)
        if not meta:
            break
        parent = meta.get("parentConversationId") or ""
        if not parent:
            break
        cur = parent
    return cur


def collect_snapshot(
    app_data_dir: Path,
    state_dir: Path,
    active_cid: str | None = None,
    active_model_hint: str | None = None,
    now_utc: dt.datetime | None = None,
    timing: dict[str, Any] | None = None,
) -> dict[str, Any]:
    now = now_utc or dt.datetime.now(dt.timezone.utc)
    local_tz = dt.datetime.now().astimezone().tzinfo or dt.timezone.utc
    today_local = now.astimezone(local_tz).date()

    db_meta = read_conversation_db(app_data_dir)
    brain_dir = app_data_dir / "brain"
    candidate_paths: dict[str, Path] = {}

    if brain_dir.is_dir():
        # Gather all transcript files, prioritizing recently modified ones
        entries: list[tuple[float, str, Path]] = []
        for child in brain_dir.iterdir():
            if not child.is_dir():
                continue
            tpath = child / ".system_generated" / "logs" / "transcript.jsonl"
            if tpath.is_file():
                try:
                    mtime = tpath.stat().st_mtime
                except OSError:
                    continue
                entries.append((mtime, child.name, tpath))
        entries.sort(key=lambda x: x[0], reverse=True)
        # Scan all conversations touched within last 48h plus at least top 32 most recent
        cutoff_epoch = now.timestamp() - 48 * 3600
        for idx, (mtime, cid, tpath) in enumerate(entries):
            if idx < 32 or mtime >= cutoff_epoch or cid == active_cid:
                candidate_paths[cid] = tpath

    parsed_by_cid: dict[str, dict[str, Any]] = {}
    today_in = 0
    today_cache = 0
    today_out = 0
    today_sessions = 0

    for cid, tpath in candidate_paths.items():
        parsed = parse_transcript(tpath, today_local, local_tz)
        if parsed is None:
            continue
        parsed_by_cid[cid] = parsed
        if parsed["todayTotalTokens"] > 0:
            today_in += parsed["todayInputTokens"]
            today_cache += parsed["todayCacheReadTokens"]
            today_out += parsed["todayOutputTokens"]
            today_sessions += 1

    # Aggregate subagents into root conversations
    subagent_agg: dict[str, dict[str, int]] = {}
    for cid, parsed in parsed_by_cid.items():
        root_cid = resolve_root_conversation(cid, db_meta)
        if root_cid != cid:
            bucket = subagent_agg.setdefault(root_cid, {"count": 0, "tokens": 0})
            bucket["count"] += 1
            bucket["tokens"] += parsed["sessionTokens"]

    timing_turns = (timing or {}).get("turns", {}) if isinstance(timing, dict) else {}

    # Build session entries (root conversations prioritized)
    sessions_list: list[tuple[float, str, dict[str, Any]]] = []
    for cid, parsed in parsed_by_cid.items():
        meta = db_meta.get(cid, {})
        # Skip child conversations as top-level cards unless explicitly active
        if meta.get("parentConversationId") and cid != active_cid:
            continue
        model = parsed["lastModel"] or (active_model_hint if cid == active_cid else "") or ""
        family = classify_model_family(model)
        ctx_window = context_window_for_model(model)
        ctx_tokens = parsed["contextTokens"]
        ctx_pct = round(min(100.0, (ctx_tokens / ctx_window) * 100.0), 2) if ctx_window > 0 and ctx_tokens > 0 else 0.0

        in_plus_cache = parsed["inputTokens"] + parsed["cacheReadTokens"]
        hit_rate = round(parsed["cacheReadTokens"] / in_plus_cache, 4) if in_plus_cache > 0 else None

        # Turn timing & tok/s
        turn_rec = timing_turns.get(cid) if isinstance(timing_turns, dict) else None
        last_dur_ms: int | None = None
        tok_per_sec: float | None = None
        tok_per_sec_estimated = False
        exec_dur_ms = 0

        if isinstance(turn_rec, dict):
            exec_dur_ms = int(turn_rec.get("executionDurationMs") or 0)
            rec_dur = turn_rec.get("lastTurnDurationMs")
            rec_step = turn_rec.get("lastStepIndex")
            if isinstance(rec_dur, int) and MIN_TURN_DURATION_MS <= rec_dur <= MAX_TURN_DURATION_MS:
                # Prefer exact hook duration if it matches or is newer than parsed transcript
                if rec_step is None or rec_step >= parsed["lastStepIndex"]:
                    last_dur_ms = rec_dur

        if last_dur_ms is None and parsed["estimatedTurnDurationMs"] is not None:
            last_dur_ms = parsed["estimatedTurnDurationMs"]
            tok_per_sec_estimated = True

        if last_dur_ms and parsed["lastTurnOutputTokens"] > 0:
            tok_per_sec = round(parsed["lastTurnOutputTokens"] / (last_dur_ms / 1000.0), 2)

        sub = subagent_agg.get(cid, {"count": 0, "tokens": 0})
        last_mod = parsed["lastModifiedAt"] or parse_iso_utc(meta.get("lastModifiedAt")) or now

        entry = {
            "conversationId": cid,
            "title": meta.get("title") or cid[:8],
            "model": model,
            "modelFamily": family,
            "status": meta.get("status") or "",
            "lastModifiedAt": format_iso_utc(last_mod),
            "sessionTokens": parsed["sessionTokens"],
            "inputTokens": parsed["inputTokens"],
            "cacheReadTokens": parsed["cacheReadTokens"],
            "outputTokens": parsed["outputTokens"],
            "cacheHitRate": hit_rate,
            "lastTurnTokens": parsed["lastTurnTokens"],
            "lastTurnOutputTokens": parsed["lastTurnOutputTokens"],
            "lastTurnDurationMs": last_dur_ms,
            "tokPerSec": tok_per_sec,
            "tokPerSecEstimated": tok_per_sec_estimated,
            "contextTokens": ctx_tokens,
            "contextWindowSize": ctx_window,
            "contextPct": ctx_pct,
            "subagentCount": sub["count"],
            "subagentTokens": sub["tokens"],
            "totalWithSubagentsTokens": parsed["sessionTokens"] + sub["tokens"],
            "executionDurationMs": exec_dur_ms,
        }
        sessions_list.append((last_mod.timestamp(), cid, entry))

    sessions_list.sort(key=lambda item: item[0], reverse=True)
    selected = sessions_list[:MAX_SESSIONS_IN_SNAPSHOT]
    sessions_map = {cid: entry for _, cid, entry in selected}

    resolved_active = active_cid if active_cid in sessions_map else (selected[0][1] if selected else "")
    today_in_plus_cache = today_in + today_cache
    today_hit_rate = round(today_cache / today_in_plus_cache, 4) if today_in_plus_cache > 0 else None

    return {
        "schemaVersion": SCHEMA_VERSION,
        "updatedAt": format_iso_utc(now),
        "sourceEnv": "wsl" if is_wsl() else ("windows" if os.name == "nt" else "posix"),
        "activeConversationId": resolved_active,
        "today": {
            "date": today_local.isoformat(),
            "totalTokens": today_in + today_cache + today_out,
            "inputTokens": today_in,
            "cacheReadTokens": today_cache,
            "outputTokens": today_out,
            "cacheHitRate": today_hit_rate,
            "sessionsCount": today_sessions,
        },
        "sessions": sessions_map,
    }


def maybe_launch_overlay(state_dir: Path) -> None:
    overlay_exe = state_dir / "bin" / "AntigravityUsageOverlay.exe"
    if not overlay_exe.is_file():
        return
    try:
        if os.name == "nt":
            subprocess.Popen(
                ["explorer.exe", str(overlay_exe)],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                close_fds=True,
            )
        elif is_wsl():
            wslpath = shutil.which("wslpath")
            explorer = "/mnt/c/Windows/explorer.exe"
            if wslpath and os.path.exists(explorer):
                win_exe = subprocess.check_output(
                    [wslpath, "-w", str(overlay_exe)],
                    stderr=subprocess.DEVNULL,
                    timeout=2,
                    text=True,
                ).strip()
                if win_exe:
                    subprocess.Popen(
                        [explorer, win_exe],
                        stdout=subprocess.DEVNULL,
                        stderr=subprocess.DEVNULL,
                        start_new_session=True,
                    )
    except (OSError, subprocess.SubprocessError):
        pass


def handle_hook_event(
    event: str,
    payload: dict[str, Any],
    app_data_dir: Path,
    state_dir: Path,
    now_utc: dt.datetime | None = None,
    launch_overlay: bool = True,
) -> dict[str, Any] | None:
    now = now_utc or dt.datetime.now(dt.timezone.utc)
    now_ms = int(now.timestamp() * 1000)
    cid = str(payload.get("conversationId") or "").strip()
    model_name = str(payload.get("modelName") or "").strip()

    timing = load_timing_state(state_dir)

    if event == "SessionStart":
        if launch_overlay:
            maybe_launch_overlay(state_dir)
        snapshot = collect_snapshot(app_data_dir, state_dir, active_cid=cid or None, active_model_hint=model_name or None, now_utc=now, timing=timing)
        write_dual_slot(state_dir, "metrics", snapshot)
        return snapshot

    if event == "PreInvocation":
        if cid:
            timing["pending"][cid] = {
                "startedMs": now_ms,
                "invocationNum": payload.get("invocationNum"),
                "initialNumSteps": payload.get("initialNumSteps"),
                "modelName": model_name,
            }
            save_timing_state(state_dir, timing)
        return None

    if event in ("PostInvocation", "Stop"):
        if cid:
            pending = timing["pending"].pop(cid, None)
            turn_rec = timing["turns"].setdefault(
                cid,
                {"executionDurationMs": 0, "lastTurnDurationMs": None, "lastStepIndex": -1, "countedSteps": [], "updatedMs": now_ms},
            )
            if isinstance(pending, dict) and isinstance(pending.get("startedMs"), int):
                delta = now_ms - pending["startedMs"]
                if MIN_TURN_DURATION_MS <= delta <= MAX_TURN_DURATION_MS:
                    turn_rec["lastTurnDurationMs"] = delta
                    step_marker = pending.get("initialNumSteps")
                    counted = turn_rec.setdefault("countedSteps", [])
                    if step_marker not in counted:
                        turn_rec["executionDurationMs"] = int(turn_rec.get("executionDurationMs") or 0) + delta
                        counted.append(step_marker)
                        if len(counted) > 200:
                            del counted[:-200]
            # Peek transcript for latest step_index
            tpath = app_data_dir / "brain" / cid / ".system_generated" / "logs" / "transcript.jsonl"
            if tpath.is_file():
                parsed = parse_transcript(tpath, now.astimezone().date(), dt.datetime.now().astimezone().tzinfo or dt.timezone.utc)
                if parsed and parsed["lastStepIndex"] >= 0:
                    turn_rec["lastStepIndex"] = parsed["lastStepIndex"]
            turn_rec["updatedMs"] = now_ms
            save_timing_state(state_dir, timing)

        snapshot = collect_snapshot(app_data_dir, state_dir, active_cid=cid or None, active_model_hint=model_name or None, now_utc=now, timing=timing)
        write_dual_slot(state_dir, "metrics", snapshot)
        return snapshot

    return None


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Antigravity Usage Monitor collector")
    parser.add_argument("--event", choices=("SessionStart", "PreInvocation", "PostInvocation", "Stop"), help="Antigravity hook event name")
    parser.add_argument("--scan-only", action="store_true", help="Scan local transcripts without reading hook stdin")
    parser.add_argument("--app-data-dir", help="Override ~/.gemini/antigravity directory")
    parser.add_argument("--state-dir", help="Override state output directory")
    parser.add_argument("--no-launch", action="store_true", help="Do not launch overlay executable on SessionStart")
    parser.add_argument("--now", help="Override current UTC timestamp (ISO8601) for testing")
    args = parser.parse_args(argv)

    now_utc = parse_iso_utc(args.now) if args.now else None
    payload: dict[str, Any] = {}
    if args.event and not args.scan_only:
        try:
            raw = sys.stdin.read()
            if raw.strip():
                loaded = json.loads(raw)
                if isinstance(loaded, dict):
                    payload = loaded
        except (OSError, ValueError):
            payload = {}

    try:
        app_data_dir = Path(os.path.expanduser(args.app_data_dir)) if args.app_data_dir else detect_default_app_data_dir(payload)
        state_dir = Path(os.path.expanduser(args.state_dir)) if args.state_dir else detect_default_state_dir()
        if args.scan_only:
            timing = load_timing_state(state_dir)
            snapshot = collect_snapshot(app_data_dir, state_dir, now_utc=now_utc, timing=timing)
            write_dual_slot(state_dir, "metrics", snapshot)
        elif args.event:
            handle_hook_event(
                args.event,
                payload,
                app_data_dir,
                state_dir,
                now_utc=now_utc,
                launch_overlay=not args.no_launch,
            )
    except Exception:
        # Hook failures must never block or pollute Antigravity turns.
        pass

    if args.event:
        sys.stdout.write("{}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
