#!/usr/bin/env python3
from pathlib import Path
p=Path('Assets/Scripts/Ai/V2/Strategy/Demand/InfrastructureFulfillment.cs')
s=p.read_text(encoding='utf-8')
before='''                float freeApWithOwnHold = StrategicResourceReservationLedger.SpendableExcludingOwner(
                    player, turn, StrategicReservedResource.ActionPoints, root.ActionPoints, owner);
                float activationAp = actor.HasActivatedThisTurn ? 0f : actor.ActivationApCost;
                bool completionThisTurn = intent.Status == IntentStatus.Active
                    && route != int.MaxValue && route <= actor.CurrentMovement
                    && freeApWithOwnHold + AiConfigV2.allocatorSliceEpsilon
'''
after='''                // Actual turn AP determines whether this concrete owner CAN still execute.
                // Another owner's legitimate hold is not evidence that our already-provisioned
                // completion became physically impossible; otherwise iteration order would
                // downgrade one of two individually executable independent projects.
                float liveAp = root.ActionPoints;
                float activationAp = actor.HasActivatedThisTurn ? 0f : actor.ActivationApCost;
                bool completionThisTurn = intent.Status == IntentStatus.Active
                    && route != int.MaxValue && route <= actor.CurrentMovement
                    && liveAp + AiConfigV2.allocatorSliceEpsilon
'''
assert s.count(before)==1, f'exact anchor count: {s.count(before)}'
p.write_text(s.replace(before,after,1),encoding='utf-8')
print('Corrected independent completion feasibility: live AP, not other owners reservations')
