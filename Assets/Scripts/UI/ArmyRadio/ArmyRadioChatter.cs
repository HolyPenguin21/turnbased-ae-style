using System;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.UI
{
    public enum ArmyRadioSituation { Contact, Waiting, Moving, Garrison, Airborne, Stealth, Damaged, Ready }

    [Serializable]
    public sealed class ArmyRadioChatterSettings
    {
        public bool enabled = true;
        [Min(5f)] public float ambientInterval = 18f;
        [Min(1f)] public float minimumGap = 3f;
        [Min(10f)] public float armyCooldown = 45f;
        [Min(2f)] public float displaySeconds = 5f;
        [Min(1f)] public float waitingSeconds = 12f;
        [Range(1, 3)] public int maximumMessages = 3;
        [Min(.05f)] public float fadeInSeconds = .18f;
        [Min(.05f)] public float fadeOutSeconds = .28f;
        [Min(10f)] public float jokeIdleSeconds = 60f;
        [Range(0f, 1f)] public float jokeChance = .3f;
        [Range(0f, 1f)] public float dialogueChance = .25f;
        [Min(.5f)] public float replyDelay = 2f;
        [Header("Radio dispatch")]
        [Range(0f, 1f)] public float criticalChance = .6f;
        [Range(0f, 1f)] public float operationalChance = .3f;
        [Range(0f, 1f)] public float situationalChance = .2f;
        [Range(0f, 1f)] public float ambientChance = .1f;
        [Range(1, 6)] public int maximumPerMinute = 3;
        [Range(1, 64)] public int maximumPendingCandidates = 24;
        [Min(1f)] public float operationalTtl = 10f;
        [Min(1f)] public float minorEventTtl = 6f;
        [Min(1f)] public float categoryCooldown = 25f;
        [Range(1, 64)] public int recentPhraseHistorySize = 16;
    }

    // Cosmetic read-only policy. No tactical evaluation, purchases, or random game rolls.
    public static partial class ArmyRadioChatter
    {
        public static UnitData FirstUnit(ArmyData army)
        {
            if (army == null) return null;
            foreach (UnitData unit in army.Members)
                if (unit != null && !unit.IsPrisoner && unit.HitPointsCurrent > 0) return unit;
            return null;
        }

        public static Sprite DetailArt(UnitData unit) => unit == null ? null
            : unit.OriginatingCard != null && unit.OriginatingCard.detailArt != null
                ? unit.OriginatingCard.detailArt : unit.DetailArt;

        public static bool CanSpeak(ArmyData army, PlayerSetupData listener) =>
            army != null && listener != null && army.Owner == listener && !listener.IsNeutral
            && !listener.IsEliminated && !army.IsPrison && FirstUnit(army) != null;

        public static bool SeesEnemy(ArmyData army)
            => Observe(army, null);

        // Contact and base discovery share one observer-authorized range scan.
        public static bool Observe(ArmyData army, Action<BuildingData> visibleBase)
        {
            if (army == null || army.Owner == null) return false;
            bool contact = false;
            HexCoord origin = army.Controller != null ? army.Controller.CurrentHex : army.Hex;
            foreach (HexCoord hex in HexGridMath.HexesInRange(origin, VisionSystem.ArmyVisionRadius(army)))
            {
                if (!VisionSystem.IsVisible(army.Owner, hex)) continue;
                if (visibleBase != null) visibleBase(BuildingRegistry.FindAt(hex));
                foreach (ArmyData other in ArmyRegistry.AllAt(hex))
                    if (other != army && other.Owner != army.Owner && !other.IsPrison
                        && FirstUnit(other) != null && (other.Controller == null || !other.Controller.IsMoving)
                        && StealthSystem.HasAnyTargetableMember(other, army.Owner))
                    { if (visibleBase == null) return true; contact = true; }
            }
            return contact;
        }

        public static bool CanIdleFlavor(ArmyData army)
        {
            if (army == null || StealthSystem.IsArmyFullyHidden(army)) return false;
            foreach (var unit in army.Members)
                if (unit != null && unit.HitPointsCurrent > 0 && unit.HitPointsCurrent < unit.HitPointsMax) return false;
            return true;
        }
        public static ArmyRadioSituation Situation(ArmyData army, bool contact, bool moved, float quietSeconds)
        {
            if (contact) return ArmyRadioSituation.Contact;
            foreach (UnitData unit in army.Members)
                if (unit != null && unit.HitPointsCurrent > 0 && unit.HitPointsCurrent < unit.HitPointsMax)
                    return ArmyRadioSituation.Damaged;
            if (StealthSystem.IsArmyFullyHidden(army)) return ArmyRadioSituation.Stealth;
            if (army.IsAirArmy) return ArmyRadioSituation.Airborne;
            if (army.IsGarrison || army.IsAirfield) return ArmyRadioSituation.Garrison;
            if (moved || army.HasActivatedThisTurn) return ArmyRadioSituation.Moving;
            return quietSeconds > 0f ? ArmyRadioSituation.Waiting : ArmyRadioSituation.Ready;
        }

        private static readonly string[][] English =
        {
            new[] { "Enemy in sight. Awaiting orders.", "Contact reported. Weapons ready.", "Eyes on the enemy. Standing by.", "Visual contact. Keeping watch.", "We've got company. They look unfriendly.", "Movement ahead. Definitely not the wind.", "Hostiles spotted. Your call, command.", "Enemy position confirmed. Holding fire.", "Heads up. Trouble on the horizon.", "Contact. Everyone keep your helmets on.", "We see them. Waiting for the green light.", "Opposition ahead. Staying alert.", "Eyes forward. This sector has company.", "Enemy nearby. Radio stays open." },
            new[] { "Holding position. Awaiting orders.", "Still here. Still breathing.", "Standing by. The dust isn't going anywhere.", "Quiet sector. Keeping watch.", "Any orders? The sand is getting comfortable.", "No movement here. Just the tumbleweeds.", "We've memorized this patch of dirt.", "Holding steady. Boots could use a walk.", "Same position. New layer of dust.", "Waiting on your signal, command.", "Watch continues. Nobody wander off.", "No orders yet. Keeping the channel open.", "Still on station. Still paying attention.", "Standing watch. Let us know when." },
            new[] { "Position reached. Standing by.", "Boots full of sand. Still operational.", "We're here. What's next?", "New position. Taking a look around.", "Radio check. All accounted for.", "Move complete. Catching our breath.", "Made it. Checking the horizon.", "New patch of wasteland, same old dust.", "On location. Waiting for the next order.", "Arrived in one piece. Mostly clean boots.", "We've stopped. Keeping our eyes open.", "Position updated. Channel open." },
            new[] { "Garrison reporting. Keeping watch.", "Our post is covered.", "Perimeter watch. Radio standing by.", "Home station. Standing by.", "Coffee's cold. Watch continues.", "Watching the fence. Fence watching us.", "Post occupied. Awaiting instructions.", "Keeping an eye on the approaches.", "On guard. Don't forget the password.", "Station reporting. Nothing to add yet.", "Same walls. Same watch. Still awake.", "Holding the post. Radio check complete." },
            new[] { "Air wing on frequency.", "Flight crew reporting. Standing by.", "Air wing ready for orders.", "Radio check from the flight crew.", "Pilot here. Keeping an eye on things.", "Cockpit radio checks out.", "Flight crew waiting for the next call.", "Wings on standby. Command, over.", "Pilot on channel. What's the plan?", "Flight crew accounted for.", "Air wing checking in. We hear you.", "Crew standing by. Eyes on the horizon." },
            new[] { "Keeping a low profile.", "Quiet on the radio. Quiet on the ground.", "Staying out of sight.", "Low voices. Eyes open.", "We're just another patch of dust.", "Short transmission. Still concealed.", "Keeping our heads down.", "Whispering into the radio. Standing by.", "No sudden moves. Staying quiet.", "Shadows make good neighbors.", "Under cover. Awaiting your signal.", "Brief check-in. Back to silence." },
            new[] { "Took some damage. Still here.", "Could use repairs. Standing by.", "A little battered. Radio still works.", "Damage report sent. Awaiting orders.", "Dents are temporary. Probably.", "We've looked better. Keeping watch.", "Repair tape is doing its best.", "Some wear and tear. Orders, command?", "Damage noted. Nobody likes the dents.", "Still on frequency. A bit worse for wear.", "Could use a quiet minute and a toolkit.", "Battered, but listening." },
            new[] { "Radio check. Loud and clear.", "Standing by for orders.", "All eyes open. Reporting in.", "Another day in the dust.", "On frequency. Try not to lose us.", "Command, we hear you.", "Crew checking in. What's next?", "Radio works. Boots fit. Awaiting orders.", "Watching the horizon. Channel open.", "Here and listening.", "Checking equipment. Waiting for your call.", "Another shift in the wasteland." },
        };

        private static readonly string[] Jokes =
        {
            "Before the end, people paid for bottled water. Now we pay for the bottle.",
            "Found a pre-war survival guide. Step one: don't have an apocalypse.",
            "The bunker has a five-star rating. All five stars are holes in the roof.",
            "Our cook says the soup is organic. It moved on its own, so I believe him.",
            "Good news: the road is free of traffic. Bad news: it's also free of road.",
            "Asked a trader for something fresh. He sold me a fresh excuse.",
            "My boots are waterproof. They keep all the water inside.",
            "We found a working clock. Finally, we can be late professionally.",
            "The old world had cloud storage. We have a leaking shed.",
            "A scavenger walked into a bar. Took the bar. Good scrap metal.",
            "Our shelter is open plan. That's what we call missing walls now.",
            "The dentist accepts scrap. Unfortunately, so do my teeth.",
            "The ration says best before the war. Very optimistic packaging.",
            "We joined a recycling program. Apparently, we're the program.",
            "Found a luxury mattress. Only two springs tried to kill me.",
            "The geiger counter has a beat. Finally, music everyone can dance away from.",
            "Our mechanic offers a lifetime warranty. He won't say whose lifetime.",
            "Pre-war coffee is priceless. Mostly because nobody knows what it was.",
            "The wasteland gym is free. Just carry everything you own forever.",
            "We planted a garden. The weeds filed a territorial claim.",
            "Our map says you are here. Nice to meet someone so confident.",
            "Found a vacuum cleaner. Plenty of dust, no electricity. Perfect partnership.",
            "The bunker newsletter is out. One page: still underground.",
            "They promised hot showers at the base. Turns out it's a weather forecast.",
            "I miss online shopping. Now every delivery quest comes with raiders.",
        };

        private static readonly string[][] Dialogues =
        {
            new[] { "Neighbor, got any coffee?", "Depends. Do you count warm brown water?" },
            new[] { "Radio check. Can you hear us?", "Loud and clear. So can the whole wasteland." },
            new[] { "Anything moving over there?", "Just our patience. It's leaving." },
            new[] { "What are you having for dinner?", "The same tin, with a different label." },
            new[] { "How's the view from your position?", "Dust on the left. Premium dust on the right." },
            new[] { "Anyone remember what weekends were?", "Two days of waiting, with better chairs." },
            new[] { "Got a spare filter?", "One. It's been promoted to family heirloom." },
            new[] { "You seeing that tumbleweed?", "Yes. It has better route planning than us." },
            new[] { "Who's taking the next watch?", "The one who asks fewer questions." },
            new[] { "Your radio sounds better than ours.", "We hit it on the other side this morning." },
            new[] { "Think it will rain?", "Sure. The roof has been waiting for its moment." },
            new[] { "Neighbor, still awake?", "Yes. This dirt isn't going to watch itself." },
        };

        public static int PhraseCount(ArmyRadioSituation situation) => English[(int)situation].Length;
        public static int JokeCount => Jokes.Length;
        public static int DialogueCount => Dialogues.Length + AdditionalDialogues.Length;
        public static string Phrase(ArmyRadioSituation situation, int variation) =>
            English[(int)situation][Index(variation, PhraseCount(situation))];
        public static string Joke(int variation) => Jokes[Index(variation, Jokes.Length)];
        public static string Dialogue(int variation, bool reply) => Index(variation, DialogueCount) < Dialogues.Length
                ? Dialogues[Index(variation, DialogueCount)][reply ? 1 : 0]
                : AdditionalDialogues[Index(variation, DialogueCount) - Dialogues.Length][reply ? 1 : 0];
        private static int Index(int variation, int count) => (variation % count + count) % count;
    }
}
