namespace Game.UI
{
    public static partial class ArmyRadioChatter
    {
        private static readonly string[][] OperationalEnglish =
        {
            // BattleStarted
            new[]
            {
                "Contact has turned into a fight. Keep this channel clear.",
                "Weapons engaged. Command, stay on frequency.",
                "This is no drill. We are in combat.",
                "Fight is on. Short transmissions from here.",
                "Engagement underway. Waiting time is over.",
                "Battle joined. Eyes off the radio, eyes forward.",
                "We are committed now. Keep the orders brief.",
                "Combat station reporting. Things just got loud.",
                "Enemy engaged. The quiet shift is finished.",
                "We have a fight on our hands. Stand by, command.",
            },
            // BattleWon
            new[]
            {
                "That fight went our way. Checking what comes next.",
                "We came out on top. Keeping the watch up.",
                "Engagement settled in our favor. Command, over.",
                "Victory confirmed. Nobody get comfortable yet.",
                "We won this one. The dust can settle now.",
                "Fight is ours. Waiting on the next order.",
                "Good outcome, command. Keeping our heads on a swivel.",
                "We carried the day. Still watching the approaches.",
                "Our side prevailed. Radio is back on routine.",
                "One fight behind us. That was a win.",
            },
            // Retreat
            new[]
            {
                "We pulled back. Awaiting orders from the new position.",
                "Withdrawal complete. Do not send orders to the old grid.",
                "We gave ground. Still on the channel.",
                "Back from the fight. Command, we need a new plan.",
                "We broke off. Standing by where we landed.",
                "Pulled out of that engagement. Taking stock.",
                "We are out of that fight. Keep the frequency open.",
                "Fallback completed. Nobody mistake this for an advance.",
                "Position changed under pressure. Command, over.",
                "We withdrew. Pride can catch up later.",
            },
            // HeavyLosses
            new[]
            {
                "We lost a lot in that fight. Command needs to know.",
                "Heavy losses confirmed. The roster is shorter now.",
                "That engagement cost us dearly. Reporting losses.",
                "We have gaps in the ranks. Not the usual kind.",
                "Too many names missing after that fight.",
                "Loss report is grim. We are still on frequency.",
                "We paid heavily for that engagement.",
                "Our formation took serious losses. Sending the count.",
                "The fight left us short of people. Command, over.",
                "That was a costly battle. No jokes on this channel.",
            },
            // EnemyBaseDiscovered
            new[]
            {
                "Enemy base in view. Marking the location.",
                "Command, we have eyes on a hostile outpost.",
                "Those are enemy colors over a base. Position noted.",
                "Hostile base spotted. Keeping it under observation.",
                "We can see an enemy stronghold. Your call.",
                "Opposition has a base here. Adding it to the report.",
                "Enemy installation in sight. No guesswork on this one.",
                "Visual on a hostile base. Keeping the channel open.",
                "Found where the enemy plants its flag. Base confirmed.",
                "Enemy outpost visible. That belongs on the map.",
            },
            // BaseCaptured
            new[]
            {
                "Base is under our control. Updating command.",
                "Our colors over this base now. Keeping watch.",
                "Base taken. New address, same dust.",
                "We control the outpost. Checking the approaches.",
                "Command, this base has changed hands. It is ours.",
                "Ownership confirmed. Holding the captured base.",
                "We have the base. Awaiting the next instruction.",
                "The outpost is ours now. Keeping the radio open.",
                "Base captured. Nobody unpack too much.",
                "Our side controls this base. Report complete.",
            },
            // HeroCaptured
            new[]
            {
                "Enemy commander captured. Passing the custody report.",
                "Hostile leader is a prisoner now. Command, over.",
                "We have captured an enemy hero. Custody confirmed.",
                "Enemy command personnel taken alive.",
                "Prisoner report: enemy commander secured.",
                "One hostile leader in our custody. Keep the paperwork dry.",
                "Capture confirmed. That enemy hero is in our hands.",
                "Enemy officer is a prisoner. Reporting to command.",
                "We took their commander alive. Custody logged.",
                "Enemy leader captured. A rare day for the paperwork.",
            },
            // EventDiscovered
            new[]
            {
                "Command, we found something worth a closer look.",
                "Unusual site here. We have reached it.",
                "There is something out here besides dust. Reporting the find.",
                "We came across a site of interest. Your call, command.",
                "Found something on the ground. Awaiting a decision.",
                "This place deserves a look. Sending the location.",
                "We have a find here. No promises about what is inside.",
                "New site encountered. Keeping our hands off for now.",
                "Command, this spot has a story. We are at the site.",
                "Something interesting at our position. Could be trouble.",
            },
            // TerritoryExplored
            new[]
            {
                "New ground reached. Updating the survey.",
                "We have set foot here. Mark it as visited.",
                "Another patch of blank map has bootprints now.",
                "Ground survey checked in from this position.",
                "We reached unvisited territory. Sending the grid.",
                "New ground under our boots. Map update follows.",
                "First ground visit here. Keeping the survey brief.",
                "We have been here now. The map can stop guessing.",
                "Fresh territory visited. Same familiar dust.",
                "Ground covered for the first time. Report sent.",
            },
            // ReinforcementsArrived
            new[]
            {
                "New personnel joined the formation. Checking them in.",
                "Reinforcement received. Another face on the roster.",
                "We have an addition to the unit. Command, over.",
                "Fresh arrival with us now. Finding room in the formation.",
                "Roster just grew. Reinforcement checked in.",
                "New arrival reporting with our formation.",
                "We received a reinforcement. Getting acquainted.",
                "An extra pair of eyes with us now.",
                "Formation reinforced. Updating the headcount.",
                "New member on the roster. Hope they packed water.",
            },
            // BaseFounded
            new[]
            {
                "New base established. Command has a new address.",
                "Outpost founded here. Keeping watch from the new site.",
                "Base construction committed. It is on the map now.",
                "We have a new outpost. Dust came included.",
                "New base is in place. Reporting completion.",
                "Outpost established at this position. Command, over.",
                "Base founded. Another place to collect sand.",
                "The new base is ours and standing.",
                "New foothold established. Updating the network.",
                "Construction complete here. Base reported to command.",
            },
            // AviationSortie
            new[]
            {
                "Flight operation underway. Air wing checking in.",
                "Wing operating away from storage. Command, over.",
                "Air crew on the operation. Keeping this brief.",
                "Sortie in progress. Radio is with you.",
                "Flight operation confirmed. Standing by for orders.",
                "Wing has begun its run. Channel open.",
                "Air unit operating. We will keep the chatter down.",
                "Sortie checked in. Let command know we are working.",
                "Air operation active. Short calls from here.",
                "Wing on duty. The waiting part is over.",
            },
            // AviationReturned
            new[]
            {
                "Aircraft landed here. Flight crew checked in.",
                "Aircraft are back at our station. Landing confirmed.",
                "Landing checked in at this station.",
                "Aircraft received on the ground. Command, over.",
                "Landing completed here. Crew can stop gripping things.",
                "Aircraft are back at the airfield. Report sent.",
                "Flight crew returned here. Ground station confirms.",
                "Aircraft down at our station. Another landing logged.",
                "Return to airfield confirmed. Checking the flight roster.",
                "Landed aircraft received. The ground won that argument.",
            },
            // RepairsCompleted
            new[]
            {
                "The repaired member is back to full condition.",
                "Repair job finished. One less problem on the roster.",
                "Maintenance completed on our member. Command, over.",
                "Repair complete. That particular dent is history.",
                "Work on the damaged member is done.",
                "Repaired member checked back in. Full condition confirmed.",
                "Maintenance signed off. Keeping the toolkit close.",
                "That repair is finished. Updating the condition report.",
                "Repair work completed. The tape gets a rest.",
                "Our repaired member is restored. Report sent.",
            },
            // EquipmentReceived
            new[]
            {
                "New equipment installed on a member. Checking it in.",
                "Gear attachment complete. Somebody finally read the fitting notes.",
                "Equipment received and fitted. Command, over.",
                "New kit on the roster. Installation confirmed.",
                "Gear is installed. Keeping the instruction sheet.",
                "Equipment fitted to our member. Report sent.",
                "New attachment checked in. Hope the bolts stay put.",
                "Kit upgrade installed. Updating the equipment report.",
                "The new gear is on. Now we get to carry it.",
                "Equipment attachment confirmed. One more thing to maintain.",
            },
        };
        private static readonly string[] AmbientEnglish =
        {
            "The canteen tastes like the canteen. Water might have been a rumor.",
            "Whoever designed this collar never wore a pack.",
            "Trying to remember a day when socks were dry.",
            "Radio battery has more discipline than the rest of us.",
            "The ration label has faded. Probably for our protection.",
            "An old world museum would pay for our boots. As evidence.",
            "Our coffee has sediment layers. The bottom one predates the war.",
            "If dust were currency, we could retire this afternoon.",
            "Someone explain why every spare screw belongs to a different machine.",
            "Last night I dreamed of a chair with all four legs.",
            "Waiting for orders. Practicing looking busy while sitting still.",
            "The wind is doing a fine job of relocating my lunch.",
            "I remember when a hot meal meant the food was hot.",
            "Every pocket on this uniform contains sand and disappointment.",
            "The old world had weather forecasts. We have a sore knee.",
            "Kept the instruction manual. Makes a decent cushion.",
            "If the radio hum gets any louder, it can take the next watch.",
            "Somewhere there is a factory still making the wrong size gloves.",
            "You can tell the good water by how little you argue with it.",
            "The pre-war maps show a restaurant here. I admire their optimism.",
            "The quiet shifts are the ones you learn to appreciate.",
            "Our spare parts bag sounds healthier than our equipment.",
            "I miss windows that were part of a wall.",
            "The dust has settled. Mostly inside my collar.",
            "Command, routine channel check. Still remembering how to relax.",
        };
        private static readonly string[][] AdditionalDialogues =
        {
            new[] { "Got any decent water over there?", "Decent is a strong word. We have wet." },
            new[] { "How old are those ration tins?", "Old enough to outrank us." },
            new[] { "You get the updated map?", "Yes. They moved the coffee stain north." },
            new[] { "Who designed these boots?", "Someone who never had to walk away from a design meeting." },
            new[] { "Any spare batteries?", "One. We are saving it for a conversation worth hearing." },
            new[] { "Remember streetlights?", "Sure. Things that told you where the street used to be." },
            new[] { "Your equipment rattling too?", "Only when we stop listening to the radio." },
            new[] { "What is the forecast over there?", "Dust, with a chance of more dust at higher prices." },
            new[] { "Got a trick for sleeping in this kit?", "Take off your expectations first." },
            new[] { "Why does your watch always look so cheerful?", "We take turns pretending." },
            new[] { "Any idea what this old sign says?", "Probably tells us not to be standing here." },
            new[] { "Neighbor, how is morale?", "Still fits in the ration tin. We keep the lid on." },
        };

        public static int OperationalPhraseCount(ArmyRadioCategory category) => OperationalEnglish[(int)category].Length;
        public static string OperationalPhrase(ArmyRadioCategory category, int variation) =>
            OperationalEnglish[(int)category][Index(variation, OperationalPhraseCount(category))];
        private static readonly string[] PilotAmbientEnglish =
        {
            "The cockpit seat was designed by someone who walked to work.",
            "Our fuel gauge has two settings. Worry and more worry.",
            "Ground crews get dust in their boots. We get it in expensive places.",
            "The old flight manual says enjoy the view. Bold advice.",
            "Someone explain why the smoothest ride is always back in the hangar.",
        };
        public static int PilotAmbientCount => PilotAmbientEnglish.Length;
        public static string PilotAmbient(int variation) => PilotAmbientEnglish[Index(variation, PilotAmbientEnglish.Length)];
        public static int AmbientCount => AmbientEnglish.Length;
        public static string Ambient(int variation) => AmbientEnglish[Index(variation, AmbientEnglish.Length)];
    }
}
