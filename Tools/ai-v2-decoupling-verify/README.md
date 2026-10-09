# ai-v2-decoupling-verify — acceptance tools for the AI V2 layer decoupling (stages Э0–Э6)

Two small checkers over a **JSONL trace written by a test recorder** (not by game code and not by a
new game registry). Python 3 only; nothing here is imported by Unity (`Tools/` is outside `Assets/`).

```bash
python Tools/ai-v2-decoupling-verify/selftest.py                      # proves the checkers catch what they must
python Tools/ai-v2-decoupling-verify/compare_traces.py before.jsonl after.jsonl [--ignore f1,f2]
python Tools/ai-v2-decoupling-verify/check_boundaries.py run.jsonl
```

Exit codes: `0` ok, `1` violation / divergence, `2` bad input.

## Recorder, golden traces, coupling matrix

- Recorder: `Assets/Editor/AiDecouplingTrace.cs` (test-only, reads the ledger, writes JSONL when
  `AI_V2_TRACE_DIR` is set). Fixtures: `Assets/Editor/AiDecouplingBaselineTests.cs`.
- Golden traces: `golden/*.jsonl` — written on the stage's input revision. After a stage, rerun the fixtures with a new
  `AI_V2_TRACE_DIR` and `compare_traces.py golden/S1_bank.jsonl <new>/S1_bank.jsonl`.
- `coupling_matrix.py --rev <sha>` — the folder dependency matrix of `Ai/V2` and the exact foreign types of
  `Orchestration` (whole folder) and of `AiStrategyV2Pipeline.cs`; same method for every revision.

## Trace schema (one JSON object per line)

| Field | Meaning |
|---|---|
| `player`, `turn` | scope of the record |
| `work` | work kind (`Mission`, `MandatoryAviation`, `Tempo`, `Cold`, `Pass`, `Reentry`, `Recall`, ...) |
| `ordinal` | strictly increasing within a (player, turn) scope |
| `event` | `turn_start`, `take`, `consume`, `reentry`, `commit`, `terminal_force`, `income_cover_release`, `tempo_round_start`, spend / release / rollback events, ... |
| `ap`, `h`, `e`, `m`, `t` | caller-recorded SPENDABLE values at that point (before a guard); `null` when the fixture does not know them |
| `reserved` | `{ap,h,e,m,t}` held by the ledger at that point (always recorded) |
| `rows` | owner rows: `{owner, reason, resource, amount, expiry}`; `expiry` is a stage name (`EndOfTurn`, ...) or a turn number (R6 checks only numbers) |
| `operation_keys`, `actors` | operation keys and actor ids touched |
| `world_revision`, `knowledge_version`, `pathing_version`, `scope_id` | cache and revision coordinates |
| `consumed`, `pending` | pending / consumed fact reasons; `take_id` links take → consume → reentry |

A game log that lacks these fields is **not** a numeric comparison; use it as a smoke check only.

## What `check_boundaries.py` enforces

R1 ordinals increase · R2 a world revision is committed once · R3 every row has owner and reason ·
R4 take → consume → reentry order · R5 income cover released once, after `terminal_force` and before the
first `tempo_round_start` · R6 no expired row visible at the next `turn_start`.

## Limits

`selftest.py` shows that five injected defects (swapped consume/reentry, erased owner, second commit,
income-cover release moved past the first tempo round, expired view at the next turn) give exit 1, and
that a good trace gives exit 0. It does not show that a future recorder emits correct data.
`compare_traces.py` compares records in order, so a different spend order is a divergence even when the
final numbers match. Row order inside one record is ignored.
