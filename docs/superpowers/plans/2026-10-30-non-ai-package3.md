# Package 3 combat ownership implementation plan

> **For agentic workers:** Use superpowers:executing-plans or subagent-driven-development; track completion below.

**Goal:** engine/session own combat decisions, attack plans and world application independently of presentation.
**Architecture:** extend existing BattleEngine, challenge sessions, BattleEncounterCoordinator and ExplosionAttack; compatible public adapters. No AI policy/balance/save schema/assets/dependency changes.
**Tech Stack:** Unity 6000.5.4f1, C#, NUnit.
**Spec:** user package3 3.1–3.5 in this conversation.

## Global constraints
- Reuse package1 world owners and package2 ProductionAttempt/card owners.
- Preserve Bodyguard, collateral, Shock, heal, disruption, summons, winner-before-stripping and prison rules.
- Actor/phase/action, reactions and turn advancement cannot be bypassed by repeated callbacks.
- Apply world consequences and acknowledgment are separate, each idempotent. Freeze authored/environment rules and accepted attack plan.
- Same decisions and rolls finish sessions identically with and without UI; local Fate survives terminal paths.
- Preserve entrypoint compatibility; no AI policy edits. Verification stubs remain outside game code.

## Review focus
- Stale callbacks across action/reaction/next encounter and replacement R/P sessions.
- Hero+summons versus summons-only, retained heroes requiring Capture/Kill before finalize.
- Cancel/disable/exception and unrelated live Fate updates.
- Stable preview RNG and single-use expenditure in every public attack adapter.
- Missing AA popup must run admitted fire; cleanup during movement and cancellation.

### Task 1: Research challenge lifecycle (3.2)
- [x] Tests for local Fate and all terminal/reentrant callback paths.
- [x] Session owns roll/spend/tactics/accept/frozen result/cancel. Shared headless/popup decision primitives.
- [x] ProductionAttempt owns completion. Close old UI before callback; stale callback cannot cancel replacement.
- [x] Focused verification and independent review.

### Task 2: Ground commands and plans (3.1/3.4)
- [x] Wrong actor/phase, repeat hit, counterattack, stable plan tests.
- [x] Engine phase/action commands; protected state progression and one main action.
- [x] Immutable rules snapshot and attack plan, shared Bodyguard/collateral projection; RawHit vs PositiveDamage semantics.
- [x] UI adapters use engine commands/plans; all public apply adapters include reaction/launcher completion.

### Task 3: Encounter lifecycle (3.3)
- [x] Finalization/ack idempotency and mandatory hero Capture/Kill tests.
- [x] Coordinator applies casualties/summons/retreat/capture/prison/guards via package1 owners before acknowledgment.
- [x] Continue only once after acknowledgment; preserve all original winner/guard/retreat/delayed paths.

### Task 4: Aviation operation (3.5)
- [x] AA fire/skip/parity/duplicate/cancel tests.
- [x] Gameplay operation yielding decision requests; shared supplied session and headless adapter, presenter only renders.
- [x] Public adapters and existing explosion lifetime preserved.

### Task 5: Verification and contracts
- [x] Full-source managed compilation and differential regressions against bf0a4ec.
- [x] Independent bottom-up review; correct concrete findings and verify.
- [x] Document contracts, decisions and checks still requiring Unity.

- [ ] Full native EditMode and affected interactive scenarios in Unity 6000.5.4f1 (editor unavailable).

Contracts and current evidence: `Docs/NonAI_Package3_Contract.md`.
