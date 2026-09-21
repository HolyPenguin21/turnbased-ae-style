using System.Collections.Generic;
using System.Linq;
using Game.Map;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ACTOR COMMITMENTS  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    //  A normalized "which of my own armies are already committed to an active operation" view.
    //  Built once from MissionContinuityLayer's resolved intents (and rebuilt from the reconciled
    //  registry before Phase B). Downstream code — DemandLayer, CapabilityInventory,
    //  ReusableArmySelector, StrategicManager — only ever asks IsArmyClaimed(id); it never learns
    //  HOW continuity stores mover ownership. This is what lets "existing Scout" be told apart
    //  from "available Scout", and it extends unchanged to Raid / Defence / Assembly when those
    //  gain persistent missions.
    //
    //  An intent's PreferredMoverArmyId is only claimed while the actor is STILL VALID for that
    //  intent. Active Raid combat phases use the same structural actor shape ProvisioningManager
    //  accepts: a real ground field army, not prison/airfield/air/Recce and not a lone hero awaiting
    //  escort. Raid Return is deliberately different: the objective is to bring the surviving
    //  ground container home, so it keeps the claim while it still matches ProvisionReturn's
    //  live/non-empty ground-container contract even if battle damage made it combat-ineligible.
    // ===========================================================================================
    public sealed class ActorCommitments
    {
        private readonly HashSet<int> _claimedArmyIds = new HashSet<int>();

        public IReadOnlyCollection<int> ClaimedArmyIds => _claimedArmyIds;

        // Live copy for the shared eligibility primitive (ScoutMoverSelector.Eligible takes an ISet).
        public HashSet<int> ClaimedArmyIdSet => new HashSet<int>(_claimedArmyIds);

        // armyId here is always an already-resolved concrete actor id, never a "no army" signal —
        // 0 is a legitimate ArmyData.Id (see ArmyData identity sequencing) and must be claimable
        // exactly like any other id.
        public bool IsArmyClaimed(int armyId) => _claimedArmyIds.Contains(armyId);

        public void Claim(int armyId) => _claimedArmyIds.Add(armyId);

        public static ActorCommitments FromIntents(IEnumerable<MissionIntent> intents,
            WorldSnapshot snap, IReadOnlyList<ReconObjective> reconObjectives)
        {
            var c = new ActorCommitments();
            if (intents == null || snap?.Self?.Armies == null)
                return c;

            // The current stealth requirement of each intent's objective, from the ONE Recon
            // objective enumeration (an exposed Explore is Stealth.Required too — never infer the
            // requirement from ScoutTargetKind).
            var reqByKey = new Dictionary<MissionIntentKey, StealthRequirement>();
            if (reconObjectives != null)
                foreach (ReconObjective o in reconObjectives)
                    reqByKey[o.IntentKey] = o.Stealth;

            foreach (MissionIntent i in intents)
            {
                // AGG-RAID §5/§SupportReturn — the SUPPORT actor of a Raid is claimed independently
                // of the primary while it is either carrying reinforcement TO the primary
                // (Reinforcement) or walking a displaced member back home AFTER a full/full swap
                // (SupportReturn): Housekeeping (and every other mission lane) must never see the
                // convoy as a free army during either leg. Losing it releases just this claim.
                RaidIntent raid = i?.Raid;
                if (raid != null && raid.Phase == RaidMissionPhase.AirSupport
                    && raid.AirSupportArmyId.HasValue
                    && snap.Self.Armies.Any(a => a != null
                        && a.ArmyId == raid.AirSupportArmyId.Value && a.IsAir
                        && !a.IsAirfield && a.MemberCount > 0))
                    c.Claim(raid.AirSupportArmyId.Value);
                if (raid != null && raid.SupportArmyId.HasValue
                    && (raid.Phase == RaidMissionPhase.Reinforcement || raid.Phase == RaidMissionPhase.SupportReturn)
                    && snap.Self.Armies.Any(a => a != null && a.ArmyId == raid.SupportArmyId.Value
                        && !a.IsPrison && !a.IsAir && a.MemberCount > 0))
                {
                    c.Claim(raid.SupportArmyId.Value);
                    AiDebugLog.Write($"[AI][V2][Commitment][Raid] decision=CLAIM intent={i.IntentKey} "
                        + $"support={raid.SupportArmyId.Value} phase={raid.Phase} reason=support_actor_en_route");
                }
                if (i?.PreferredMoverArmyId == null)
                    continue;

                if (i.Kind == MissionKind.Economy)
                {
                    int actorId = i.PreferredMoverArmyId.Value;
                    bool mobile = i.Economy?.Kind == EconomyTaskKind.MobileCollection
                        || i.Economy?.Kind == EconomyTaskKind.ReturnCollector;
                    ArmySnapshot actor = snap.Self.Armies.FirstOrDefault(a => a != null
                        && a.ArmyId == actorId && !a.IsPrison && !a.IsAir
                        && (mobile || a.HasHero));
                    if (actor != null) c.Claim(actorId);
                    continue;
                }

                if (i.Kind == MissionKind.Development)
                {
                    int actorId = i.PreferredMoverArmyId.Value;
                    ArmySnapshot actor = snap.Self.Armies.FirstOrDefault(a => a != null
                        && a.ArmyId == actorId && !a.IsPrison && !a.IsAir && a.HasHero);
                    ArmyData live = actor == null ? null : ArmyRegistry.AllForOwner(actor.Owner)
                        .FirstOrDefault(a => a != null && a.Id == actorId);
                    if (live?.Members.Contains(i.Development?.Hero) == true)
                        c.Claim(actorId);
                    continue;
                }

                if (i.Kind == MissionKind.Raid)
                {
                    int actorId = i.PreferredMoverArmyId.Value;
                    if (raid != null && (raid.Phase == RaidMissionPhase.Return
                            || raid.Phase == RaidMissionPhase.RecoveryReturn))
                    {
                        ArmySnapshot returningPrimary = snap.Self.Armies.FirstOrDefault(a => a != null
                            && a.ArmyId == actorId && !a.IsPrison && !a.IsAir && a.MemberCount > 0);
                        if (returningPrimary != null)
                        {
                            c.Claim(actorId);
                            AiDebugLog.WriteDeduped(i.IntentKey.ToString(),
                                $"[AI][V2][Commitment][Raid] decision=CLAIM intent={i.IntentKey} actor={actorId} "
                                + $"reason={raid.Phase}_actor_still_matches_ground_container_gate");
                        }
                        else
                        {
                            AiDebugLog.WriteDeduped(i.IntentKey.ToString(),
                                $"[AI][V2][Commitment][Raid] decision=RELEASE intent={i.IntentKey} actor={actorId} "
                                + "reason=return_actor_missing_or_non_ground_container");
                        }
                        continue;
                    }

                    if (RaidActorStillValid(actorId, snap, out string reason))
                    {
                        c.Claim(actorId);
                        AiDebugLog.WriteDeduped(i.IntentKey.ToString(),
                            $"[AI][V2][Commitment][Raid] decision=CLAIM intent={i.IntentKey} actor={actorId} "
                            + "reason=actor_still_matches_raid_provisioning_gate");
                    }
                    else
                    {
                        AiDebugLog.WriteDeduped(i.IntentKey.ToString(),
                            $"[AI][V2][Commitment][Raid] decision=RELEASE intent={i.IntentKey} actor={actorId} "
                            + $"reason={reason}");
                    }
                    continue;
                }

                if (i.Kind == MissionKind.ActiveDefence)
                {
                    int actorId = i.PreferredMoverArmyId.Value;
                    if (RaidActorStillValid(actorId, snap, out _))
                        c.Claim(actorId);
                    continue;
                }

                StealthRequirement req;
                if (reqByKey.TryGetValue(i.IntentKey, out StealthRequirement r))
                {
                    req = r;
                }
                else
                {
                    // A still-valid incumbent Explore/Refresh whose hex has fallen out of the
                    // frozen enumeration (Explore: wave band moved; Refresh: hex dropped past the
                    // capped stale-hex pool in BuildRefreshObjectives) has NO entry here.
                    // MissionLayer re-materialises the ONE incumbent objective via
                    // ReconObjectiveEvaluator.{ExploreAt,RefreshAt}, each of which recomputes
                    // exposure and can return Stealth.Required. Mirror that per-kind (a re-focused
                    // Refresh must NOT fall through to SurveilOf — it has no TrackedArmyId, so that
                    // path returns null and silently drops a real stealth requirement).
                    ReconObjective o = null;
                    if (i.Scout != null)
                    {
                        switch (i.Scout.Kind)
                        {
                            case ScoutTargetKind.Explore:
                                o = ReconObjectiveEvaluator.ExploreAt(snap, i.Scout.FocusHex);
                                break;
                            case ScoutTargetKind.Refresh:
                                o = ReconObjectiveEvaluator.RefreshAt(snap, i.Scout.FocusHex);
                                break;
                            default:
                                o = ReconObjectiveEvaluator.SurveilOf(snap,
                                    ScoutObjectiveEvaluator.SurveilContact(snap, i.Scout.TrackedArmyId));
                                break;
                        }
                    }
                    req = o?.Stealth ?? StealthRequirement.None;
                }

                if (HasCapableActor(i, snap, req))
                    c.Claim(i.PreferredMoverArmyId.Value);
            }
            return c;
        }

        private static bool RaidActorStillValid(int armyId, WorldSnapshot snap, out string reason)
        {
            reason = null;
            if (snap?.Self?.Armies == null)
            {
                reason = "missing_snapshot";
                return false;
            }

            ArmySnapshot actor = snap.Self.Armies.FirstOrDefault(a => a != null && a.ArmyId == armyId);
            if (actor == null || actor.Owner == null)
            {
                reason = "actor_not_in_own_snapshot";
                return false;
            }
            if (actor.IsPrison || actor.IsAir || actor.IsSoloRecce || actor.MemberCount <= 0)
            {
                reason = "snapshot_actor_not_ground_combat_force";
                return false;
            }

            // Snapshot.IsAir does not encode an airfield container, and a post-battle lone hero is
            // a role-level invalid Raid actor that the snapshot does not encode directly. Resolve
            // only the matching OWN live army to mirror the final provisioning structural gate.
            ArmyData live = ArmyRegistry.AllForOwner(actor.Owner)
                .FirstOrDefault(a => a != null && a.Id == armyId);
            if (live == null)
            {
                reason = "live_actor_missing";
                return false;
            }
            if (live.IsPrison || live.IsGarrison || live.IsAirfield || live.IsAirArmy)
            {
                reason = "live_actor_is_non_field_container";
                return false;
            }
            if (AiArmyRoles.IsSoloRecce(live))
            {
                reason = "live_actor_is_dedicated_recce";
                return false;
            }
            if (AiArmyRoles.IsSoloHeroAwaitingEscort(live))
            {
                reason = "live_actor_is_solo_hero_awaiting_escort";
                return false;
            }
            if (live.Members.Count <= 0)
            {
                reason = "live_actor_empty";
                return false;
            }
            return true;
        }

        // Is the intent's committed mover structurally able to continue the role? Ground uses the
        // canonical solo-Recce shape. Air may continue observation (Refresh/Surveil) outside the
        // ground concurrency cap, but can never satisfy Explore's physical-visit or stealth lane.
        public static bool HasCapableActor(MissionIntent intent, WorldSnapshot snap, StealthRequirement requirement)
        {
            if (intent?.PreferredMoverArmyId == null || snap?.Self?.Armies == null)
                return false;
            int id = intent.PreferredMoverArmyId.Value;

            ArmySnapshot a = null;
            foreach (ArmySnapshot s in snap.Self.Armies)
                if (s != null && s.ArmyId == id) { a = s; break; }
            if (a == null || a.IsPrison || a.MemberCount <= 0)
                return false;

            if (a.IsAir)
                return intent.Scout != null
                    && intent.Scout.Kind != ScoutTargetKind.Explore
                    && requirement != StealthRequirement.Required;

            if (!a.IsSoloRecce)
                return false;

            if (requirement == StealthRequirement.Required
                && !(a.IsHidden || a.CanEnterStealth || a.StealthLevel > 0))
                return false;
            return true;
        }
    }
}
