#!/usr/bin/env python3
import datetime as dt
import json
import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path

import importlib.util

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "hooks"))

import collect  # noqa: E402

_sl_spec = importlib.util.spec_from_file_location("statusline_sync", ROOT / "hooks" / "statusline-sync.py")
assert _sl_spec and _sl_spec.loader
statusline_sync = importlib.util.module_from_spec(_sl_spec)
_sl_spec.loader.exec_module(statusline_sync)


def write_jsonl(path: Path, rows: list[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as fh:
        for row in rows:
            fh.write(json.dumps(row) + "\n")


class CollectorTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.base = Path(self.tmp.name)
        self.app_dir = self.base / "antigravity"
        self.state_dir = self.base / "state"
        self.app_dir.mkdir(parents=True, exist_ok=True)
        self.state_dir.mkdir(parents=True, exist_ok=True)

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def _create_db(self, rows: list[tuple]) -> None:
        db = self.app_dir / "conversation_summaries.db"
        conn = sqlite3.connect(str(db))
        conn.execute(
            """
            CREATE TABLE conversation_summaries (
                conversation_id text PRIMARY KEY,
                title text NOT NULL DEFAULT "",
                step_count integer NOT NULL DEFAULT 0,
                last_modified_time datetime NOT NULL,
                status text NOT NULL DEFAULT "",
                parent_conversation_id text NOT NULL DEFAULT "",
                nesting_depth integer NOT NULL DEFAULT 0,
                not_fully_idle numeric NOT NULL DEFAULT false
            )
            """
        )
        conn.executemany(
            "INSERT INTO conversation_summaries VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            rows,
        )
        conn.commit()
        conn.close()

    def test_transcript_dedupe_today_and_subagent_rollup(self) -> None:
        now = dt.datetime(2026, 11, 24, 3, 10, 0, tzinfo=dt.timezone.utc)
        self._create_db(
            [
                ("root-1", "Main Feature", 10, "2026-11-24 03:09:00+00:00", "CASCADE_RUN_STATUS_IDLE", "", 0, 0),
                ("child-1", "Research Worker", 4, "2026-11-24 03:08:00+00:00", "CASCADE_RUN_STATUS_IDLE", "root-1", 1, 0),
            ]
        )
        write_jsonl(
            self.app_dir / "brain" / "root-1" / ".system_generated" / "logs" / "transcript.jsonl",
            [
                {"step_index": 0, "type": "USER_INPUT", "created_at": "2026-11-24T03:00:00Z"},
                # Duplicate step_index=1 should keep the latest entry only
                {"step_index": 1, "type": "PLANNER_RESPONSE", "created_at": "2026-11-24T03:00:10Z", "model": "gemini-4-neon-high", "input_tokens": 1000, "cache_read_tokens": 0, "output_tokens": 100},
                {"step_index": 1, "type": "PLANNER_RESPONSE", "created_at": "2026-11-24T03:00:10Z", "model": "gemini-4-neon-high", "input_tokens": 10000, "cache_read_tokens": 0, "output_tokens": 500},
                {"step_index": 3, "type": "PLANNER_RESPONSE", "created_at": "2026-11-24T03:01:00Z", "model": "gemini-4-neon-high", "input_tokens": 2000, "cache_read_tokens": 10000, "output_tokens": 600},
                # Yesterday step counts toward session total but NOT today total
                {"step_index": 2, "type": "PLANNER_RESPONSE", "created_at": "2026-11-22T03:00:00Z", "model": "gemini-4-neon-high", "input_tokens": 5000, "cache_read_tokens": 5000, "output_tokens": 400},
            ],
        )
        write_jsonl(
            self.app_dir / "brain" / "child-1" / ".system_generated" / "logs" / "transcript.jsonl",
            [
                {"step_index": 1, "type": "PLANNER_RESPONSE", "created_at": "2026-11-24T03:05:00Z", "model": "claude-sonnet-5-5-high", "input_tokens": 3000, "cache_read_tokens": 7000, "output_tokens": 500},
            ],
        )

        snap = collect.collect_snapshot(self.app_dir, self.state_dir, active_cid="root-1", now_utc=now)
        self.assertEqual(snap["activeConversationId"], "root-1")
        self.assertIn("root-1", snap["sessions"])
        self.assertNotIn("child-1", snap["sessions"])

        root_s = snap["sessions"]["root-1"]
        # root session: step1(10500) + step2(10400) + step3(12600) = 33500
        self.assertEqual(root_s["sessionTokens"], 33500)
        self.assertEqual(root_s["subagentCount"], 1)
        self.assertEqual(root_s["subagentTokens"], 10500)
        self.assertEqual(root_s["totalWithSubagentsTokens"], 44000)
        self.assertEqual(root_s["modelFamily"], "gemini")
        self.assertEqual(root_s["contextTokens"], 12000)
        self.assertAlmostEqual(root_s["cacheHitRate"], 15000 / 32000, places=4)

        # today: root step1(10500) + root step3(12600) + child step1(10500) = 33600
        self.assertEqual(snap["today"]["totalTokens"], 33600)
        self.assertEqual(snap["today"]["sessionsCount"], 2)

    def test_pre_post_invocation_exact_tok_per_sec_and_dual_slot(self) -> None:
        t0 = dt.datetime(2026, 11, 24, 3, 20, 0, tzinfo=dt.timezone.utc)
        t1 = dt.datetime(2026, 11, 24, 3, 20, 10, tzinfo=dt.timezone.utc)
        write_jsonl(
            self.app_dir / "brain" / "sess-a" / ".system_generated" / "logs" / "transcript.jsonl",
            [
                {"step_index": 1, "type": "PLANNER_RESPONSE", "created_at": "2026-11-24T03:20:10Z", "model": "claude-opus-5-5-high", "input_tokens": 5000, "cache_read_tokens": 15000, "output_tokens": 500},
            ],
        )

        collect.handle_hook_event(
            "PreInvocation",
            {"conversationId": "sess-a", "modelName": "claude-opus-5-5-high", "initialNumSteps": 1},
            self.app_dir,
            self.state_dir,
            now_utc=t0,
            launch_overlay=False,
        )
        snap = collect.handle_hook_event(
            "PostInvocation",
            {"conversationId": "sess-a", "modelName": "claude-opus-5-5-high", "initialNumSteps": 1},
            self.app_dir,
            self.state_dir,
            now_utc=t1,
            launch_overlay=False,
        )
        assert snap is not None
        s = snap["sessions"]["sess-a"]
        self.assertEqual(s["modelFamily"], "claude")
        self.assertEqual(s["lastTurnDurationMs"], 10000)
        self.assertAlmostEqual(s["tokPerSec"], 50.0, places=2)
        self.assertFalse(s["tokPerSecEstimated"])
        self.assertEqual(s["executionDurationMs"], 10000)
        self.assertAlmostEqual(s["contextPct"], 10.0, places=2)

        seq, latest = collect.read_latest_slot(self.state_dir, "metrics")
        self.assertEqual(seq, 1)
        assert latest is not None
        self.assertEqual(latest["activeConversationId"], "sess-a")

    def test_statusline_quota_sync_conservative_dual_pool(self) -> None:
        now = dt.datetime(2026, 11, 24, 3, 0, 0, tzinfo=dt.timezone.utc)
        payload = {
            "model": {"display_name": "Gemini 4 Neon (High)"},
            "context_window": {"used_percentage": 18.5},
            "plan_tier": "Pro",
            "email": "should-not-be-saved@example.com",
            "quota": {
                "gemini-pro-5h": {"remaining_fraction": 0.42, "reset_time": "2026-11-24T06:00:00Z"},
                "gemini-flash-5h": {"remaining_fraction": 0.25, "reset_time": "2026-11-24T06:00:00Z"},
                "claude-weekly": {"remaining_fraction": 0.12, "reset_in_seconds": 3600},
            },
        }
        qdoc = statusline_sync.parse_statusline_quota(payload, now_utc=now)
        assert qdoc is not None
        self.assertNotIn("email", qdoc)
        self.assertEqual(qdoc["planTier"], "Pro")
        self.assertAlmostEqual(qdoc["pools"]["gemini"]["remainingPct"], 25.0)
        self.assertAlmostEqual(qdoc["pools"]["claude"]["remainingPct"], 12.0)
        self.assertEqual(qdoc["pools"]["claude"]["resetAt"], "2026-11-24T04:00:00Z")

        line = statusline_sync.render_cli_line(payload, qdoc)
        self.assertIn("G 25%", line)
        self.assertIn("C 12%", line)


if __name__ == "__main__":
    unittest.main()
