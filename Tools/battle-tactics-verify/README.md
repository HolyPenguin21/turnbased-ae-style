# Tactical participation audit

`ScenarioAudit.cs` is an opt-in NUnit diagnostic fixture. It compares tactical actions on
authored Concord/Ashen stats without loading scenes or faction assets. It is outside
`Assets`, so normal Unity imports and builds do not include it.

For a native Unity comparison, copy it temporarily to
`Assets/Editor/Tests/Combat/BattleScenarioAudit.cs` in an isolated checkout of each revision.
Use Unity 6000.5.4f1 and select
`Game.EditorTests.BattleScenarioAudit.ExportMatchedTacticalScenarios` in EditMode tests.
Set these environment variables before launching the editor/test runner:

```text
TACTICS_OUTPUT=/absolute/path/to/before-or-after.tsv
TACTICS_SEEDS=8
```

`TACTICS_CASE=20` optionally restricts the run to one numbered scenario. With no
`TACTICS_OUTPUT`, the fixture is ignored. Remove the temporary imported source and its
generated `.meta` after the diagnostic run.

The fixture exports one TSV row per battle and a companion `.trace.txt` for seed 0.
All attacks and moves are executed through the live battle engine and checked for
legality. Both armies are committed to fighting; retreat is deliberately outside this
diagnostic. A twelve-round limit bounds stalemates. The round-controller guard fails
the test if the diagnostic itself stops making progress.

`wastedMoves` uses an independent BFS to count steps that do not shorten a free route
to a cell from which the actor can hurt an enemy. `avoidablePasses` excludes an
engaged ally's Heal/Drill/Bodyguard support; `supportHolds` records those separately.
`impossibleShots` counts attacks whose maximum primary damage is zero. None of these
metrics alone proves optimal play.

See `docs/battle-tactics-before-after.md` for the full comparison, environment limits,
test results, and compositions. The portable verification adapters used for that report
were kept outside game code. Re-run the full EditMode suite and inspect the opening of
large battles in Unity before merging.
