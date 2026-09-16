from pathlib import Path


def replace(path, old, new):
    file = Path(path)
    content = file.read_text()
    hits = content.count(old)
    if hits != 1:
        raise RuntimeError(f'{path}: expected exactly 1 guarded match, found {hits}: {old[:100]!r}')
    file.write_text(content.replace(old, new, 1))


p = 'Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.cs'
replace(p,
'''            if (_raidDurableCommitments != null)
                foreach (int id in _raidDurableCommitments.ClaimedArmyIds)
                    if (proposal == null || !proposal.FromDurableIntent
                        || proposal.PreferredMoverArmyId != id)
                        excluded.Add(id);
            return excluded;
''',
'''            if (_raidDurableCommitments != null)
                foreach (int id in _raidDurableCommitments.ClaimedArmyIds)
                    if (proposal == null || !proposal.FromDurableIntent
                        || proposal.PreferredMoverArmyId != id)
                        excluded.Add(id);
            // Batch-assigned Raid hosts/support are also unavailable as donors, even before
            // their mission executes and RegisterSuccess adds them to ClaimedArmyIds.
            StableMissionKey? ownKey = proposal == null
                ? (StableMissionKey?)null : StableMissionKey.For(proposal);
            foreach (KeyValuePair<StableMissionKey, int> assignment in _raidAssignment)
                if (!ownKey.HasValue || !assignment.Key.Equals(ownKey.Value))
                    excluded.Add(assignment.Value);
            return excluded;
''')
replace(p,
'''            session.SetRaidConstraints(durableCommitments, pinnedByOtherLegs);
            var cands = new List<List<int>>(open.Count);
''',
'''            // A re-pack refreshes the entire assignment; never let last pass's assignments
            // exclude current candidates while solving the new batch.
            session.SetRaidAssignment(new Dictionary<StableMissionKey, int>());
            session.SetRaidConstraints(durableCommitments, pinnedByOtherLegs);
            var cands = new List<List<int>>(open.Count);
''')
replace(p,
'''                    WinChanceGate = RaidAdmissionPolicy.ContinuationWinChanceFloor,
                });
            if (!plan.Feasible)
''',
'''                    // New operations keep the strict fresh gate; only a pinned Hard
                    // incumbent may use the existing bounded continuation floor.
                    WinChanceGate = proposal.FromDurableIntent
                        && proposal.DurableFundingTier == CommitmentTier.Hard
                        && proposal.PreferredMoverArmyId == actorId
                        ? RaidAdmissionPolicy.ContinuationWinChanceFloor
                        : RaidAdmissionPolicy.FreshStartWinChanceGate,
                });
            if (!plan.Feasible)
''')
replace(p,
'''            // Preserve existing PlanForArmy's continuation win floor for the assigned actor.
            // Fresh candidates already passed the strict gate in GroundCombatAdmissionRegistry;
            // unlike PlanForArmy, this request can also assemble a legal same-hex roster.
''',
'''            // Keep the strict gate for fresh actors and the bounded continuation floor for
            // the same Hard incumbent. Unlike PlanForArmy, this request can also assemble
            // a legal same-hex roster, but may never re-select a different primary.
''')

t = 'Assets/Editor/AiRaidActorCommitmentTests.cs'
replace(t,
'''        [Test]
        public void AdmissionExcludesCommittedActor_BeforeRaidFunding()
''',
'''        [Test]
        public void UnassignedRaid_CannotIgnoreBatchDecisionEvenWithFreeForce()
        {
            var snap = new WorldSnapshot { Self = new SelfSnapshot
            {
                Armies = new List<ArmySnapshot> { new ArmySnapshot
                {
                    ArmyId = 9, IsStructuralRaidActor = true, MemberCount = 1,
                    CurrentMovement = 3, Members = Array.Empty<WorthIt.DefenderProfile>(),
                } },
            } };
            var session = new ProvisioningSession(snap);
            MissionProposal mission = Assault();
            session.SetRaidConstraints(new ActorCommitments(), new HashSet<int>());

            GroundCombatAssemblyPlan plan = RaidProvisioner.PlanAssignedAssault(
                session, mission, Array.Empty<WorthIt.DefenderProfile>(), out ProvisionFailure failure);

            Assert.That(plan, Is.Null);
            Assert.That(failure.Kind, Is.EqualTo(ProvisionFailureKind.MoverContended));
        }

        [Test]
        public void AssignedFreeArmy_RemainsPinnedAndExecutesWithoutReassignment()
        {
            var snap = new WorldSnapshot { Self = new SelfSnapshot
            {
                Armies = new List<ArmySnapshot> { new ArmySnapshot
                {
                    ArmyId = 9, IsStructuralRaidActor = true, MemberCount = 1,
                    CurrentMovement = 3,
                    Members = new[] { new WorthIt.DefenderProfile(
                        defense: 1f, hasCeramicArmor: false, attack: 20f,
                        hitPoints: 20f, maxHitPoints: 20f) },
                } },
            } };
            var session = new ProvisioningSession(snap);
            MissionProposal mission = Assault();
            session.SetRaidConstraints(new ActorCommitments(), new HashSet<int>());
            session.SetRaidAssignment(new Dictionary<StableMissionKey, int>
            {
                { StableMissionKey.For(mission), 9 },
            });

            GroundCombatAssemblyPlan plan = RaidProvisioner.PlanAssignedAssault(
                session, mission, Array.Empty<WorthIt.DefenderProfile>(), out _);

            Assert.That(plan, Is.Not.Null);
            Assert.That(plan.Feasible, Is.True);
            Assert.That(plan.BaseArmyId, Is.EqualTo(9));
        }

        [Test]
        public void AdmissionExcludesCommittedActor_BeforeRaidFunding()
''')
replace(t,
'''            session.SetRaidConstraints(claimed, new HashSet<int> { 11 });

            HashSet<int> excluded = session.ExcludedForRaid(incumbent);
''',
'''            session.SetRaidConstraints(claimed, new HashSet<int> { 11 });
            session.SetRaidAssignment(new Dictionary<StableMissionKey, int>
            {
                { StableMissionKey.For(incumbent), 9 },
                { StableMissionKey.For(Assault(43)), 13 }, // Funded Raid not yet provisioned.
            });

            HashSet<int> excluded = session.ExcludedForRaid(incumbent);
''')
replace(t,
'''            Assert.That(excluded, Does.Contain(11));
            session.ClaimedArmyIds.Add(12);
''',
'''            Assert.That(excluded, Does.Contain(11));
            Assert.That(excluded, Does.Contain(13)); // Cannot borrow another Raid's host.
            session.ClaimedArmyIds.Add(12);
''')

source = Path(p).read_text()
assert 'session.SetRaidAssignment(new Dictionary<StableMissionKey, int>());' in source
assert 'assignment.Key.Equals(ownKey.Value)' in source
assert 'proposal.DurableFundingTier == CommitmentTier.Hard' in source
assert Path(t).read_text().count('[Test]') == 6
print('PASS: assigned host/donor reservation, repack cleanup, strict fresh win gate, six EditMode regression tests staged')
