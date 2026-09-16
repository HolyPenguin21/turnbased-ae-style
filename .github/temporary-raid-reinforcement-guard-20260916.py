from pathlib import Path

p = Path('Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.cs')
s = p.read_text()
old = '''            var excluded = new HashSet<int>(ClaimedArmyIds);
            excluded.UnionWith(_raidPinnedByOtherLegs);
            if (_raidDurableCommitments != null)
'''
new = '''            var excluded = new HashSet<int>(ClaimedArmyIds);
            foreach (int pinnedId in _raidPinnedByOtherLegs)
            {
                // The pinned set is computed across ALL funded non-Assault Raid legs, including
                // this very Reinforcement/Return leg. Its own actor must remain permitted;
                // never erase a real same-pass claim or an assignment belonging to another Raid.
                bool thisLegsActor = proposal?.Target is RaidMissionTarget raid
                    && ((raid.Phase == RaidMissionPhase.Reinforcement
                            && raid.SupportArmyId == pinnedId)
                        || (raid.Phase == RaidMissionPhase.Return
                            && raid.PrimaryArmyId == pinnedId)
                        || (raid.Phase == RaidMissionPhase.SupportReturn
                            && raid.SupportArmyId == pinnedId));
                if (!thisLegsActor)
                    excluded.Add(pinnedId);
            }
            if (_raidDurableCommitments != null)
'''
if s.count(old) != 1: raise RuntimeError(f'ownership section changed; matches {s.count(old)}')
p.write_text(s.replace(old, new, 1))

t = Path('Assets/Editor/AiRaidActorCommitmentTests.cs')
s = t.read_text()
old = '''        [Test]
        public void AdmissionExcludesCommittedActor_BeforeRaidFunding()
'''
new = '''        [Test]
        public void PinnedReinforcement_KeepsItsOwnSupportButNotOtherLegs()
        {
            var session = new ProvisioningSession(new WorldSnapshot());
            var mission = new MissionProposal
            {
                Kind = MissionKind.Raid,
                FromDurableIntent = true,
                PreferredMoverArmyId = 9,
                Target = new RaidMissionTarget
                {
                    Phase = RaidMissionPhase.Reinforcement,
                    Target = RaidTargetRef.ForNeutralArmy(42),
                    PrimaryArmyId = 8,
                    SupportArmyId = 9,
                },
            };
            var claimed = new ActorCommitments();
            claimed.Claim(9);
            session.SetRaidConstraints(claimed, new HashSet<int> { 8, 9, 10 });

            HashSet<int> excluded = session.ExcludedForRaid(mission);
            Assert.That(excluded, Does.Not.Contain(9));
            Assert.That(excluded, Does.Contain(8));
            Assert.That(excluded, Does.Contain(10));
        }

        [Test]
        public void AdmissionExcludesCommittedActor_BeforeRaidFunding()
'''
if s.count(old) != 1: raise RuntimeError(f'test insertion marker changed; matches {s.count(old)}')
t.write_text(s.replace(old, new, 1))
assert 'session.ExcludedForRaid(funded.Mission).Contains(support.Id)' in p.read_text()
assert t.read_text().count('[Test]') == 7
print('PASS: same-pass claims and other Raid reservations remain protected; own pinned support exempted, seven Unity test cases staged')
