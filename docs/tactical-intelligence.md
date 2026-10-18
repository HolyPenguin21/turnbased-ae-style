# Tactical intelligence

`TacticalIntelligence` is a passive capability, independent of card names. The neutral
`Tactical Center` facility grants it. `PlayerDataPanelUI` checks the human player's
in-play capability sources through `IncomeProjection.CountInPlayAbilitySources`:
units/heroes in non-prison armies, owned buildings and their placed facilities.
Cards in hand/deck grant no access. Losing the last source hides the Intel button,
closes the report and clears its text. Capturing a base transfers its facilities' access.
Multiple sources provide redundancy, without adding coverage.

The report uses the existing vertical ScrollRect, with a plain `PlayerDataScrollbar`
(track, sliding area and handle) available for manual styling in the Game scene.
The scrollbar hides when the report fits. Opponents are sorted by visible-field count
descending; ties preserve the original match order. Sorting refreshes with coverage.

Only opponents are listed; neutral players and the observer are omitted. Name, faction
and elimination status are public. The sixteen private fields are:

1. Deployed ground power.
2. Strike army potential (`PlayerForceAnalysis.AttackPeak`, including its existing exclusions).
3. Strongest field army power.
4. Strongest field army size/capacity.
5. AI attack operation phase, without its target.
6. Ground field army count, including scouts and lone heroes.
7. Ground troop count, excluding heroes and aircraft.
8. Aircraft count, including stored aircraft.
9. Deployed hero count, excluding prisoners.
10. Base count, including the citadel.
11. Placed facility count.
12. Original citadel garrison power.
13. Human stock and projected income.
14. Energy stock and projected income.
15. Materials stock and projected income.
16. Tech stock and projected income.

A resource's stock/income pair is one indivisible field. Ground power and army potential
reuse the current analysis, and income reuses the real income allocator. Report reads
never create hands, draw cards, create mission state, reserve resources or update AI plans.
`PlayerForceAnalysis` now uses `MissionIntentRegistry.Peek` for that reason.

## Coverage and report rotation

Each opponent uses the minimum geometric hex distance from the observer's nonempty
ground field armies to that opponent's original citadel coordinates. Hidden scouts and
lone heroes qualify. Garrisons, prisons, airfields, carrier containers, aircraft, dead
units, prisoners and temporary summons do not qualify. Armies do not add coverage:

| Distance | Coverage | Visible fields |
|---|---|---|
| 0 | 100% | 16 |
| 1 | 75% | 12 |
| 2 | 50% | 8 |
| 3 | 25% | 4 |
| 4+ / no observer / unknown citadel | 0% | 0 |

A deterministic shuffle uses match identity, player-list slots and the observer's personal
turn. Increasing coverage reveals a longer prefix of the same order; decreasing coverage
hides the surplus fields as `???`. Reopening, rebuilding a center or moving back and forth
does not reroll the report. The personal turn is derived from `CompletedTurnsFor`: ending
the observer's turn keeps the old report until their next turn starts. There is no new
registry/cache or history to serialize; restoring those existing identity/turn inputs
reproduces the mask. Visible values refresh from current sources once per second.

Even 100% coverage does not print army names/coordinates, deck names, individual rosters,
attack targets or open map objects behind fog. A future human opponent has no AI intent;
its attack-operation field displays `unavailable`.

## Verification

`TacticalIntelligenceTests` covers distance tiers, eligibility, nesting, deterministic
rotation, field distribution, generic ability sources, ownership, multiple centers and
read-only force analysis. `TacticalIntelligencePanelTests` requires Unity and covers the
authored Tactical Center, button/report lifecycle, hidden data, aircraft counting and
personal-turn report rotation.

Before merge, run the full EditMode suite with Unity 6000.5.4f1, then check the Game scene:
place a Tactical Center; move a ground army through all distance tiers; reopen the panel;
end the human turn; destroy/remove/capture the last ability source while the panel is open.
Confirm that long reports still scroll correctly at the project's target resolutions.

Container validation uses the repository's stub references and temporary compatibility
adapters, not a real Unity build. All four edited YAML files pass both `verify_full.js`
and `verify_types.js`. No images are added or changed.
