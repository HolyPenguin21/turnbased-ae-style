namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Materialization — Strategic Manager (Phase A/B card play + capability preparation), AI-MGR-02 end-of-turn tempo arbiter, AI-MGR-01 strategic card evaluator.
    public static partial class AiConfigV2
    {
        // =======================================================================================
        //  STRATEGIC MANAGER  (Strategy V2 — centralized card play + capability preparation)
        //  NOT a DesireAxis and NOT radar-sliced. Two phases:
        //    Phase A — FulfillDemands: before mission planning, satisfies AxisDemands with cards.
        //              AP is charged to demand.RequestingAxis via the shared AxisBudgetLedger.
        //    Phase B — UseSurplus: after mission execution, spends GENUINELY remaining real
        //              AP/resources on proactive preparation + hand cycling. No radar slice.
        //  Both play cards ONLY through V2 CardPlayExecutor (the single authoritative V2 path).
        // =======================================================================================
        // Phase A — hard safety bound on demand-fulfilment card plays per AI turn.
        public const int maxDemandFulfillmentActionsPerTurn = 3;

        // Card-candidate scoring, shared by Phase A + Phase B.
        //   costFactor  = 1 + stratCardApCostWeight * plan.TotalApCost   (higher AP -> lower score)
        //   trait bonus = a flat add when a demand's preferred trait (e.g. Stealth) is on the card
        //   TargetFit   = 1 at the demand's TargetHex, decaying linearly to 0 at stratTargetFitRange
        public const float stratCardApCostWeight = 0.15f;
        public const float stratTraitMatchBonus = 0.35f;
        public const int stratTargetFitRange = 10;

        // GRADED placement preference (add to the candidate score). Mirrors V1's card-placement
        // principle — fill an existing suitable army / the garrison before founding a new one:
        //   Garrison  >  Existing suitable army  >  Reusable empty shell  >  new army (0).
        // A solo (Recce / ScoutCapability) card is shell-or-new only, so the top two never apply
        // to it. Garrison respects garrisonReservedSlots (PlacementRules.CanDepositIntoGarrison).
        public const float stratPlacementGarrisonBonus = 0.30f;
        public const float stratPlacementExistingArmyBonus = 0.20f;
        public const float stratPlacementReusableShellBonus = 0.10f;

        // ---- Step 8B: generated cards + equipment chains ---------------------------------
        //  StrategicManager reasons about the COMPLETE action chain (MaterializationPlan): at most
        //  one Research/Production generation step + one Equipment attachment + one final deploy.
        //  RequiredTraits stay a hard feasibility gate on the projected end result; these knobs
        //  only shape RANKING between feasible chains — a cheap sufficient Direct must still be
        //  able to win against a generation / equipment chain (spec §30 / AC #36).
        //   costFactor += stratChainResCostWeight * Σ(chain R/H/M/T)
        //   score      -= per-extra-step penalty (attach / generation)
        //   generated contingent benefit is multiplied by the real Challenge success chance;
        //   certain resource-efficiency terms stay certain.
        //   score      -= stratChainScarcityPenalty   when a chain would spend a unique Stealth item
        //                                              on a Demand that does not require Stealth
        public const float stratChainResCostWeight = 0.05f;
        public const float stratChainAttachStepPenalty = 0.08f;
        public const float stratChainGenerationStepPenalty = 0.15f;
        public const int stratChainStealthScarceAt = 1;   // StealthScouts <= this -> preserve a unique Stealth item
        public const float stratChainScarcityPenalty = 0.40f;
        // Generalized scarcity (spec §5): a Hero body spent on a non-Hero, non-Scout demand while
        // no free deployed hero exists. Scout demands carry their own contextual hero-opportunity
        // term (scoutQualityHeroOppCostMax) and are excluded here to avoid double-counting.
        public const int stratChainHeroScarceAt = 0;      // AvailableHeroes <= this -> the Hero body is a live bottleneck
        public const float stratChainHeroScarcityPenalty = 0.30f;

        // Hard bound on Research/Production Challenges the Strategic Manager may ATTEMPT per AI turn.
        // ONE shared counter across Phase A demand fulfilment AND the end-of-turn tempo arbiter
        // (materialization surplus + non-combat surplus) — enforced through
        // MaterializationReservation.GenerationAttemptsUsed, which every generation path increments
        // via RecordGenerationAttempt. There is NO second per-turn generation budget anywhere.
        // This is a runaway-loop safety bound, not a policy gate. Phase-A/Phase-B portfolio
        // feasibility consumes real AP and H/E/M/T for every attempt, so available resources decide
        // whether one, two or three Challenges are actually selected.
        public const int maxGenerationActionsPerTurn = 3;

        // Phase B may proactively generate / attach with GENUINELY remaining resources, behind
        // every existing reserve + the Phase-A generator claim. Bounded, never a production planner.
        public const bool surplusAllowGeneration = true;
        public const bool surplusAllowAttach = true;
        public const float surplusAttachTraitBonus = 0.30f;   // added when a proactive attach grants a scarce trait

        public const bool surplusAllowDraw = true;
        // AI-MGR-02 — SEMANTIC sub-cap: the max number of end-of-turn tempo *draws* per turn. The
        // unified loop honours this exactly like the old terminal-draw stage did; a draw beyond it
        // is not offered as a candidate.
        public const int maxTerminalDrawsPerTurn = 4;
        // Generic (no-residual) combat surplus into an already-saturated garrison must clear
        // this absolute shared-score floor. It is a local garrison-cap policy, not a second global
        // Phase-B admission threshold; all alternatives still reach the common arbiter.
        public const float garrisonSaturatedMinUtility = 3.60f;
        // The garrison counts as "already strong enough" for the cap above once its EffectivePower
        // is at least this fraction of the player's best assemblable stack (BestStackPotential).
        public const float garrisonSaturatedReserveFractionOfBestStack = 0.60f;
        // NOTE: the old speculative Phase-B floors (surplusApReserve / surplus{Human,Energy,
        // Materials,Tech}Reserve) are RETIRED. Phase B runs AFTER ordinary mission execution;
        // AP cannot be banked and there is no resource/AP-costing late V2 stage (housekeeping is
        // zero-AP by invariant). StrategicManager.ReservesOkAfterChain now protects only the REAL
        // remaining pool. A future late stage that genuinely needs resources after Phase B must
        // add its own explicit V2 reservation contract rather than reviving a fixed floor here.

        // =======================================================================================
        //  AI-MGR-02 — END-OF-TURN TEMPO ARBITER
        //  StrategicManager.UseSurplus is one bounded live-arbitration loop. Every end-of-turn
        //  decision (PlayCard / DrawCard / strategic spend / HoldResources / EndTurn) is a candidate
        //  in ONE comparable utility space; PlayCard utility is the StrategicCardEvaluator NetScore
        //  verbatim (no caller-side correction — spec §5). The loop stops when max(Hold, EndTurn) >=
        //  the best actionable spend, or the hard action bound is hit.
        // =======================================================================================
        // Hard bound on the unified arbitration loop (spec §3). Also bounds materialization +
        // non-combat plays + draws + strategic spends TAKEN TOGETHER — no lane gets a reserved slot.
        public const int maxEndOfTurnTempoActionsPerTurn = 10;
        // A spend candidate must beat BOTH this floor AND max(Hold, EndTurn). Small and positive:
        // stranded AP is used for a marginally-useful action, but a genuinely worthless one still
        // loses to EndTurn (spec §4 — "high SpendPressure is not play garbage at any cost").
        public const float tempoMinSpendUtility = 0.05f;
        // AI-MGR-02 §P0 (round 4) — HoldResources is NOT a global "stop everything" gate. AP is
        // never held, so an AP-only action (Draw, AP-only Pressure) is COMPATIBLE with keeping the
        // persistent pool and competes only against EndTurn / tempoMinSpendUtility. The H/E/M/T
        // A concrete non-card spend is priced from its exact cost vector by the same canonical
        // StrategicCardEvaluator resource-opportunity model as card plays. PlayCard remains exempt
        // from any Phase-B adjustment: its NetScore already owns HoldValue / ScarcityValue /
        // ResourcePressureBenefit. The constants below retain only the whole-pool scarcity signal
        // shown on the diagnostic `policy Hold(full pool)` line; it is not an execution stop gate.
        public const int   tempoHoldResourceComfortableStock = 8;    // a resource at/above this is not "scarce"
        public const float tempoHoldFragilityWeight = 0.5f;
        public const float tempoHoldScarcityWeight = 1.0f;
        public const float tempoHoldPersistentResourceValueScale = 1.6f;
        public const float tempoHoldPersistentResourceValueCap = 2.5f; // a strong play (NetScore > this) still wins
        // AI-MGR-02 §P1 (round 4) — STRATEGIC OVERSTOCK relief, per resource. The game has NO hard
        // resource storage cap (PlayerRoot.AddResource is unbounded), so nothing is physically lost
        // and this is NOT "overflow". It only expresses: a resource held far above the deck's
        // sustainable runway need is worth LESS to keep, so a spend that consumes it is penalised
        // less. runwayTarget = max(comfortableStock, IncomeTarget[r] * tempoHoldOverstockRunwayHorizon);
        // overstock = max(0, (stock + PerTurnIncome[r]) - runwayTarget); summed per resource, floored
        // at 0 each so a scarce resource cannot re-inflate another resource's relief.
        public const float tempoHoldOverstockRunwayHorizon = 6f;     // turns of IncomeTarget that define the runway target
        public const float tempoHoldOverstockReliefWeight = 0.20f;   // per overstock unit, subtracted from diagnostic Hold
        public const float tempoHoldOverstockReliefCap = 3.0f;       // max total overstock relief
        // DrawCard candidate utility, same [~0..5] NetScore band as PlayCard (spec §1 — Draw is a
        // full peer, not a terminal fallback, and is NOT penalised for holding H/E/M/T it does not
        // spend — Draw costs only AP).
        public const float tempoDrawBaseValue = 0.45f;              // floor value of a fresh option even from a weak deck
        public const float tempoDrawDeckValueWeight = 0.9f;         // * normalised mean remaining-deck strategic card value
        public const float tempoDrawDeckValueNorm = 24f;            // strategic card value that maps to a full unit of deck value
        public const float tempoDrawThinDeckTaperCards = 3f;        // deck at/under this many cards tapers expected value linearly
        public const float tempoDrawEquipmentValue = 12f;          // generic strategic value of an unseen Equipment card
        public const float tempoDrawInfraValue = 14f;              // generic strategic value of an unseen Base / Facility card
        public const float tempoDrawEffectRoleValue = 3f;          // per generic strategic role a unit card would cover (AoE/Regen/Aura/Summon/…)
        public const float tempoDrawFillFloor = 0.55f;             // fill factor with only one free slot (was ~0.33 -> near-auto-reject at 9/10)
        public const float tempoDrawFillPerSlot = 0.15f;           // + this per free slot, clamped to 1
        public const float tempoDrawApOpportunityWeight = 0.04f;   // per AP the draw costs
        public const float tempoDrawFutureBlockPenalty = 0.10f;    // drawing into the last free slot (softened — 9/10 is a legal draw)
        public const float tempoDrawHandActionableWeight = 0.12f;  // * best CURRENTLY-SELECTABLE play NetScore, subtracted (hand already actionable NOW)
        // A draw that preserves the last hand card before its best play would strand too little AP
        // for a follow-up draw gets this option-continuity value. This is utility, not a gate:
        // genuinely strong/urgent plays can still win the common Phase-B arbitration.
        public const float tempoDrawLastCardContinuityBonus = 0.85f;
        // Utility of a ready decisive structure-pressure advance (StrategicPressureAdvance), in the
        // shared band. It fires only in the narrow "no enemy contact, known citadel, saturated
        // military" fallback, so a modest fixed value is enough for it to beat Hold/EndTurn there.
        public const float tempoPressureAdvanceValue = 1.20f;
        // AI-MGR-02 — StrategicMaintenancePolicy enumerates only genuinely non-card strategic
        // actions (Base/Citadel slot-capacity upgrades). Their utility is not configured as a fixed
        // band: it is the concrete Facility's dynamic StrategicCardEvaluator TotalUseScore minus
        // the upgrade AP opportunity cost; Phase B then subtracts the tier's exact marginal H/E/M/T
        // opportunity cost on the same evaluator scale. Facility placement, Equipment attach and
        // Research/Production generation remain ordinary PlayCard candidates through that single
        // evaluator (spec §5, one scorer).
        // spec §7 (round 4) — the reaction pass reserves a BOUNDED AP BUDGET for its same-turn
        // replan, not an exact action cost (the replan re-runs the whole Demand→Mission→Provision
        // pipeline and picks its own action, so there is no single pre-planned action to price).
        // This is the ceiling on that budget; the estimate feeding it may legitimately exceed it —
        // the replan is simply bounded to this many AP.
        public const int reactionReserveApCap = 6;
        // spec §7 — AP estimate for a hand-only (no discovered target) reaction follow-up when the
        // hand carries no priced card to sample; a single modest replay.
        public const int reactionFollowupApEstimate = 2;
        // spec §7 — extra AP beyond a responder's activation cost folded into the reaction estimate
        // for the one repositioning step a bounded replan typically makes.
        public const int reactionResponderMoveApEstimate = 1;
        // AI-MGR-02 §7 — kept for HousekeepingManager's same-turn tempo re-run after a released
        // reservation. UseSurplus is itself the bounded loop; this only caps the re-run repeats.
        public const int maxEndOfTurnTempoReruns = 1;

        // =======================================================================================
        //  STRATEGIC CARD EVALUATOR  (AI-MGR-01 — the shared Card x IntendedUse model used by BOTH
        //  StrategicManager phases). Replaces ScorePlanA's cost/fit product and SurplusUtility's
        //  additive sum with one breakdown (RoleFit / ImmediateTempo / NextTurnPotential /
        //  CapabilityGapValue / ForceGrowthValue / ThreatResponseValue / ResourceEfficiency /
        //  SynergyValue / Deployability / ScarcityValue / RedundancyPenalty / AlternativeUseValue /
        //  HoldValue / ResourcePressureBenefit / HandPressureBenefit). The Hero card CLASS adds no
        //  flat bonus or penalty — hero fitness is HeroRoleEvaluator's characteristic score, and
        //  the only hero cost is AlternativeUseValue when a scarce hero is spent off its best use.
        //  First-pass; tune against real AiDebug.log strat.eval lines.
        // =======================================================================================
        // ForceGrowthValue = SurplusCombatReadinessUtility (marginal AiPower, [0..2]) * Lerp(
        //   baselineReadinessGrowthFloor, 1, BaselineForceReadiness.Need) * this. Keeps an ordinary
        //   combat body worth materialising at AGG = 0 / DEF = 0 without out-bidding a real demand.
        public const float forceGrowthValueWeight = 0.60f;
        // Flat bonus for a card that closes a capability the AI currently lacks ENTIRELY (0 scouts,
        // 0 heroes, no field body). Same magnitude band as surplusScarcityMed.
        public const float capabilityGapValue = 0.50f;
        // ThreatResponseValue (AntiArmor / AntiAir roles only) = clamp(enemyDriverPower / norm) * weight.
        // Uses omniscient enemy power as a DIRECTIONAL strategic bias; never becomes normal AI intel.
        public const float threatResponseNorm = 40f;
        public const float threatResponseValueWeight = 0.30f;
        // NextTurnPotential — a fresh independent actor (new army / reusable shell) opens next turn.
        public const float nextTurnActorPotential = 0.15f;
        // A non-recce body with at least this moveMax also offers the MobileCombat role.
        public const int mobileCombatMoveMax = 5;
        // Hero fitness for a field-command role: HeroRoleEvaluator-style leadership score / norm,
        // clamped to cap. A weak hero scores low; there is NO flat hero bonus.
        public const float heroLeadershipFitNorm = 8f;
        public const float heroLeadershipFitCap = 1.20f;
        public const float heroSupportFitValue = 0.30f;   // a Researcher/Assembler hero evaluated for the Support role
        // HoldValue (spec §3) parts.
        public const float holdUniqueRoleValue = 0.40f;    // a rare stealth body / a support hero while a combat leader is already fielded
        public const float holdScarcityValue = 0.25f;      // the card carries a scarce capability (SurplusScarcity >= med)
        public const float holdHandPressurePenalty = 0.50f;// a full hand argues against holding
        public const float holdLostTempoPenalty = 0.35f;   // Phase B — not playing now forfeits this turn's tempo
        public const float holdNearTermDemandValue = 0.30f;// P1.6 — a specialist counter whose triggering threat is already visible is worth keeping ready
        // review-r4 P1 ARCH — StrategicEffectRegistry tunables (ability -> strategic value). AntiAir
        // / AntiArmor reuse capabilityGapValue, Support reuses surplusRecurringApIncomeBonus /
        // heroSupportFitValue (parity with the old inline SupportRoleFit); only these two are new.
        public const float effectMobileBaseFit = 0.20f;    // a fast non-recce body's MobileCombat role-fit floor
        public const float effectCriticalDamageFit = 0.35f;// §3.5 acceptance row — a CriticalDamage (x2-on-hit) body's CombatBody role-fit bonus
        public const float effectSplashFit = 0.30f;        // Splash — CombatBody AoE fit (scaled by TargetDensity x magP)
        public const float effectScorcherFit = 0.20f;      // Scorcher — narrower Bio-gated AoE fit
        public const float effectRegenerationFit = 0.25f;  // Regeneration — CombatBody ExpectedSustain fit
        public const float effectRaiseTheRotsFit = 0.35f;  // RaiseTheRots — ForceGrowth FreeBattleSlots fit
        public const float effectProduceResourceFit = 0.30f;// Produce{Human,Energy,Materials,Tech} — maximum strategic value scale for one +1/turn source
        public const float effectRecurringFloor = 0.40f;   // RecurringResource context: multiplier at a secure economy
        // Contextual-scaler norms for the currently-unused effect contexts (ready for AoE / regen /
        // aura mechanics — a value at/above the norm gives the effect its full BaseFit).
        public const int effectTargetDensityRadius = 3;    // hex radius around the deploy hex counted for AoE target density
        public const float effectTargetDensityNorm = 4f;   // SUPERSEDED by effectAoeBodiesNorm — enemy ARMY count, kept for the LocalEnemyArmies field
        public const float effectSustainHpNorm = 8f;       // projected HP for a regen effect to reach full value
        public const float effectAuraAllyNorm = 3f;        // eligible allies in the dest army for an aura to reach full value
        // final closure §3 — richer AoE / regen context signals (still all inert until a real
        // Splash / Regenerate row is added to StrategicEffectRegistry.ByAbility).
        public const float effectAoeBodiesNorm = 6f;       // expected AFFECTED BODIES (KNOWN enemy unit count near the deploy, or friendly bodies for a DestArmy-scoped nova) for an AoE effect to reach full value
        public const float effectCombatRoundsPowerRatio = 1.5f;// KNOWN-enemy-to-own power ratio near the deploy that adds one expected extra combat round
        public const float effectCombatRoundsMax = 5f;     // cap on the expected-combat-duration proxy
        public const float effectSustainRoundsNorm = 3f;   // usable regen rounds (min of expected duration and the effect's DurationRounds) for the regen duration factor to reach full value
        public const float effectNoCombatTimingFloor = 0.25f;// value multiplier for a DuringCombat effect when no fight is expected at the deploy
        public const float effectSummonDurationNorm = 3f;  // DurationRounds for a temporary Summon's duration factor to reach full value (0 DurationRounds = permanent = full)
        public const float effectStackingDiminishFactor = 0.5f;// EffectStacking.Diminishing: each extra identical copy is worth this fraction of the previous

        // === AI-MGR — DYNAMIC STRATEGIC EFFECT UTILITY ======================================
        //  A PlayerGlobal / Persistent / RecurringResource effect (ApBonus is the first) is NO
        //  LONGER a flat "+0.75 because the ability is present". Its value is
        //     perTurnValue x yield x (horizon x futureOpportunity) x marginalApUtility
        //         x carrierPersistence x saturation
        //  (generation risk is owned once by StrategicCardEvaluator.Deployability, never here)
        //  computed from snapshot-pure state (SelfSnapshot.ApEconomy). All descriptor-driven — a
        //  new global recurring effect (Energy/turn, draw/N turns, movement budget) is one more
        //  StrategicEffect row, no evaluator edit. Meant to be tuned against real AiDebug runs.
        //  effectRecurringHorizonTurns is DEDICATED — never reuse tempoHoldOverstockRunwayHorizon
        //  (that constant owns persistent-resource STOCK retention; coupling the two would make an
        //  economy tweak silently move Hank / Base ApBonus valuation and vice-versa).
        public const int   effectRecurringHorizonTurns         = 8;    // bounded pay-back horizon for a persistent/recurring effect (no authoritative game end)
        public const float effectRecurringOpportunityFloor     = 0.35f;// futureOpportunity never drops below this — an EARLY source is worth at least this share of the horizon
        public const float effectGlobalRecurringApPerTurnValue  = 0.06f;// strategic RoleFit units earned per +1 usable AP/turn, per horizon turn
        public const float effectGlobalRecurringValueCap        = 1.6f; // hard cap on ONE global recurring effect's contribution
        public const float effectStockpileMarginalUtilFloor     = 0.15f;// a currently-secure H/E/M/T income source retains only option value
        public const float effectRecurringSourceDiminish        = 0.72f;// each ApBonus source ALREADY in play multiplies the next one's value by this (diminishing multi-source)
        public const float effectRecurringRealisationFloor      = 0.30f;// carrierPersistence = Lerp(floor, 1, carrierDurability) — generation risk lives in StrategicCardEvaluator.Deployability
        public const float effectRecurringCarrierDurabilityUnit = 0.80f;// a recurring source riding a Unit body is less certain to persist than one on a Base/Facility
        public const float effectRecurringCarrierDurabilityHero = 0.90f;// ...a Hero is between a Unit and infrastructure
        public const float effectRecurringLateStageWeakWeight   = 0.25f;// how much the WEAK turn-number fallback is allowed to pull futureOpportunity down late
        public const int   effectRecurringStageRampLo           = 6;   // turn at/under which the weak late-stage fallback contributes 0
        public const int   effectRecurringStageRampHi           = 40;  // turn at/over which it is fully applied
        // futureOpportunity blend weights (state-driven, deliberately NOT monotonically rising with turn number)
        public const float effectRecurringOppForceRoomWeight    = 0.35f;// room left to grow standing force (1 - power/potential)
        public const float effectRecurringOppMapRoomWeight      = 0.25f;// explorable unknown map fraction still to be discovered by walking
        public const float effectRecurringOppActionRoomWeight   = 0.25f;// current marginal AP utility (if AP sits idle now it likely will later too)
        public const float effectRecurringOppLateFallbackWeight = 0.15f;// the weak turn-number fallback

        // SelfSnapshot.ApEconomy — marginal AP utility (how valuable ONE more AP/turn is RIGHT NOW).
        // action-economy driven, NOT H/E/M/T EconomicSecurity: an AI with a perfect economy but 4
        // armies, live recon, Development and a full hand still binds on AP.
        public const float apMarginalUtilRampLo = 0.60f;   // usefulApDemand / apAvailable at/under this -> one more AP is worth ~nothing (AP regularly idle)
        public const float apMarginalUtilRampHi = 1.20f;   // ...and at/over this -> fully valuable (AP is the binding constraint)
        public const float apMarginalUtilFloor  = 0.10f;   // marginalApUtility = Lerp(floor, 1, ramp) — a tiny residual value always survives
        public const float apDevActionApProxy   = 1f;      // AP the AI could still usefully spend on a Development action this turn
        public const float apAirSortieApProxy   = 1f;      // AP per available recon-air sortie folded into useful AP demand
        // Structural-fallback path only (a call with no owner-witnessed workload — sims, bare tests,
        // a plan-less non-combat lane with no surplus set): the ApActionEconomySnapshot structural
        // demand is an UPPER BOUND, never proof every action is useful, so the fallback demand is
        // scaled down. See StrategicEffectRegistry.StructuralFallbackApDemand.
        public const float apStructuralDemandConfidence = 0.60f;
        // Flat (Phase B / surplus) EstimateLegalApWorkload subset search: a signature-deduped pool at
        // or under this size gets an exact 2^N sweep; a larger (pathological) pool falls back to an
        // AP-descending greedy admission. Never a "cheapest N" pre-prune — that would drop exactly
        // the expensive AP opportunities the measurement exists to surface.
        public const int   apWorkloadExactSearchMax = 18;

        // Hero Command marginal-capacity valuation — REPLACES commandRating * heroRoleCommandWeight
        // inside HeroLeadershipFit. Extra Command is only worth something when the AI actually has
        // bodies to fill the slots it unlocks (canonical CardPlayExecutor.ProjectedCapacityAfterDeploy).
        public const float heroCommandMarginalSlotValue = 0.9f;// value of ONE extra battle slot this hero's Command unlocks AND the AI can fill
        public const int   heroCommandMarginalMaxSlots  = 4;   // cap on counted extra slots

        // review-r4 finding 6 — the two Hold terms spec §3 lists but the impl was still missing.
        public const float holdComboPreservationValue = 0.30f;// a still-available combo partner (equipment in hand fitting this body) makes the bare play forfeit a stronger combined play
        public const float holdResourcePressurePenalty = 0.35f;// a secure economy (resources at risk of capping / cheaply replenished) lowers the value of hoarding by holding the card

        // --- Review follow-up P1.4/P1.5/P1.6/P0.2 tunables ------------------------------------
        public const float scoutBaseRoleFit = 1.0f;            // Phase-B Scout RoleFit base, before the CapabilityQualityEvaluator multiplier
        public const float roleVersatilityPerExtraRole = 0.12f;// value per real viable role beyond the first (NOT a Hero class bonus)
        public const float roleVersatilityCap = 0.40f;
        public const float altUseForegoneFraction = 0.25f;     // AlternativeUseValue = this * next-best PLAY role score (Hold is priced only in NetScore)
        public const float stratHoldBeatsPlayMaxDemandValue = 40f; // legacy — superseded by the urgency ramp below
        // P0.2 review-r2 — Phase A ranks by NET decision value (play - hold + urgency) and plays
        // only when it is positive. Urgency ramps with the demand's Value so a real threat / raid
        // gap materialises even against a high-HoldValue card, while a soft baseline demand adds
        // ~nothing and can genuinely lose to Hold.
        public const float stratHoldUrgencyRampLo = 25f;   // demand Value at/under this -> urgency 0
        public const float stratHoldUrgencyRampHi = 60f;   // demand Value at/over this -> full urgency
        public const float stratHoldUrgencyMax = 2.0f;     // full urgency bonus added to net decision value
        // residual-resource continuity — extra opportunity cost charged to a Phase-B card that would
        // consume a resource whose CURRENT-turn, actor-aware AGG/RCN demand is proven blocked on that
        // exact resource. Scaled by urgency (same ramp as above) * attainability * setback, all [0..1],
        // so only a near-attainable, urgent gap is protected; remote gaps are not frozen.
        public const float stratResidualResourcePreservationMax = 2.0f;
        // review-r3 — how many scored chains per demand TopForDemand hands the Phase A injective
        // assignment. >= 2 so a demand with a cheap fallback can yield its scarce card to another
        // demand; the assignment cost is (phaseATopK+1)^activeDemandCount, activeDemandCount <=
        // maxDemandFulfillmentActionsPerTurn.
        public const int phaseATopK = 3;
        public const float baselineReadinessHandBodyMinPower = 4f;  // a hand Unit/Hero at/above this AiPower counts as prepared standing force
        public const float baselineReadinessHandBodyActorWeight = 0.5f; // how much a hand-ready body counts toward the combat-actor gap vs a deployed one

        // --- BaselineForceReadiness (spec §4) — radar-DEMAND-INDEPENDENT standing-force signal.
        //     Need in [0..1]: high when the fielded force / combat-actor count / capability coverage
        //     is thin for the game stage, economy and known enemy strength. Consumed by
        //     ForceGrowthValue AND by DemandLayer.BaselineForceReadinessDemands (one low-priority
        //     FieldCombatPower demand so an ordinary unit gets Phase-A pull, not only surplus).
        public const int baselineReadinessStageRampLo = 2;    // turn at/under which "stage" is 0 (very little standing force expected)
        public const int baselineReadinessStageRampHi = 18;   // turn at/over which "stage" is 1 (a full standing force is expected)
        public const float baselineReadinessBaseTargetPower = 12f;   // minimum expected fielded power regardless of enemy
        public const float baselineReadinessEnemyMatchFrac = 0.60f;  // ...or this fraction of known enemy strength, whichever is larger
        public const float baselineReadinessEarlyTargetFrac = 0.35f; // fraction of the target that applies at stage 0
        public const int baselineReadinessTargetActors = 2;          // combat-capable field actors the AI wants standing
        public const float baselineReadinessPowerGapWeight = 0.45f;
        public const float baselineReadinessActorGapWeight = 0.35f;
        public const float baselineReadinessCoverGapWeight = 0.20f;
        public const float baselineReadinessSecureDamp = 0.55f;      // a fully secure economy multiplies Need by this
        public const float baselineReadinessGrowthFloor = 0.40f;     // ForceGrowthValue keeps at least this fraction of its marginal value at Need 0
        public const float baselineReadinessDemandMinNeed = 0.45f;   // below this Need, DemandLayer raises no baseline demand
        public const float baselineReadinessDemandValue = 22f;       // AxisDemand.Value ceiling for the baseline demand (scaled by Need) — deliberately low so real threats/raids outrank it
        public const float baselineReadinessSatisfiedPower = 14f;    // free raid-eligible field power at/above this + enough actors -> no baseline demand

        // --- P0.1 non-combat cards (Aviation / Base / Facility / standalone Equipment) scored on
        //     the SAME breakdown / NetScore as Unit/Hero — no more NonCombatCardPlayer's fixed
        //     55/45/40/24 scale. Values sit in the same band as a decent combat body's ForceGrowth
        //     + gap so the two lanes are directly comparable. First-pass.
        public const float nonCombatAviationBaseValue = 1.4f;
        public const float nonCombatAviationNoAirGap = 1.2f;   // added when the AI has zero air observation capacity (no wing, no launchable storage)
        public const float nonCombatBaseValue = 1.6f;
        public const float nonCombatFewBasesGap = 1.0f;        // added when the AI holds <= 1 base
        public const float nonCombatFacilityValue = 1.1f;
        public const float nonCombatEconomyRunwayBonus = 1.0f; // scales Base/Facility RoleFit by (1 - EconomicSecurity)
        public const float nonCombatEquipmentValueFloor = 0.15f;
    }
}
