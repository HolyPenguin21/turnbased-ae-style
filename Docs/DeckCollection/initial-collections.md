# Default collections and balanced starters

The authored source is `Assets/Cards/StartingDeckCatalog.asset`. Each faction's human
starter and AI preset field the same 29 main cards: all eight faction heroes, 18
units, and one each of Laboratory, Factory and Intel Center. There are 15 distinct
unit cards; the foot scout, basic infantry and main tank each have two copies.
The unit roster includes two aircraft, ground AA, transport and two artillery
cards. Scrapper, Tech Scrapper and Salvage Drone are absent from these loadouts.

| Faction | Main tank (two copies) | Main points | Human blueprints | Human total |
|---|---|---:|---|---:|
| Iron Concord | Light Tank | 81 | Heavy MG, AT Launcher | 83/100 |
| The Ashen | Rust Tank | 65 | Claws, Dermal Plating | 67/100 |
| The Vessels | Resonance Tank | 67 | Claws, Dermal Plating | 69/100 |

The existing human blueprints remain authored in each starter's `cards` list;
`CollectionService.Starter` separates them into saved equipment and mutator lists.
The AI's existing equipment and mutator blueprint lists remain unchanged, unlock
unlimited production and never enter its 29-card draw pool. AI presets do not use
the human `SavedDeck` 100-point validation for their full blueprint catalog.

## Initial ownership

`CollectionService` grants at least the copies needed by every starter, plus one
copy of every permitted building: the faction base, Laboratory, Factory, Intel
Center, Tactical Center and AA Defence. Building ownership is independent of the
three building cards selected for the starter. System citadels remain excluded;
extractors are built through hex actions, not drawn from a deck.

## Existing profiles

The first load of a profile without `starterDeckRevision = 1` replaces protected
starters with the new authored compositions. Their IDs, names and valid deck
selections are retained. Other saved decks, rewards and owned copies are retained;
missing initial copies and buildings are granted using the existing maximum-count
rule. The revision and decks are saved in the same atomic transaction. A failed
save leaves the in-memory profile unchanged and retries on a later load.

After the revision is saved, later user edits to a starter survive subsequent
loads. Missing starters are still restored separately; unmarked legacy defaults
are promoted only when their name and main composition match the authored source.
Changing the authored balance again requires a new starter revision.

The absent base card produces the existing expansion warning in `DeckRules`,
which does not invalidate the starter. Actual competitive balance still requires
match testing; close aggregate `AiPower` scores are an authoring check.
