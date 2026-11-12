using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Game.HexGrid;

namespace Game.Map
{
    public enum SandstormPhase
    {
        WaitingForSpawn = 1,
        Active = 2,
        Cooldown = 3,
    }

    // Read-only view of one storm slot for rules diagnostics and the future presenter.
    public sealed class SandstormInstanceView
    {
        public int SlotId { get; }
        public SandstormPhase Phase { get; }
        public int Age { get; }
        public int Course { get; }
        // SandstormRules.SpotCells order (ring, then the centre last); empty outside the Active phase.
        public IReadOnlyList<HexCoord> Body { get; }

        internal SandstormInstanceView(int slotId, SandstormPhase phase, int age, int course, IEnumerable<HexCoord> body)
        {
            SlotId = slotId;
            Phase = phase;
            Age = age;
            Course = course;
            Body = new ReadOnlyCollection<HexCoord>((body ?? Enumerable.Empty<HexCoord>()).ToArray());
        }
    }

    // Immutable weather of one moment. Rules read only Contains (the union of every storm:
    // overlapping storms apply each penalty once). A search or forecast captures one snapshot
    // and never sees it change mid-calculation.
    public sealed class SandstormSnapshot
    {
        public static readonly SandstormSnapshot Clear =
            new SandstormSnapshot(MatchWeatherProfile.Clear, 0, Array.Empty<SandstormInstanceView>());

        private readonly HashSet<HexCoord> _cells;

        public MatchWeatherProfile Profile { get; }
        // Changes whenever any instance's geometry changes (even if the union happens to stay).
        public int Revision { get; }
        public IReadOnlyList<SandstormInstanceView> Instances { get; }
        // Union of the current storm areas, in stable coordinate order.
        public IReadOnlyList<HexCoord> Cells { get; }
        public bool HasStorm => _cells.Count > 0;

        internal SandstormSnapshot(MatchWeatherProfile profile, int revision, IEnumerable<SandstormInstanceView> instances)
        {
            Profile = profile;
            Revision = revision;
            var views = (instances ?? Enumerable.Empty<SandstormInstanceView>()).OrderBy(v => v.SlotId).ToArray();
            Instances = new ReadOnlyCollection<SandstormInstanceView>(views);
            _cells = new HashSet<HexCoord>();
            foreach (SandstormInstanceView view in views)
                foreach (HexCoord hex in view.Body)
                    _cells.Add(hex);
            var sorted = _cells.ToList();
            sorted.Sort(SandstormRules.CompareCoords);
            Cells = new ReadOnlyCollection<HexCoord>(sorted);
        }

        public bool Contains(HexCoord hex) => _cells.Contains(hex);

        public bool SameArea(SandstormSnapshot other) =>
            other != null && other._cells.Count == _cells.Count && _cells.SetEquals(other._cells);
    }

    // File DTOs (JsonUtility-serializable). Slot coordinates are parallel q/r lists.
    [Serializable] public sealed class SandstormStateData
    {
        public int profile;
        public long seed;
        public int lastRound;
        public List<SandstormSlotData> slots = new List<SandstormSlotData>();
    }

    [Serializable] public sealed class SandstormSlotData
    {
        public int id, phase, age, course, nextSpawnRound;
        public long random;
        public List<int> bodyQ = new List<int>();
        public List<int> bodyR = new List<int>();
    }

    // ===========================================================================================
    //  The match's only weather owner: profile + seed and 0/1/2 independent storm slots. One
    //  call per FULL ROUND (GameTurnController.ProceedWithNewTurn) advances every slot on a
    //  working copy and publishes one new snapshot atomically. Loading restores the exact
    //  state without a tick; a new match resets it. No renderer, Update() or UI state here.
    // ===========================================================================================
    public static class SandstormState
    {
        private sealed class Slot
        {
            public int Id;
            public SandstormPhase Phase;
            public int Age;
            public int Course;
            public List<HexCoord> Body = new List<HexCoord>();
            public int NextSpawnRound;
            public SandstormRandom Random;

            public Slot Copy() => new Slot
            {
                Id = Id, Phase = Phase, Age = Age, Course = Course, Body = new List<HexCoord>(Body),
                NextSpawnRound = NextSpawnRound, Random = Random,
            };
        }

        private static MatchWeatherConditions? _conditions;
        private static int _lastRound;
        private static List<Slot> _slots = new List<Slot>();
        private static SandstormSnapshot _snapshot = SandstormSnapshot.Clear;
        private static int _revision;
        private static readonly List<string> _diagnostics = new List<string>();

        // One notification per published geometry change (all instances at once).
        public static event Action<SandstormSnapshot> Changed;

        public static bool IsInitialized => _conditions.HasValue;
        public static MatchWeatherConditions? Conditions => _conditions;
        public static int LastAppliedRound => _lastRound;
        public static SandstormSnapshot Snapshot => _snapshot;
        // Why a slot stayed in place or waited for a birth on the last tick (normally empty).
        public static IReadOnlyList<string> LastTickDiagnostics => _diagnostics;

        public static void Initialize(MatchWeatherConditions conditions, HexMap map)
        {
            if (_conditions.HasValue)
                throw new InvalidOperationException("Weather is already initialized for this match.");
            _conditions = conditions;
            _lastRound = 0;
            _slots = new List<Slot>();
            for (int id = 0; id < SandstormRules.ConfiguredCount(conditions.Profile); id++)
                _slots.Add(new Slot
                {
                    Id = id, Phase = SandstormPhase.WaitingForSpawn, NextSpawnRound = 1,
                    Random = SandstormRandom.ForSlot(conditions.Seed, id),
                });
            _diagnostics.Clear();
            Publish(map, forceNotify: true);
        }

        public static void ResetForMatch()
        {
            _conditions = null;
            _lastRound = 0;
            _slots = new List<Slot>();
            _snapshot = SandstormSnapshot.Clear;
            _revision = 0;
            _diagnostics.Clear();
            Changed = null;
        }

        // Advances every slot to `round` (the new full-round number) and publishes the result.
        // The same round again is a no-op (age, area and random untouched); an earlier round is
        // rejected. Returns true when a new state was applied.
        public static bool AdvanceRound(HexMap map, int round) =>
            AdvanceRound(map != null ? map.AllCoords : Enumerable.Empty<HexCoord>(), round, map);

        internal static bool AdvanceRound(IEnumerable<HexCoord> mapCoords, int round, HexMap map = null)
        {
            if (!_conditions.HasValue) return false;
            if (round == _lastRound) return false;
            if (round < _lastRound)
                throw new InvalidOperationException($"Weather cannot go back from round {_lastRound} to {round}.");

            var topology = new SandstormRules.Topology(mapCoords);
            var memo = new SandstormRules.ContinuationMemo();
            var work = _slots.Select(s => s.Copy()).ToList();
            var notes = new List<string>();

            // Phase 1: existing storms disappear, expand and advance; pauses run out.
            foreach (Slot slot in work)
            {
                switch (slot.Phase)
                {
                    case SandstormPhase.Active:
                        if (slot.Age >= SandstormRules.LastActiveAge)
                        {
                            slot.Phase = SandstormPhase.Cooldown;
                            slot.Body.Clear();
                            slot.Age = 0;
                            slot.NextSpawnRound = round + SandstormRules.CooldownRounds;
                            break;
                        }
                        slot.Age++;
                        if (slot.Age == 1)
                        {
                            HexCoord centre = SandstormRules.CentreOf(slot.Body);
                            if (SandstormRules.TryExpand(topology, centre, slot.Course, out List<HexCoord> expanded))
                                slot.Body = expanded;
                            else
                                notes.Add($"storm {slot.Id}: no room to grow its spot around {centre}");
                        }
                        if (SandstormRules.TryPlanAdvance(topology, slot.Body, slot.Course,
                                SandstormRules.LastActiveAge - slot.Age, ref slot.Random, memo,
                                out List<HexCoord> moved, out int course, out _))
                        {
                            slot.Body = moved;
                            slot.Course = course;
                        }
                        else
                        {
                            notes.Add($"storm {slot.Id}: no legal 2-3 hex advance from {slot.Body[slot.Body.Count - 1]} (course {slot.Course}); stays this round");
                        }
                        break;
                    case SandstormPhase.Cooldown:
                        if (round >= slot.NextSpawnRound) slot.Phase = SandstormPhase.WaitingForSpawn;
                        break;
                }
            }

            // Phase 2: births in slot order; the half spot never covers a hex another storm now covers.
            var occupied = new HashSet<HexCoord>(work.Where(s => s.Phase == SandstormPhase.Active).SelectMany(s => s.Body));
            foreach (Slot slot in work)
            {
                if (slot.Phase != SandstormPhase.WaitingForSpawn || round < slot.NextSpawnRound) continue;
                var candidates = new List<(HexCoord Hex, List<int> Courses)>();
                foreach (HexCoord hex in topology.SortedInterior)
                {
                    if (occupied.Contains(hex)) continue;
                    List<int> courses = SandstormRules.BirthCourses(topology, hex, memo);
                    courses.RemoveAll(c => SandstormRules.SpotCells(hex, c, grown: false).Any(occupied.Contains));
                    if (courses.Count > 0) candidates.Add((hex, courses));
                }
                if (candidates.Count == 0)
                {
                    slot.NextSpawnRound = round + 1;
                    notes.Add($"storm {slot.Id}: no legal birth hex; waits for the next round");
                    continue;
                }
                var pick = candidates[slot.Random.Next(candidates.Count)];
                slot.Phase = SandstormPhase.Active;
                slot.Age = 0;
                slot.Course = pick.Courses[slot.Random.Next(pick.Courses.Count)];
                slot.Body = SandstormRules.SpotCells(pick.Hex, slot.Course, grown: false);
                slot.NextSpawnRound = 0;
                occupied.UnionWith(slot.Body);
            }

            bool geometryChanged = work.Count != _slots.Count
                || work.Where((s, i) => !s.Body.SequenceEqual(_slots[i].Body)).Any();
            _slots = work;
            _lastRound = round;
            _diagnostics.Clear();
            _diagnostics.AddRange(notes);
            foreach (string note in notes)
                UnityEngine.Debug.LogWarning("[Weather] round " + round + ": " + note);
            if (geometryChanged) _revision++;
            Publish(map, geometryChanged);
            return true;
        }

        private static void Publish(HexMap map, bool forceNotify)
        {
            var profile = _conditions?.Profile ?? MatchWeatherProfile.Clear;
            _snapshot = new SandstormSnapshot(profile, _revision,
                _slots.Select(s => new SandstormInstanceView(s.Id, s.Phase, s.Age, s.Course, s.Body)));
            map?.PublishWeather(_snapshot);
            if (forceNotify) Changed?.Invoke(_snapshot);
        }

        // ---- Save / load ---------------------------------------------------------------------------
        public static SandstormStateData CaptureState()
        {
            if (!_conditions.HasValue) throw new InvalidOperationException("Weather is not initialized for this match.");
            var data = new SandstormStateData
            {
                profile = (int)_conditions.Value.Profile, seed = _conditions.Value.Seed, lastRound = _lastRound,
            };
            foreach (Slot slot in _slots)
            {
                var saved = new SandstormSlotData
                {
                    id = slot.Id, phase = (int)slot.Phase, age = slot.Age, course = slot.Course,
                    nextSpawnRound = slot.NextSpawnRound, random = unchecked((long)slot.Random.State),
                };
                foreach (HexCoord hex in slot.Body) { saved.bodyQ.Add(hex.Q); saved.bodyR.Add(hex.R); }
                data.slots.Add(saved);
            }
            return data;
        }

        // Full structural validation against the map's coordinates; throws ArgumentException.
        // Overlap between different slots is valid; duplicates inside one body are not.
        public static MatchWeatherConditions Validate(SandstormStateData data, IEnumerable<HexCoord> mapCoords)
        {
            if (data == null || data.slots == null) throw new ArgumentException("Missing weather state.");
            var profile = (MatchWeatherProfile)data.profile;
            if (!MatchWeatherConditions.IsValid(profile)) throw new ArgumentException("Unknown weather profile.");
            if (data.lastRound < 0) throw new ArgumentException("Invalid weather round.");
            int count = SandstormRules.ConfiguredCount(profile);
            if (data.slots.Count != count) throw new ArgumentException("Weather slot count does not match its profile.");
            var topology = new SandstormRules.Topology(mapCoords);
            for (int i = 0; i < data.slots.Count; i++)
            {
                SandstormSlotData slot = data.slots[i];
                if (slot == null || slot.id != i || slot.bodyQ == null || slot.bodyR == null || slot.bodyQ.Count != slot.bodyR.Count)
                    throw new ArgumentException("Invalid weather slot.");
                var body = slot.bodyQ.Select((q, k) => new HexCoord(q, slot.bodyR[k])).ToList();
                switch ((SandstormPhase)slot.phase)
                {
                    case SandstormPhase.Active:
                        if (slot.age < 0 || slot.age > SandstormRules.LastActiveAge || slot.course < 0 || slot.course > 5
                            || data.lastRound - slot.age < 1 || slot.nextSpawnRound != 0
                            || !SandstormRules.IsValidBody(topology, body, slot.course, grown: slot.age > 0))
                            throw new ArgumentException("Invalid active storm.");
                        break;
                    case SandstormPhase.Cooldown:
                        if (body.Count != 0 || slot.age != 0 || slot.nextSpawnRound <= data.lastRound
                            || slot.nextSpawnRound > data.lastRound + SandstormRules.CooldownRounds)
                            throw new ArgumentException("Invalid storm pause.");
                        break;
                    case SandstormPhase.WaitingForSpawn:
                        if (body.Count != 0 || slot.age != 0 || slot.nextSpawnRound < 1
                            || slot.nextSpawnRound > data.lastRound + 1)
                            throw new ArgumentException("Invalid storm spawn wait.");
                        break;
                    default:
                        throw new ArgumentException("Unknown storm phase.");
                }
            }
            return new MatchWeatherConditions(profile, data.seed);
        }

        // Restores the exact saved slots (bodies, ages, pauses, random streams) without a tick
        // and without choosing anything new.
        public static void RestoreState(HexMap map, SandstormStateData data) =>
            RestoreState(map != null ? map.AllCoords : Enumerable.Empty<HexCoord>(), data, map);

        internal static void RestoreState(IEnumerable<HexCoord> mapCoords, SandstormStateData data, HexMap map = null)
        {
            MatchWeatherConditions conditions = Validate(data, mapCoords);
            _conditions = conditions;
            _lastRound = data.lastRound;
            _slots = data.slots.Select(x => new Slot
            {
                Id = x.id, Phase = (SandstormPhase)x.phase, Age = x.age, Course = x.course,
                Body = x.bodyQ.Select((q, k) => new HexCoord(q, x.bodyR[k])).ToList(),
                NextSpawnRound = x.nextSpawnRound, Random = new SandstormRandom(unchecked((ulong)x.random)),
            }).ToList();
            _revision++;
            _diagnostics.Clear();
            Publish(map, forceNotify: true);
        }
    }
}
