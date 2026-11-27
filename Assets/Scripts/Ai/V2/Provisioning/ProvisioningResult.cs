using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    internal readonly struct ProvisionFailure
    {
        public readonly ProvisionFailureKind Kind;
        public readonly ProvisionDisposition Disposition;
        // The GENERIC multi-resource envelope this failure reports as needed.
        // Only meaningful for EnvelopeTooSmall/RepriceThisTurn; every other constructor leaves it at
        // ProvisionRequirement.Zero. RequiredAp is kept as a read-only convenience projection for
        // existing AP-only call sites/log lines — it is never the underlying storage any more.
        public readonly ProvisionRequirement Requirement;
        public readonly string Detail;

        public ProvisionFailure(ProvisionFailureKind kind, ProvisionDisposition disposition, ProvisionRequirement requirement, string detail)
        {
            Kind = kind;
            Disposition = disposition;
            Requirement = requirement;
            Detail = detail;
        }

        public static ProvisionFailure MoverContended(string d) =>
            new ProvisionFailure(ProvisionFailureKind.MoverContended, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure NoMoverExists(string d) =>
            new ProvisionFailure(ProvisionFailureKind.NoMoverExists, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure NoObservationVantage(string d) =>
            new ProvisionFailure(ProvisionFailureKind.NoObservationVantage, ProvisionDisposition.RejectWithCooldown, ProvisionRequirement.Zero, d);
        // AP-only convenience overload — every existing caller (Ground Scout, Raid, and any future
        // Aggression/Defence/Economy/Development provisioner) that has no physical-resource shortfall
        // keeps calling this exactly as before; Physical is Zero, so the allocator's component-wise
        // max reduces to the pre-round-7 float-floor behaviour for them.
        public static ProvisionFailure EnvelopeTooSmall(float requiredAp, string d) =>
            EnvelopeTooSmall(ProvisionRequirement.ApOnly(requiredAp), d);
        public static ProvisionFailure EnvelopeTooSmall(ProvisionRequirement requirement, string d) =>
            new ProvisionFailure(ProvisionFailureKind.EnvelopeTooSmall, ProvisionDisposition.RepriceThisTurn, requirement, d);
        public static ProvisionFailure NoExecutableStep(string d) =>
            new ProvisionFailure(ProvisionFailureKind.NoExecutableStep, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure DestinationUnreachable(string d) =>
            new ProvisionFailure(ProvisionFailureKind.DestinationUnreachable, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure TargetSatisfied(string d) =>
            new ProvisionFailure(ProvisionFailureKind.TargetSatisfied, ProvisionDisposition.DropThisTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure TargetInvalidated(string d) =>
            new ProvisionFailure(ProvisionFailureKind.TargetInvalidated, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure AssemblyInfeasible(string d) =>
            new ProvisionFailure(ProvisionFailureKind.AssemblyInfeasible, ProvisionDisposition.RejectWithCooldown, ProvisionRequirement.Zero, d);
        // Air recon only — hard feasibility passed (CanAffordLaunch + funded envelope + live AP/Energy),
        // but the staged strategic reservation decision declined to protect Energy/AP for the sortie
        // this turn. No cooldown: it is re-evaluated from a fresh snapshot every turn.
        public static ProvisionFailure SortieNotWorthwhile(string d) =>
            new ProvisionFailure(ProvisionFailureKind.SortieNotWorthwhile, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
    }

    internal sealed class ProvisioningResult
    {
        public bool Success;
        public ProvisionedMission Provisioned;
        public ProvisionFailure Failure;

        // Provisioning is pure binding except the same-hex assault assembly and the Recon garrison
        // extraction: a committed transfer (or a commander promotion, `otherMutation`) bumps the
        // world revision once, and the provisioned plan is stamped with the revision it was made at.
        public static ProvisioningResult Ok(ProvisionedMission m, int transferredMemberCount = 0,
            bool otherMutation = false)
        {
            bool changed = transferredMemberCount > 0 || otherMutation;
            int version = changed ? WorldDeltaLifecycle.CommitMutation() : WorldDeltaLifecycle.Current;
            if (m != null) m.PlannedAtStateVersion = version;
            return new ProvisioningResult { Success = true, Provisioned = m };
        }

        // `stateChanged`: a rollback that could not undo every transfer left the world changed.
        public static ProvisioningResult Fail(ProvisionFailure f, bool stateChanged = false)
        {
            if (stateChanged) WorldDeltaLifecycle.CommitMutation();
            return new ProvisioningResult { Success = false, Failure = f };
        }
    }
}
