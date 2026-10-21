using System.Collections.Generic;

namespace Game.Combat
{
    // The actual AiЕhoughts_Text content — English, personal, a little dry/funny where it fits,
    // One random line per category per trigger. BattleAiThoughtsUI finishes the visible line
    // and retains only the latest pending event. Tokens reactions precede the final result.
    public static class BattleAiPhraseBank
    {
        // Cosmetic choices must not consume ChallengeResolver's UnityEngine.Random stream.
        private static readonly System.Random PhraseRandom = new System.Random();

        private static readonly Dictionary<AiThoughtCategory, string[]> Phrases = new Dictionary<AiThoughtCategory, string[]>
        {
            [AiThoughtCategory.FightDecision] = new[]
            {
                "We're staying in this fight. Hold the line.",
                "Keep fighting. Stay sharp.",
                "Not our first fight, won't be our last. Advance.",
                "We're committed now. Stay sharp.",
                "Still in this fight. No mistakes now.",
                "We're fighting this out. Engage.",
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
                "A chance to finish this one. Taking it.",
                "Going for the kill.",
                "This hit could end it. Stay focused.",
                "Trying to finish this one off.",
                "There's an opening. Make it count.",
                "A finishing blow is within reach.",
            },
            [AiThoughtCategory.PriorityTarget] = new[]
            {
                "That one gives us an opening. Taking it.",
                "Focusing on this target.",
                "Everyone else can wait - this one comes first.",
                "This looks like our best shot.",
                "Picking the target we can do most with.",
                "Focus fire. Make this attack count.",
            },
            [AiThoughtCategory.LowDamageAttack] = new[]
            {
                "Slim odds, but taking the shot.",
                "A difficult target. Trying anyway.",
                "Not much of an opening. Let's try it.",
                "This will be hard to hurt. Taking a chance.",
                "A long shot is still a shot.",
                "No easy hit here. Trying our luck.",
            },
            [AiThoughtCategory.AdvanceMove] = new[]
            {
                "Changing position.",
                "Getting into a better position.",
                "No use standing around. Moving.",
                "Looking for a better angle.",
                "Time to reposition.",
                "On the move.",
            },
            [AiThoughtCategory.CautiousWait] = new[]
            {
                "Not moving this turn.",
                "Holding position - no reason to walk into that.",
                "No useful move right now.",
                "Staying put for now.",
                "Holding here for now.",
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
                "Worth the risk - rerolling that one.",
                "Spending Fate here. This matters.",
                "One more roll. We need this.",
                "Burning some luck on this - it counts.",
                "This is exactly what Fate is for.",
                "Rerolling. Can't let this one stand.",
            },
            [AiThoughtCategory.FateSpendSkip] = new[]
            {
                "Keeping Fate for another roll.",
                "Accepting these tokens. Saving Fate.",
                "Letting this one go - Fate's better spent elsewhere.",
                "Not every fight needs a reroll.",
                "Holding onto our luck for now.",
                "This isn't the moment to burn Fate.",
            },
            [AiThoughtCategory.DamageTakenMinor] = new[]
            {
                "Took a hit. Still fighting.",
                "We're damaged, but still standing.",
                "Still in this fight after that hit.",
                "That hurt. Keep going.",
                "Hit, but not stopped.",
                "We're still holding after that blow.",
            },
            [AiThoughtCategory.DamageTakenMajor] = new[]
            {
                "...Barely standing now.",
                "We're bleeding badly here.",
                "This is getting dangerous fast.",
                "Not much health left now.",
                "We need this fight to turn around, quickly.",
                "We're close to falling.",
            },
            [AiThoughtCategory.GoodRoll] = new[]
            {
                "That's a promising roll.",
                "The tokens favor us so far.",
                "Good start. Let's see how it holds.",
                "I'll take those tokens.",
                "That roll looks good.",
                "Luck is on our side for now.",
            },
            [AiThoughtCategory.BadRoll] = new[]
            {
                "Of course the tokens did that. Of course.",
                "Not our day for tokens, apparently.",
                "That roll could have been better.",
                "Rough luck there.",
                "The tokens aren't helping yet.",
                "Not the start we wanted.",
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
                "It's over. We won.",
                "Victory. Hard fought, but ours.",
                "They're finished. We're still standing.",
                "That's a win we'll remember.",
                "The field is ours.",
                "Well fought, all of us.",
            },
            [AiThoughtCategory.BattleLost] = new[]
            {
                "We lost this fight.",
                "This one's lost. We'll come back stronger.",
                "Not our day. The battle is lost.",
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
                "Engagement continues.",
                "Standing ground. Continuing the fight.",
                "Committing to the fight.",
            },
            [AiThoughtCategory.RetreatDecision] = new[]
            {
                "The army falls back.",
                "Withdrawing - the numbers don't favor a fight.",
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
                "A finishing blow is possible.",
                "Attacking with a chance to eliminate the target.",
                "Attempting to finish the target.",
                "A lethal hit is within reach.",
            },
            [AiThoughtCategory.PriorityTarget] = new[]
            {
                "Focusing fire on the selected target.",
                "Taking the best available attack.",
                "Engaging the preferred target.",
                "Target selected. Opening fire.",
            },
            [AiThoughtCategory.LowDamageAttack] = new[]
            {
                "Low damage expected. Attempting the attack.",
                "No easy target available. Taking a chance.",
                "A difficult target is engaged.",
                "Attempting a low-probability hit.",
            },
            [AiThoughtCategory.AdvanceMove] = new[]
            {
                "The unit changes position.",
                "Repositioning on the battlefield.",
                "Moving to a new position.",
                "The unit is on the move.",
            },
            [AiThoughtCategory.CautiousWait] = new[]
            {
                "Holding position.",
                "The unit remains in place.",
                "No move this turn.",
                "Standing by.",
            },
            [AiThoughtCategory.ForcedAdvance] = new[]
            {
                "Advancing regardless of risk.",
                "No more delay. Moving in.",
                "The unit commits to the advance.",
                "Caution abandoned - advancing now.",
            },
            [AiThoughtCategory.DamageTakenMinor] = new[]
            {
                "Damage sustained. Still operational.",
                "The unit survives the hit.",
                "Damaged, but still combat capable.",
                "The target remains in the fight.",
            },
            [AiThoughtCategory.DamageTakenMajor] = new[]
            {
                "Health is critically low.",
                "The unit is critically damaged.",
                "The unit is close to destruction.",
                "The target barely remains standing.",
            },
            [AiThoughtCategory.GoodRoll] = new[]
            {
                "Favorable outcome on that roll.",
                "The initial tokens favor this side.",
                "A strong result there.",
                "That roll went well.",
            },
            [AiThoughtCategory.BadRoll] = new[]
            {
                "The initial tokens favor the opponent.",
                "Unfavorable roll.",
                "That attempt came up short.",
                "The tokens give no advantage yet.",
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
                "Forces defeated.",
                "This engagement is over.",
                "Defeat confirmed.",
            },
        };

        // Named variants are shared by hero-led and hero-less sides, so they use no personal
        // narrator. They describe the same event as the corresponding plain pool.
        private static readonly Dictionary<AiThoughtCategory, string[]> NamedPhrases = new Dictionary<AiThoughtCategory, string[]>
        {
            [AiThoughtCategory.FinishingBlow] = new[]
            {
                "A chance to finish {0}. Taking it.",
                "One more hit could finish {0}.",
                "Going for a lethal hit on {0}.",
                "Trying to end the fight for {0}.",
            },
            [AiThoughtCategory.PriorityTarget] = new[]
            {
                "Focusing on {0}.",
                "{0} offers the best opening. Taking it.",
                "Everyone else can wait - {0} goes first.",
                "{0} is the preferred target.",
            },
            [AiThoughtCategory.LowDamageAttack] = new[]
            {
                "Slim odds against {0}. Trying anyway.",
                "Taking a long shot at {0}.",
                "Low damage expected against {0}.",
                "Trying to hurt {0}, despite the odds.",
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
                "{0} has fallen. Make it count.",
                "{0} is down. Keep fighting.",
                "{0} won't be forgotten.",
                "A costly loss - goodbye, {0}.",
            },
            [AiThoughtCategory.DamageTakenMinor] = new[]
            {
                "{0} dealt a hit. Still holding.",
                "Taking damage from {0}, but still in the fight.",
                "{0}'s hit was survived.",
                "Damaged by {0}. Still standing.",
            },
            [AiThoughtCategory.DamageTakenMajor] = new[]
            {
                "Critical condition after {0}'s hit.",
                "Dangerously low on health after {0}'s attack.",
                "{0} is keeping the pressure on.",
                "Barely standing after {0}'s hit.",
            },
            [AiThoughtCategory.GoodRoll] = new[]
            {
                "Promising tokens against {0}.",
                "A favorable roll against {0} so far.",
                "The tokens offer an advantage against {0}.",
                "A good start against {0}.",
            },
            [AiThoughtCategory.BadRoll] = new[]
            {
                "Unfavorable tokens against {0}.",
                "The tokens favor {0} for now.",
                "A rough start against {0}.",
                "The roll gives {0} an advantage so far.",
            },
            [AiThoughtCategory.FateSpendWorthIt] = new[]
            {
                "Worth the risk against {0} - rerolling.",
                "Spending Fate against {0}.",
                "{0} isn't getting away that easy. Rerolling.",
                "This matters - spending Fate on {0}.",
            },
            [AiThoughtCategory.FateSpendSkip] = new[]
            {
                "Keeping Fate after this roll against {0}.",
                "Accepting these tokens against {0}.",
                "Saving Fate against {0}.",
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

            int index = PhraseRandom.Next(total);
            return index < plainCount ? plain[index] : string.Format(named[index - plainCount], unitName);
        }
    }
}
