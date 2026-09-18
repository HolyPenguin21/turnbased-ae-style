from pathlib import Path
import subprocess

EXPECTED = {
 'Assets/Scripts/Ai/V2/Continuity/MissionIntentState.cs':'63c8d72126d4a2a1ed15dca2c58f2e1e7b4fa2ac',
 'Assets/Scripts/Ai/V2/Materialization/MaterializationCandidateBuilder.cs':'cf6a1f8ad30e3d1a1027a239f453700a8c811e5d',
 'Assets/Scripts/Ai/V2/Strategy/Demand/InfrastructureFulfillment.cs':'05079619114a5f38a43669a00d7c6f4113c063683',
 'Assets/Scripts/Ai/V2/Strategy/StrategicPhaseA.cs':'4a7ea87b911148f40d851bd01040fbfcf23bac08',
 'Assets/Editor/AiIndependentDevelopmentTests.cs':'03d23fc49bd39cd97747959309f37c02da4c450e',
}
for name, sha in EXPECTED.items():
    actual = subprocess.check_output(['git', 'hash-object', name], text=True).strip()
    if actual != sha:
        raise SystemExit(f'Source changed: {name}: {actual} != {sha}')

def replace(path, old, new):
    p = Path(path)
    text = p.read_text()
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'Expected one anchor in {path}, got {count}: {old[:120]!r}')
    p.write_text(text.replace(old, new, 1))

state='Assets/Scripts/Ai/V2/Continuity/MissionIntentState.cs'
replace(state, '''        public void Remove(MissionIntentKey k) => _intents.Remove(k);
''', '''        public void Remove(MissionIntentKey k) => _intents.Remove(k);

        // Challenge mints a physical Hero before the destination factory can deploy it.
        // This per-player intent store survives turns; AP/resource reservations do not.
        private readonly Dictionary<CardData, (HexCoord Site, ResearchProductionMode Mode, int Turn)>
            _generatedDevelopmentOperators =
                new Dictionary<CardData, (HexCoord, ResearchProductionMode, int)>();

        internal void RememberGeneratedDevelopmentOperator(CardData card, HexCoord site,
            ResearchProductionMode mode, int turn)
        {
            if (card == null) return;
            // Only one card may be earmarked for each facility/role at once.
            foreach (CardData previous in _generatedDevelopmentOperators
                .Where(x => x.Value.Site.Equals(site) && x.Value.Mode == mode)
                .Select(x => x.Key).ToList())
                _generatedDevelopmentOperators.Remove(previous);
            _generatedDevelopmentOperators[card] = (site, mode, turn);
        }

        // Infrastructure provides current structural eligibility; State owns identity,
        // finite age and removal. An AP shortage is NOT a reason to discard a valid card.
        internal IReadOnlyList<CardData> ReconcileGeneratedDevelopmentOperators(int turn,
            Func<CardData, HexCoord, ResearchProductionMode, bool> stillNeeded)
        {
            foreach (var claim in _generatedDevelopmentOperators.ToList())
                if (turn < claim.Value.Turn
                    || turn - claim.Value.Turn > System.Math.Max(1, AiConfigV2.commitmentStallTurns)
                    || stillNeeded == null
                    || !stillNeeded(claim.Key, claim.Value.Site, claim.Value.Mode))
                    _generatedDevelopmentOperators.Remove(claim.Key);
            return _generatedDevelopmentOperators.Keys.ToList();
        }
''')

builder='Assets/Scripts/Ai/V2/Materialization/MaterializationCandidateBuilder.cs'
replace(builder, '''        public bool ClaimsDevelopmentOperatorCard(CardData card) =>
            card != null && claimedDevelopmentOperatorCards.Contains(card);
''', '''        public bool ClaimsDevelopmentOperatorCard(CardData card) =>
            card != null && claimedDevelopmentOperatorCards.Contains(card);

        // Sync the per-turn projection with durable commitments; release cards in the SAME
        // turn when staffing consumes them or their destination is no longer attainable.
        internal void ReconcileDevelopmentOperatorCards(IEnumerable<CardData> cards)
        {
            claimedDevelopmentOperatorCards.Clear();
            if (cards == null) return;
            foreach (CardData card in cards)
                ClaimDevelopmentOperatorCard(card);
        }
''')

infra='Assets/Scripts/Ai/V2/Strategy/Demand/InfrastructureFulfillment.cs'
replace(infra, '''        public static bool Handles(CapabilityKind k) =>
            k == CapabilityKind.EconomicInfrastructure
            || k == CapabilityKind.EconomicExpansionBase
            || k == CapabilityKind.DevelopmentInfrastructure
            || k == CapabilityKind.DevelopmentOperator;
''', '''        public static bool Handles(CapabilityKind k) =>
            k == CapabilityKind.EconomicInfrastructure
            || k == CapabilityKind.EconomicExpansionBase
            || k == CapabilityKind.DevelopmentInfrastructure
            || k == CapabilityKind.DevelopmentOperator;

        // This existing staffing owner validates persisted claims before Phase A/B and after
        // infrastructure mutations. Never duplicate generation, movement or card execution.
        // Current AP/resources are deliberately NOT eligibility criteria for a future turn.
        internal static void RestoreGeneratedOperatorClaims(PlayerSetupData player,
            AiHandData hand, int turn, MaterializationReservation reservation)
        {
            if (player == null || reservation == null) return;
            IReadOnlyList<CardData> cards = MissionIntentRegistry.GetOrCreate(player)
                .ReconcileGeneratedDevelopmentOperators(turn, (card, site, mode) =>
                {
                    if (card?.Definition?.cardType != CardType.Hero
                        || hand?.Hand?.Contains(card) != true
                        || !MaterializationChainMatching.EffectiveAbilities(
                            card.Definition, card.Equipment)
                            .Contains(ResearchProductionSystem.RoleAbility(mode)))
                        return false;
                    BuildingData building = BuildingRegistry.FindAt(site);
                    if (building == null || building.Owner != player
                        || !building.HasFacilityWithAbility(
                            ResearchProductionSystem.FacilityAbility(mode))
                        || ResearchProductionSystem.FindActor(player, site, mode) != null
                        || Game.Combat.BattleInitiator.FindEnemyAt(site, player) != null
                        || !PlacementRules.HasRequiredBuilding(player, site, card.Definition))
                        return false;
                    return ArmyRegistry.AllAt(site).Any(g => g != null && g.Owner == player
                        && g.IsGarrison && !g.IsPrison
                        && PlacementRules.CanDepositIntoGarrison(g)
                        && CardPlayExecutor.CanFitAfterDeploy(g, card.Definition));
                });
            reservation.ReconcileDevelopmentOperatorCards(cards);
        }
''')

phase='Assets/Scripts/Ai/V2/Strategy/StrategicPhaseA.cs'
replace(phase, '''            demands ??= System.Array.Empty<AxisDemand>();
            radar ??= Radar.Even();
''', '''            demands ??= System.Array.Empty<AxisDemand>();
            radar ??= Radar.Even();
            // A new turn constructs a fresh MaterializationReservation. Restore the exact
            // pending Hero even when the current dirty-demand subset omits Development.
            InfrastructureFulfillment.RestoreGeneratedOperatorClaims(player, hand,
                ctx.TurnNumber, result.Reservation);
''')
replace(phase, '''                            // Preserve the exact minted card until the existing operator
                            // fulfillment can place it. No new card reserve or role system.
                            result.Reservation.ClaimDevelopmentOperatorCard(
                                infra.GeneratedOperatorCard);
''', '''                            // A successful Challenge can exhaust AP before placement. Keep
                            // the exact physical Hero and its destination in durable State.
                            if (infra.GeneratedOperatorCard != null
                                && istate.Demand.TargetHex.HasValue
                                && istate.Demand.DevelopmentOperatorMode.HasValue)
                                MissionIntentRegistry.GetOrCreate(player)
                                    .RememberGeneratedDevelopmentOperator(
                                        infra.GeneratedOperatorCard, istate.Demand.TargetHex.Value,
                                        istate.Demand.DevelopmentOperatorMode.Value, ctx.TurnNumber);
''')
replace(phase, '''                        if (infra.StateChanged)
                            snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                        if (infra.Built && istate.Demand.RequestingAxis == DesireAxis.Economy
''', '''                        if (infra.StateChanged)
                        {
                            snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                            // A minted card is protected immediately. A consumed card or a
                            // newly invalid factory loses protection in this same pass.
                            InfrastructureFulfillment.RestoreGeneratedOperatorClaims(player,
                                hand, ctx.TurnNumber, result.Reservation);
                        }
                        if (infra.Built && istate.Demand.RequestingAxis == DesireAxis.Economy
''')

tests='Assets/Editor/AiIndependentDevelopmentTests.cs'
replace(tests, '''        [Test]
        public void GeneratedOperatorRequiresAuthenticQualifiedHeroAndPositiveChallengeChance()
''', '''        [Test]
        public void MintedOperatorSurvivesTurnBoundaryAndOnlyItsPhysicalCardIsClaimed()
        {
            var persistent = new MissionIntentState();
            var factory = new HexCoord(3, -2);
            var definition = new CardDefinition { cardType = CardType.Hero };
            var minted = new CardData(definition) { ResearchProductionCreated = true };
            var otherCopy = new CardData(definition) { ResearchProductionCreated = true };
            persistent.RememberGeneratedDevelopmentOperator(minted, factory,
                ResearchProductionMode.Production, 2);
            var nextTurn = new MaterializationReservation(); // not the T2 instance
            foreach (CardData card in persistent.ReconcileGeneratedDevelopmentOperators(3,
                (c, site, mode) => site.Equals(factory)
                    && mode == ResearchProductionMode.Production))
                nextTurn.ClaimDevelopmentOperatorCard(card);
            Assert.That(nextTurn.ClaimsDevelopmentOperatorCard(minted), Is.True);
            Assert.That(nextTurn.ClaimsDevelopmentOperatorCard(otherCopy), Is.False);
        }

        [Test]
        public void MintedOperatorClaimReleasesOnInvalidDestinationAndFiniteExpiry()
        {
            var persistent = new MissionIntentState();
            var minted = new CardData(new CardDefinition { cardType = CardType.Hero });
            var factory = new HexCoord(3, -2);
            persistent.RememberGeneratedDevelopmentOperator(minted, factory,
                ResearchProductionMode.Production, 2);
            var reservation = new MaterializationReservation();
            reservation.ClaimDevelopmentOperatorCard(minted);
            reservation.ReconcileDevelopmentOperatorCards(
                persistent.ReconcileGeneratedDevelopmentOperators(3, (c, site, mode) => false));
            Assert.That(reservation.ClaimsDevelopmentOperatorCard(minted), Is.False,
                "Lost, staffed, contested or full factory must release its Hero");

            persistent.RememberGeneratedDevelopmentOperator(minted, factory,
                ResearchProductionMode.Production, 2);
            int expiredTurn = 2 + System.Math.Max(1, AiConfigV2.commitmentStallTurns) + 1;
            Assert.That(persistent.ReconcileGeneratedDevelopmentOperators(expiredTurn,
                (c, site, mode) => true), Is.Empty,
                "No permanent reservation when AP or structural availability never recovers");
        }

        [Test]
        public void GeneratedOperatorRequiresAuthenticQualifiedHeroAndPositiveChallengeChance()
''')
print('Source-locked transformation completed:', ', '.join(EXPECTED))
