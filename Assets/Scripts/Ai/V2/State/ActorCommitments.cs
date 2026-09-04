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
    //  intent. For Raid that means the same structural actor shape ProvisioningManager accepts:
    //  a real ground field army, not prison/airfield/air/Recce and not a lone hero awaiting escort.
    //  If battle damage leaves a started Raid as only a hero, the INTENT may survive but the actor
    //  claim is released; DemandLayer can then ask StrategicManager for the missing escort instead
    //  of incorrectly declaring the objective covered by an actor provisioning will reject.
    // ===========================================================================================
    public sealed class ActorCommitments
    {
        private readonly HashSet<int> _claimedArmyIds = new HashSet<int>();

        public IReadOnlyCollection<int> ClaimedArmyIds => _claimedArmyIds;

        // Live copy for the shared eligibility primitive (ScoutMoverSelector.Eligible takes an ISet).
        public HashSet<int> ClaimedArmyIdSet => new HashSet<int>(_claimedArmyIds);

        public bool IsArmyClaimed(int armyId) => armyId != 0 && _claimedArmyIds.Contains(armyId);

        public void Claim(int armyId)
        {
            if (armyId != 0)
                _claimedArmyIds.Add(armyId);
        }

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
                if (i?.PreferredMoverArmyId == null)
                    continue;

                if (i.Kind == MissionKind.Raid)
                {
                    int actorId = i.PreferredMoverArmyId.Value;
                    if (RaidActorStillValid(actorId, snap, out string reason))
                    {
                        c.Claim(actorId);
                        AiDebugLog.Write($"[AI][V2][Commitment][Raid] decision=CLAIM intent={i.IntentKey} actor={actorId} "
                            + "reason=actor_still_matches_raid_provisioning_gate");
                    }
                    else
                    {
                        AiDebugLog.Write($"[AI][V2][Commitment][Raid] decision=RELEASE intent={i.IntentKey} actor={actorId} "
                            + $"reason={reason}");
                    }
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
            if (armyId == 0 || snap?.Self?.Armies == null)
            {
                reason = "missing_actor_or_snapshot";
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

        // Is the intent's committed mover a live own army STRUCTURALLY able to run it — a solo
        // Recce, and (when the objective requires stealth) hidden or able to enter stealth? Read
        // from the snapshot's own-army list; no live game-system call.
        public static bool HasCapableActor(MissionIntent intent, WorldSnapshot snap, StealthRequirement requirement)
        {
            if (intent?.PreferredMoverArmyId == null || snap?.Self?.Armies == null)
                return false;
            int id = intent.PreferredMoverArmyId.Value;

            ArmySnapshot a = null;
            foreach (ArmySnapshot s in snap.Self.Armies)
                if (s != null && s.ArmyId == id) { a = s; break; }
            if (a == null || !a.IsSoloRecce || a.IsPrison || a.IsAir || a.MemberCount <= 0)
                return false;

            if (requirement == StealthRequirement.Required
                && !(a.IsHidden || a.CanEnterStealth || a.StealthLevel > 0))
                return false;
            return true;
        }
    }
}
