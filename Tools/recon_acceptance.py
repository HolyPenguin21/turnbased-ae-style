#!/usr/bin/env python3
"""Ground Recon acceptance verifier for AI Strategy V2.

Usage:
    python Tools/recon_acceptance.py AiDebug.log
    python Tools/recon_acceptance.py AiDebug1.log AiDebug2.log

Statuses are intentionally strict:
- PASS: the scenario was observed and satisfied, or a static honesty invariant was verified.
- FAIL: an observed invariant was violated.
- NOT_OBSERVED: the supplied run did not exercise the scenario.

Exit codes: 0 = all PASS, 1 = at least one FAIL, 2 = no FAIL but at least one NOT_OBSERVED.
"""

from __future__ import annotations

import argparse
import re
import sys
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, List, Sequence, Tuple


PASS = "PASS"
FAIL = "FAIL"
NOT_OBSERVED = "NOT_OBSERVED"

SCENARIOS: Sequence[Tuple[str, str]] = (
    ("weak-recce-attack", "weak visible Recce can be opportunistically attacked"),
    ("hidden-facility-capture", "hidden scout uses the safe decloak/capture sequence"),
    ("capture-cancel-live-danger", "capture is cancelled when the live recheck reveals danger"),
    ("refresh-dominates-explored", "mostly explored map can shift strategic pressure to Refresh"),
    ("stale-facility-refresh", "stale known facility creates a Refresh objective"),
    ("coarse-direction-pressure", "enemy presence reaches Recon only through coarse direction pressure"),
    ("hidden-concentration-honesty", "direction model does not consume hidden strength/composition"),
    ("three-scout-deconflict", "three concurrent scouts receive injective/spatially distinct work"),
    ("a-b-a-b-avoidance", "ground scout avoids an immediate A-B-A-B loop"),
    ("per-step-replan", "ground scout performs multiple live one-step plans"),
)

ACCEPTANCE_RE = re.compile(
    r"\[AI\]\[V2\]\[Recon\]\[Acceptance\].*?scenario=([^\s]+).*?status=(PASS|FAIL)",
    re.IGNORECASE,
)
STEP_RE = re.compile(
    r"\[AI\]\[V2\]\[Recon\]\[Ground\]\[Step\].*?army=(.*?)\s+mode=.*?\s+from=(.*?)\s+step=(.*?)\s+score=",
    re.IGNORECASE,
)


@dataclass
class Result:
    status: str = NOT_OBSERVED
    evidence: str = "scenario not exercised by supplied log(s)"


def _promote(results: Dict[str, Result], scenario: str, status: str, evidence: str) -> None:
    if scenario not in results:
        return
    current = results[scenario].status
    if current == FAIL:
        return
    if status == FAIL or current == NOT_OBSERVED:
        results[scenario] = Result(status, evidence)


def _read_logs(paths: Iterable[Path]) -> List[str]:
    lines: List[str] = []
    for path in paths:
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError as exc:
            raise RuntimeError(f"cannot read {path}: {exc}") from exc
        lines.extend(text.splitlines())
    return lines


def _strip_csharp_comments(text: str) -> str:
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.DOTALL)
    return re.sub(r"//.*?$", "", text, flags=re.MULTILINE)


def _check_direction_honesty(source_root: Path, results: Dict[str, Result]) -> None:
    path = source_root / "Assets" / "Scripts" / "Ai" / "V2" / "ReconDirectionModel.cs"
    if not path.is_file():
        return

    raw = path.read_text(encoding="utf-8", errors="replace")
    code = _strip_csharp_comments(raw)
    required = (
        "weights[Sector(origin, enemy.Hex)] += 1f;",
        "EnemyPresenceWeight = enemyCount",
    )
    forbidden = (
        "enemy.Strength",
        "enemy.Power",
        "enemy.ArmyPower",
        "enemy.Members",
        "enemy.Cards",
        "enemy.Composition",
        "enemy.Recce",
        "enemy.AA",
        "enemy.IsHidden",
        "enemy.Stealth",
    )

    missing = [token for token in required if token not in code]
    leaks = [token for token in forbidden if token in code]
    if missing or leaks:
        details = []
        if missing:
            details.append("missing unit-weight contract: " + ", ".join(missing))
        if leaks:
            details.append("forbidden hidden detail read: " + ", ".join(leaks))
        _promote(results, "hidden-concentration-honesty", FAIL, "; ".join(details))
    else:
        _promote(
            results,
            "hidden-concentration-honesty",
            PASS,
            "static contract: each enemy contributes one unit; no strength/composition/AA/Recce/stealth reads",
        )


def _derive_existing_evidence(lines: Sequence[str], results: Dict[str, Result]) -> None:
    tracks: Dict[str, List[str]] = defaultdict(list)
    step_counts: Dict[str, int] = defaultdict(int)

    for line in lines:
        lower = line.lower()

        marker = ACCEPTANCE_RE.search(line)
        if marker:
            scenario = marker.group(1)
            status = marker.group(2).upper()
            _promote(results, scenario, status, line.strip())

        if "[ai][v2][recon][reaction]" in lower and "action=attackopportunity" in lower and "reason=exposed-weak-recce" in lower:
            _promote(results, "weak-recce-attack", PASS, line.strip())

        if "[ai][v2][recon][capture]" in lower:
            if "hiddenentry=true" in lower and "success=true" in lower:
                _promote(results, "hidden-facility-capture", PASS, line.strip())
            if "cancel" in lower and "reason=live-danger" in lower:
                _promote(results, "capture-cancel-live-danger", PASS, line.strip())

        step = STEP_RE.search(line)
        if step:
            army = step.group(1).strip()
            source = step.group(2).strip()
            destination = step.group(3).strip()
            if not tracks[army]:
                tracks[army].append(source)
            elif tracks[army][-1] != source:
                # A discontinuity means this log slice missed an intervening move. Start a fresh
                # continuity segment rather than manufacturing an A-B-A-B verdict.
                tracks[army] = [source]
            tracks[army].append(destination)
            step_counts[army] += 1

            positions = tracks[army]
            if (
                len(positions) >= 4
                and positions[-4] == positions[-2]
                and positions[-3] == positions[-1]
                and positions[-4] != positions[-3]
            ):
                _promote(
                    results,
                    "a-b-a-b-avoidance",
                    FAIL,
                    f"army={army} observed A-B-A-B sequence: {positions[-4:]}",
                )

    replanners = [army for army, count in step_counts.items() if count >= 2]
    if replanners:
        _promote(
            results,
            "per-step-replan",
            PASS,
            "multiple one-step plans observed for army/armies: " + ", ".join(sorted(replanners)),
        )

    if results["a-b-a-b-avoidance"].status != FAIL:
        long_tracks = [army for army, count in step_counts.items() if count >= 3]
        if long_tracks:
            _promote(
                results,
                "a-b-a-b-avoidance",
                PASS,
                "3+ consecutive one-step plans observed without A-B-A-B for: " + ", ".join(sorted(long_tracks)),
            )


def _print_report(results: Dict[str, Result]) -> None:
    width = max(len(name) for name, _ in SCENARIOS)
    print("Ground Recon Acceptance Suite")
    print("=" * (width + 30))
    for name, description in SCENARIOS:
        result = results[name]
        print(f"{name:<{width}}  {result.status:<12}  {description}")
        print(f"{'':<{width}}                evidence: {result.evidence}")

    counts = {status: sum(1 for r in results.values() if r.status == status) for status in (PASS, FAIL, NOT_OBSERVED)}
    print("-" * (width + 30))
    print(f"PASS={counts[PASS]} FAIL={counts[FAIL]} NOT_OBSERVED={counts[NOT_OBSERVED]}")


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Verify Ground Recon acceptance evidence in Unity AI logs.")
    parser.add_argument("logs", nargs="+", type=Path, help="AiDebug.log file(s) from a Ground Recon run")
    parser.add_argument(
        "--source-root",
        type=Path,
        default=Path(__file__).resolve().parents[1],
        help="repository root used for static honesty checks (default: repository containing this script)",
    )
    args = parser.parse_args(argv)

    results = {name: Result() for name, _ in SCENARIOS}
    try:
        lines = _read_logs(args.logs)
        _derive_existing_evidence(lines, results)
        _check_direction_honesty(args.source_root, results)
    except RuntimeError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1

    _print_report(results)
    if any(result.status == FAIL for result in results.values()):
        return 1
    if any(result.status == NOT_OBSERVED for result in results.values()):
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
