using System.Collections.Generic;
using UnityEngine;

namespace Game.Combat
{
    // The actual AiЕhoughts_Text content — English, personal, a little dry/funny where it fits,
    // per the tone samples agreed with the user. One random line per category per trigger (see
    // BattleAiThoughtsUI for how these get displayed — "freshest wins", never a stale backlog).
    public static class BattleAiPhraseBank
    {
        private static readonly Dictionary<AiThoughtCategory, string[]> Phrases = new Dictionary<AiThoughtCategory, string[]>
        {
            [AiThoughtCategory.FightDecision] = new[]
            {
                "The odds are in our favor. Let's finish this quickly.",
                "We can win this. Hold the line.",
                "Not our first fight, won't be our last. Advance.",
                "I like our chances here. Stay sharp.",
                "This one's winnable. No mistakes now.",
                "We came prepared for exactly this. Engage.",
            },
            [AiThoughtCategory.RetreatDecision] = new[]
            {
                "This isn't a fight worth finishing. Fall back.",
                "We're not winning this one. Live to fight another day.",
                "Cut our losses. Pull everyone back.",
                "The math doesn't favor us. Retreat, now.",
                "No shame in walking away from a bad trade.",
                "Preserve the army. We'll pick a better fight.",
            },
            [AiThoughtCategory.CitadelDefense] = new[]
            {
                "This is our ground. We hold, no matter the cost.",
                "They want the citadel? They'll have to take it from our cold hands.",
                "Retreat isn't on the table. Not here.",
                "Every wall behind us is a wall we don't lose. Fight.",
                "This is home. We don't run from home.",
                "Whatever it costs, we're not giving this up.",
            },
            [AiThoughtCategory.FinishingBlow] = new[]
            {
                "One more hit and that's the end of it.",
                "It's barely standing. Finish it.",
                "Almost done here — don't let it slip away.",
                "This one's mine. Watch.",
                "Weak, wounded, and about to be gone.",
                "Close the deal. No mercy.",
            },
            [AiThoughtCategory.PriorityTarget] = new[]
            {
                "That one hits too hard to ignore. Dealing with it first.",
                "Neutralize the real threat before it does any more damage.",
                "Everyone else can wait — this one's dangerous.",
                "If I don't stop it now, it'll cost us later.",
                "The biggest hammer on the field just became my problem.",
                "Focus fire. That thing is the actual danger here.",
            },
            [AiThoughtCategory.UselessTargetSkip] = new[]
            {
                "No point hitting that — the armor laughs it off.",
                "Wasting an attack there gets us nowhere. Picking a real target.",
                "That one's not worth the swing.",
                "Can't crack that shell. Moving on.",
                "Bad trade. Skipping it.",
                "Not today — that target's a dead end.",
            },
            [AiThoughtCategory.AdvanceMove] = new[]
            {
                "Closing the distance.",
                "Getting into position to actually do something.",
                "No use standing around. Moving up.",
                "Advancing — can't fight from back here.",
                "Time to close the gap.",
                "Pushing forward.",
            },
            [AiThoughtCategory.CautiousWait] = new[]
            {
                "Not yet. Let them come to us.",
                "Holding position — no reason to walk into that.",
                "Patience. The opening will come.",
                "Staying put for now.",
                "That's a bad spot to move into. Waiting.",
                "No rush. Let's see what they do first.",
            },
            [AiThoughtCategory.ForcedAdvance] = new[]
            {
                "Enough waiting. Moving up, risk or not.",
                "Can't stall forever. Here we go.",
                "Time's up on being careful.",
                "Standing still hasn't helped. Changing that now.",
                "No more hesitation.",
                "Alright, enough patience. Advancing.",
            },
            [AiThoughtCategory.FateSpendWorthIt] = new[]
            {
                "Worth the risk — rerolling that one.",
                "Spending Fate here. This matters.",
                "One more roll. We need this.",
                "Burning some luck on this — it counts.",
                "This is exactly what Fate is for.",
                "Rerolling. Can't let this one stand.",
            },
            [AiThoughtCategory.FateSpendSkip] = new[]
            {
                "Not worth spending Fate on this.",
                "Save it. This roll isn't important enough.",
                "Letting this one go — Fate's better spent elsewhere.",
                "Not every fight needs a reroll.",
                "Holding onto our luck for now.",
                "This isn't the moment to burn Fate.",
            },
            [AiThoughtCategory.DamageTakenMinor] = new[]
            {
                "That stung, but we're fine.",
                "Took a hit. Nothing serious.",
                "Barely felt that.",
                "We'll shake that off.",
                "Scratched, not stopped.",
                "That one left a mark, not a wound.",
            },
            [AiThoughtCategory.DamageTakenMajor] = new[]
            {
                "...That one hurt. A lot.",
                "We're bleeding badly here.",
                "This is getting dangerous fast.",
                "That's a serious hit to absorb.",
                "We need this fight to turn around, quickly.",
                "That one nearly finished us.",
            },
            [AiThoughtCategory.GoodRoll] = new[]
            {
                "Clean hit. Didn't even need the armor check.",
                "That's exactly how it's supposed to go.",
                "Perfect timing on that roll.",
                "Couldn't have asked for better dice.",
                "That landed hard.",
                "Beautiful. Just beautiful.",
            },
            [AiThoughtCategory.BadRoll] = new[]
            {
                "Of course it missed. Of course.",
                "Not our day for dice, apparently.",
                "That should've landed. Should have.",
                "Rough luck there.",
                "We'll get the next one.",
                "That one just wasn't meant to be.",
            },
            [AiThoughtCategory.UnitDied] = new[]
            {
                "We lost someone. Make it count.",
                "Down goes one of ours. Keep fighting.",
                "That's a price we didn't want to pay.",
                "We'll mourn later. Right now, we fight.",
                "One less on our side. Stay sharp.",
                "That loss will be remembered.",
            },
            [AiThoughtCategory.EnemyKilled] = new[]
            {
                "One down. Who's next?",
                "That's one less problem.",
                "Scratch that one off the list.",
                "Down they go.",
                "Their line just got a little thinner.",
                "That's how it's done.",
            },
            [AiThoughtCategory.BattleWon] = new[]
            {
                "It's over. We held.",
                "Victory. Hard fought, but ours.",
                "They're finished. We're still standing.",
                "That's a win we'll remember.",
                "The field is ours.",
                "Well fought, all of us.",
            },
            [AiThoughtCategory.BattleLost] = new[]
            {
                "We couldn't hold. Regroup.",
                "This one's lost. We'll come back stronger.",
                "Not our day. Withdraw and recover.",
                "Defeat, but not the end.",
                "We'll learn from this one.",
                "The field is theirs. For now.",
            },
            [AiThoughtCategory.PlayerIdle] = new[]
            {
                "Take your time. I'll just be here. Plotting.",
                "No rush. Really.",
                "Still thinking? I'll wait.",
                "I've got nowhere else to be.",
                "Take all the time you need... within reason.",
                "Just casually waiting over here.",
            },
        };

        // A hero-less army has no single "voice" narrating (see BattleAiThoughtsUI.Show — no
        // name prefix without a hero), so its lines read as flat, impersonal status reports
        // instead of the personal "I/we" commentary above — per the user's own spec: more
        // generic for armies without a hero. Only covers categories that can actually fire for a
        // hero-less side; FateSpendWorthIt/Skip never do (no hero means no Fate to spend at all,
        // see BattleAttackPopupUI.RunAiFateSpend's own early-out), and PlayerIdle is already
        // unprefixed/impersonal for both cases, so neither needs an entry here. Falls back to
        // the personal pool above when a category has no entry (see GetRandomPhrase).
        private static readonly Dictionary<AiThoughtCategory, string[]> NoHeroPhrases = new Dictionary<AiThoughtCategory, string[]>
        {
            [AiThoughtCategory.FightDecision] = new[]
            {
                "The army presses the attack.",
                "Odds favor engagement. Proceeding.",
                "Standing ground — the numbers work here.",
                "Committing to the fight.",
            },
            [AiThoughtCategory.RetreatDecision] = new[]
            {
                "The army falls back.",
                "Withdrawing — the numbers don't favor a fight.",
                "Pulling out while it still can.",
                "Retreat ordered. Preserving what's left.",
            },
            [AiThoughtCategory.CitadelDefense] = new[]
            {
                "The garrison holds its ground. No retreat.",
                "This position does not fall.",
                "Defenders dig in. No withdrawal.",
                "The line holds, whatever it costs.",
            },
            [AiThoughtCategory.FinishingBlow] = new[]
            {
                "Target critically weakened. Pressing the attack.",
                "One more strike ends it.",
                "Closing out the kill.",
                "The target won't last another hit.",
            },
            [AiThoughtCategory.PriorityTarget] = new[]
            {
                "Redirecting fire to the greater threat.",
                "Neutralizing the dangerous unit first.",
                "Focus shifts to the bigger risk.",
                "The strongest threat is targeted.",
            },
            [AiThoughtCategory.UselessTargetSkip] = new[]
            {
                "Target's defenses are too strong. Reassigning.",
                "No effective attack available. Switching targets.",
                "That target can't be meaningfully damaged.",
                "Redirecting — this one's not worth it.",
            },
            [AiThoughtCategory.AdvanceMove] = new[]
            {
                "The unit advances.",
                "Closing the distance to the enemy.",
                "Moving into engagement range.",
                "Advancing on the enemy line.",
            },
            [AiThoughtCategory.CautiousWait] = new[]
            {
                "Holding position.",
                "The unit waits for a better opening.",
                "No advance — the risk isn't worth it yet.",
                "Standing by.",
            },
            [AiThoughtCategory.ForcedAdvance] = new[]
            {
                "Advancing regardless of risk.",
                "No more delay. Moving in.",
                "The unit commits to the advance.",
                "Caution abandoned — advancing now.",
            },
            [AiThoughtCategory.DamageTakenMinor] = new[]
            {
                "Minor damage sustained.",
                "The unit takes a light hit.",
                "Damage recorded — negligible.",
                "A glancing blow. No real harm.",
            },
            [AiThoughtCategory.DamageTakenMajor] = new[]
            {
                "Heavy damage sustained.",
                "The unit is critically damaged.",
                "Serious losses taken from that strike.",
                "That hit did real damage.",
            },
            [AiThoughtCategory.GoodRoll] = new[]
            {
                "Favorable outcome on that roll.",
                "The attack connects cleanly.",
                "A strong result there.",
                "That roll went well.",
            },
            [AiThoughtCategory.BadRoll] = new[]
            {
                "The attack fails to connect.",
                "Unfavorable roll.",
                "That attempt came up short.",
                "No effect from that roll.",
            },
            [AiThoughtCategory.UnitDied] = new[]
            {
                "A unit has fallen.",
                "Losses reported.",
                "One of ours is down.",
                "The army suffers a loss.",
            },
            [AiThoughtCategory.EnemyKilled] = new[]
            {
                "Enemy unit eliminated.",
                "One less enemy on the field.",
                "Target destroyed.",
                "Enemy strength reduced.",
            },
            [AiThoughtCategory.BattleWon] = new[]
            {
                "The battle is won.",
                "Enemy forces defeated.",
                "Victory achieved.",
                "The field is secured.",
            },
            [AiThoughtCategory.BattleLost] = new[]
            {
                "The battle is lost.",
                "Forces defeated. Withdrawing survivors.",
                "This engagement is over.",
                "Defeat. Regrouping elsewhere.",
            },
        };

        // New, additive-only phrase set — a specific unit's name dropped into a `{0}` slot, for
        // whichever categories naturally have one available at the call site (a target being
        // attacked, a unit that just died/killed, who dealt/took damage, who a roll or Fate
        // spend was against). None of the phrases above were touched or replaced; these are
        // picked from a combined pool alongside them (see GetRandomPhrase) only when a caller
        // actually has a name to give.
        private static readonly Dictionary<AiThoughtCategory, string[]> NamedPhrases = new Dictionary<AiThoughtCategory, string[]>
        {
            [AiThoughtCategory.FinishingBlow] = new[]
            {
                "{0} is barely standing. Finishing it.",
                "One more hit and {0} is done.",
                "{0} won't survive this.",
                "This is the end for {0}.",
            },
            [AiThoughtCategory.PriorityTarget] = new[]
            {
                "{0} hits too hard to ignore. Focusing it.",
                "{0} is the real threat here. Dealing with it.",
                "Everyone else can wait — {0} goes first.",
                "{0} just became my problem.",
            },
            [AiThoughtCategory.UselessTargetSkip] = new[]
            {
                "No point hitting {0} — the armor laughs it off.",
                "{0}'s not worth the swing.",
                "Can't crack {0}'s shell. Moving on.",
                "Skipping {0}. Not today.",
            },
            [AiThoughtCategory.EnemyKilled] = new[]
            {
                "{0} is down. Who's next?",
                "That's the end of {0}.",
                "{0} won't be a problem any more.",
                "Scratch {0} off the list.",
            },
            [AiThoughtCategory.UnitDied] = new[]
            {
                "We lost {0}. Make it count.",
                "{0} is down. Keep fighting.",
                "{0} won't be forgotten.",
                "That's a price we didn't want to pay — goodbye, {0}.",
            },
            [AiThoughtCategory.DamageTakenMinor] = new[]
            {
                "{0} barely touched us.",
                "{0}'s hit stung, but we're fine.",
                "That's the best {0} can do?",
                "{0} left a mark, not a wound.",
            },
            [AiThoughtCategory.DamageTakenMajor] = new[]
            {
                "{0} hit us hard. Really hard.",
                "{0} is doing real damage here.",
                "We need an answer for {0}, fast.",
                "{0} nearly finished us with that one.",
            },
            [AiThoughtCategory.GoodRoll] = new[]
            {
                "Clean hit on {0}.",
                "{0} didn't see that one coming.",
                "That landed hard on {0}.",
                "Perfect timing against {0}.",
            },
            [AiThoughtCategory.BadRoll] = new[]
            {
                "Should've landed on {0}. Should have.",
                "{0} gets lucky this time.",
                "Not our day against {0}, apparently.",
                "{0} shrugs that one off.",
            },
            [AiThoughtCategory.FateSpendWorthIt] = new[]
            {
                "Worth the risk against {0} — rerolling.",
                "Burning Fate to deal with {0}. It counts.",
                "{0} isn't getting away that easy. Rerolling.",
                "This matters — spending Fate on {0}.",
            },
            [AiThoughtCategory.FateSpendSkip] = new[]
            {
                "Not worth spending Fate on {0}.",
                "{0} isn't important enough for a reroll.",
                "Saving our luck — {0} can wait.",
                "Not the moment to burn Fate on {0}.",
            },
        };

        // unitName is optional — omitted (or the category has no named variants at all) just
        // falls back to the plain pool, unchanged from before named phrases were added. When
        // given, named and plain phrases are pooled together (weighted by how many of each
        // exist) rather than always preferring one over the other, so named callouts show up
        // often without crowding out the general lines.
        //
        // hasHero picks which PLAIN pool that "general lines" pool actually is: the personal
        // Phrases dictionary for a side with a hero narrating, or the impersonal NoHeroPhrases
        // one otherwise (falling back to Phrases if a category has no no-hero entry at all — see
        // NoHeroPhrases's own comment on which categories that's true for). Named phrases are
        // shared either way — most read fine without a personal pronoun regardless.
        public static string GetRandomPhrase(AiThoughtCategory category, string unitName = null, bool hasHero = true)
        {
            string[] plain = null;
            if (hasHero || !NoHeroPhrases.TryGetValue(category, out plain))
                Phrases.TryGetValue(category, out plain);
            string[] named = !string.IsNullOrEmpty(unitName) && NamedPhrases.TryGetValue(category, out string[] n) ? n : null;

            int plainCount = plain?.Length ?? 0;
            int namedCount = named?.Length ?? 0;
            int total = plainCount + namedCount;
            if (total == 0)
                return string.Empty;

            int index = Random.Range(0, total);
            string phrase = index < plainCount ? plain[index] : named[index - plainCount];
            return phrase.Contains("{0}") ? string.Format(phrase, unitName) : phrase;
        }

        // Total phrase count across every category — reported once, not meant for runtime use.
        public static int TotalPhraseCount()
        {
            int total = 0;
            foreach (string[] options in Phrases.Values)
                total += options.Length;
            foreach (string[] options in NoHeroPhrases.Values)
                total += options.Length;
            foreach (string[] options in NamedPhrases.Values)
                total += options.Length;
            return total;
        }
    }
}
