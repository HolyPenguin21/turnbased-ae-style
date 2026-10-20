using Game.Aviation;
using Game.Cards;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // A structured result. A generated non-combat play is NOT atomic
    // (mint then deploy), so a partial failure — Challenge lost after resources were spent /
    // the Researcher was revealed, OR a mint that then can't be deployed — really changes state
    // and consumes the turn's generation attempt. The caller must see that, not just `false`.
    internal struct NonCombatExecuteResult
    {
        public bool Played;               // final deploy/attach/build succeeded
        public bool StateChanged;         // ANY real world mutation happened (mint, reveal, resource spend, deploy)
        public bool GenerationAttempted;  // a Challenge was rolled (attempt is spent either way)
        public bool Generated;            // the Challenge won and a card is now in hand
        public float ApSpent;
        public string FailReason;
    }

    // ===========================================================================================
    //  NON-COMBAT PLAY EXECUTION (2026-10-15: moved out of Evaluation's NonCombatCardPlayer).
    //  Runs exactly the chosen NonCombatPlay through the canonical gameplay primitives (aviation
    //  deploy, Base / Facility play, equipment attach, a Challenge mint first when the play is
    //  generated) and reports every real mutation. The one re-resolution it asks Evaluation for is
    //  the placement of a freshly minted card (NonCombatCardPlayer.PlayForGeneratedCard).
    // ===========================================================================================
    internal static class NonCombatPlayExecutor
    {
        internal static NonCombatExecuteResult Execute(NonCombatCardPlayer.NonCombatPlay play, WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            var res = new NonCombatExecuteResult();
            if (play?.Card?.Definition == null || player == null || root == null || hand?.Hand == null || ctx == null)
            {
                res.FailReason = "missing args";
                return res;
            }

            // Revalidate the successful-chain budget before minting too. Expected score
            // never authorizes a fractional payment or the use of a newly protected hold.
            if (!FitsBudget(play, player, root, ctx))
            {
                res.FailReason = "non-combat chain no longer fits AP/spendable reserves";
                return res;
            }

            int apBefore = root.ActionPoints;
            int stateVersionBefore = WorldDeltaLifecycle.Current;

            // finding 9b — a generated non-combat play pays Challenge AP + ResourceCost,
            // then probabilistically mints and deploys the REAL instance. finding P1 — every real
            // mutation of this non-atomic chain is reported even when a later step fails.
            if (play.Generation != null)
            {
                MaterializationExecutor.GenerationOutcome go =
                    MaterializationExecutor.TryGenerate(play.Generation, player, root, hand, ctx);
                res.GenerationAttempted = go.Attempted;
                if (go.StateChanged) res.StateChanged = true;
                if (!go.Success)
                {
                    res.ApSpent = System.Math.Max(0, apBefore - root.ActionPoints);
                    res.FailReason = go.FailReason ?? "generation failed";
                    StampVersion(ref res, stateVersionBefore);
                    return res;
                }
                res.Generated = true;
                res.StateChanged = true;   // a card was minted into the hand

                // Re-resolve the placement against the real card + live world (the pre-mint
                // stand-in's target may be stale). The minted card stays in hand if this fails —
                // a real asset, exactly like CardPlayExecutor keeping a created empty army.
                NonCombatCardPlayer.NonCombatPlay fresh = NonCombatCardPlayer.PlayForGeneratedCard(
                    go.Minted, snap, player, root, hand, ctx);
                // Minting publishes hand/resource events. Re-read the free pool after them;
                // the successful-chain check before generation cannot authorize a new hold.
                if (fresh == null || fresh.Kind != play.Kind || !FitsBudget(fresh, player, root, ctx))
                {
                    res.ApSpent = System.Math.Max(0, apBefore - root.ActionPoints);
                    res.FailReason = "no legal funded placement for the generated non-combat card";
                    StampVersion(ref res, stateVersionBefore);
                    return res;
                }
                play = fresh;
            }

            if (!hand.Hand.Contains(play.Card))
            {
                res.ApSpent = System.Math.Max(0, apBefore - root.ActionPoints);
                res.FailReason = "card no longer in hand";
                StampVersion(ref res, stateVersionBefore);
                return res;
            }

            bool ok;
            string failReason = null;
            switch (play.Kind)
            {
                case NonCombatCardPlayer.PlayKind.Aviation:
                {
                    ok = AviationActions.TryDeployFromCard(play.Card.Definition, player, root,
                        ctx.HexSelection, play.TargetHex, out failReason, null, play.Card);
                    if (ok)
                    {
                        hand.RemoveCard(play.Card);
                    }
                    break;
                }
                case NonCombatCardPlayer.PlayKind.Base:
                {
                    BuildingPlayResult r = BuildingPlayExecutor.PlayBaseCard(player, root, hand, ctx, play.Card, play.TargetHex);
                    ok = r.Built;
                    failReason = r.FailReason;
                    break;
                }
                case NonCombatCardPlayer.PlayKind.Facility:
                {
                    BuildingPlayResult r = BuildingPlayExecutor.PlayFacilityCard(player, root, hand, ctx, play.Card, play.TargetHex);
                    ok = r.Built;
                    failReason = r.FailReason;
                    break;
                }
                case NonCombatCardPlayer.PlayKind.Equipment:
                {
                    ArmyData recipientArmy = OwnWorld.Of(player).ArmyContaining(play.EquipHost);
                    if (play.EquipHostCard != null
                        ? !hand.Hand.Contains(play.EquipHostCard)
                        : play.EquipHost == null || play.EquipHost.IsPrisoner || recipientArmy?.Owner != player)
                    {
                        failReason = "equipment host gone";
                        ok = false;
                        break;
                    }
                    // Re-read the bank immediately before the shared gameplay transaction.
                    // A candidate's earlier affordability cannot authorize another owner's hold.
                    if (play.Card.EffectivePlayApCost > StrategicSpendability.SpendableAp(player, root, ctx)
                        || !StrategicSpendability.FitsSpendableResources(player, root, ctx,
                            play.Card.EffectivePlayResourceCost))
                    {
                        failReason = "attachment AP/resources reserved since planning";
                        ok = false;
                        break;
                    }
                    ok = play.EquipHostCard != null
                        ? EquipmentSystem.TryAttach(play.Card, play.EquipHostCard, root, out failReason)
                        : EquipmentSystem.TryAttach(play.Card, play.EquipHost, root, out failReason);
                    if (ok)
                    {
                        hand.RemoveCard(play.Card);
                        DevelopmentOutcomeTelemetry.RecordAttachment(player, ctx.TurnNumber, play.Card);
                    }
                    break;
                }
                default:
                    failReason = "unknown non-combat play kind";
                    ok = false;
                    break;
            }

            res.Played = ok;
            if (ok) res.StateChanged = true;
            res.ApSpent = System.Math.Max(0, apBefore - root.ActionPoints);
            if (!ok) res.FailReason = failReason ?? "non-combat play failed";
            StampVersion(ref res, stateVersionBefore);
            return res;
        }

        private static bool FitsBudget(NonCombatCardPlayer.NonCombatPlay play, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx) =>
            play.ApCost <= StrategicSpendability.SpendableAp(player, root, ctx) + AiConfigV2.allocatorSliceEpsilon
            && StrategicSpendability.FitsSpendableResources(player, root, ctx, play.ResCost);

        private static void StampVersion(ref NonCombatExecuteResult result, int versionBefore)
        {
            WorldDeltaLifecycle.StampAction(result.StateChanged,
                childAlreadyCommitted: WorldDeltaLifecycle.Current != versionBefore);
        }
    }
}
