namespace Game.Ai.V2
{
    // 2026-10-07 (user decisions) — equipment/mutator value as a change in a unit's EFFICIENCY (E).
    // Design table and worked examples: docs/ai-v2-equipment-efficiency-table.md.
    //   E = Attack x1 + Defense x0.5 + HP x0.5 + Range x2 (range counts up to equipRangeCap).
    public static partial class AiConfigV2
    {
        // ---- base efficiency ------------------------------------------------------------------
        public const float equipWeightAttack = 1f;
        public const float equipWeightDefense = 0.5f;
        public const float equipWeightHitPoints = 0.5f;
        public const float equipWeightRange = 2f;
        public const int equipRangeCap = 4;               // range beyond this adds nothing
        // Defense/HP bonuses are worth more on a frail unit: x (reference / (D + HP)).
        public const float equipDefenseHpReference = 8f;

        // ---- ability multipliers (x host characteristic) ----------------------------------------
        public const float equipRegenerationFactor = 0.5f;   // x (D + HP): +1 HP per own turn
        public const float equipCriticalFactor = 0.5f;       // x A
        public const float equipSplashFactor = 0.5f;         // x A x targets
        public const float equipScorcherFactor = 0.5f;       // x A x Bio share
        public const float equipHyperkineticFactor = 0.5f;   // x A x Armored/vehicle share
        public const float equipPyrokineticFactor = 0.5f;    // x A x Bio share
        public const float equipShockFactor = 0.25f;         // x A x Initiative
        public const float equipCeramicFactor = 0.25f;       // x HP
        public const float equipAntiAirFactor = 1f;          // x A x air share of the known enemies
        // Share of the known enemy units an ability can hit while nothing is known yet.
        public const float equipDefaultArmoredShare = 0.5f;
        public const float equipDefaultBioShare = 0.5f;
        public const float equipDefaultAirShare = 0.2f;

        // ---- skills -----------------------------------------------------------------------------
        public const float equipStealthFactor = 0.5f;        // x A (hidden first strike)
        public const float equipHeroStealthFactor = 0.25f;   // x (D + HP): safe from air strikes
        // A host that is not a scout never enters stealth today (only scouts do): its Stealth is
        // worth only this share until an executor uses it elsewhere.
        public const float equipStealthUnusedShare = 0.25f;
        public const float equipRecceValue = 2f;             // r1sX: radius-1 vision + spot strength
        public const float equipRapidReactionValue = 3f;     // deploy 0 AP and 0 AP per move

        // ---- flat bonuses -----------------------------------------------------------------------
        public const float equipMoveFactor = 0.15f;          // x E_host x delta army speed x (3 / speed)
        public const float equipInitiativeFactor = 0.2f;     // x A x delta Initiative
        // 1 AP saved per activation: 2 E per AP x 3 turns x 0.5 share of turns the army moves.
        public const float equipActivationApValue = 3f;
        public const float equipHeroFateFactor = 0.5f;       // x mean army Attack, per Fate point
        public const float equipHeroArmyAttackDefault = 4f;  // when the hero is alone
        // +1 Fate of a facility operator lifts every later Challenge: ~+0.15 success x 3 outputs x 3 E.
        public const float equipOperatorFateGain = 1.4f;

        // ---- price of E ---------------------------------------------------------------------------
        // A unit costs ~0.5 AP per E; 1 AP = cardScorePerApEquivalent card units.
        public const float equipCardValuePerE = 0.075f;

        // ---- mission context (host already on a mission) ---------------------------------------------
        public const float equipAttackOffenseMult = 1.5f;
        public const float equipAttackDefenseMult = 1.25f;
        public const float equipRaidOffenseMult = 1.25f;
        public const float equipActiveDefenceDefenseMult = 1.5f;
        public const float equipScoutSkillMult = 1.5f;
        // Offense mult grows with the target hex's known defence bonus (Attack / Raid).
        public const float equipHexDefenseOffensePerPoint = 0.1f;

        // ---- supply: how scarce units are (Development production only) ---------------------------------
        // fraction = (cards in deck + hand) / equipSupplyReferenceCards. Production competes with a
        // plain unit at one third; with a full deck a unit is always the better buy.
        public const float equipSupplyReferenceCards = 35f;
        public const float equipSupplyFullFraction = 2f / 3f;   // at/above: multiplier 1
        public const float equipSupplyMidFraction = 1f / 3f;
        public const float equipSupplyMidMultiplier = 11f;
        public const float equipSupplyEmptyMultiplier = 15f;
    }
}
