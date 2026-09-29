# The Vessels

The Vessels preserve human memory in illuminated glass cores and give those
memories mechanical bodies. Their citadels are relay sanctuaries; their field
forces combine scouts, salvage machines, walking armor, artillery and aircraft.
The brass, pale stone, charcoal and warm glass palette links the roster to the
supplied concept art. The vial and flame emblem appears in the setup screen,
catalog UI and seven animated map flag phases.

## Game identity

- Playable faction ID: `Faction.Vessels = 5`; the setup screen and random
  selection both include it.
- The authored catalog has 29 cards: two structures, eight heroes, sixteen
  ground units and three aircraft. The citadel is placed by game setup.
- Mechanical bodies cost energy, materials and technology, with no human cost
  for deployable units. The relay base supplies the existing AP bonus. Heroes
  provide the existing production, research, assembly, scouting and reaction
  abilities. Armor, anti-air support and ranged artillery carry the late game.
- The 25-card starting pool includes nine heroes, light units, a base, two
  aircraft and three neutral facilities. Cards with zero copies in the starter
  are obtainable through faction-restricted research or production offerings.
- Three aircraft use the game's aviation rules: launch energy, flight duration
  and air army marker. The Dreadnought is the durable late game aircraft.

## Asset layout

| Path | Purpose |
| --- | --- |
| `Assets/Cards/TheVessels/CardCatalog_TheVessels.asset` | Card definitions, map prefab links, logo and army names |
| `Assets/Textures/Units/4_TheVessels/RawArt` | 29 clean 768 × 1120 artworks, without card frames |
| `Assets/Textures/Units/4_TheVessels/GameCards` | Empty destination for future composed game card images |
| `Assets/Textures/Units/4_TheVessels/DetailView` | Empty destination for future detail card images |
| `Assets/Textures/General/Logo_TheVessels.png` | Transparent faction emblem |
| `Assets/Textures/General/Citadel/Vessels_Flag_Logo_01–07.png` | Emblem synchronized to the seven cloth animation phases |
| `Assets/Prefabs/MapObject_Citadel_TV.prefab`, `MapObject_Base_TV.prefab` | Faction map buildings |

The catalog's `art` and `detailArt` currently point to the clean `RawArt`
sprites. No final card compositions or frames are part of this change.

## Unity review

Open the main menu's New Game setup, select The Vessels, and confirm the emblem
appears in the faction selector. Start a game with a Vessels player and an AI
opponent. Confirm the opening citadel and later relay base have the faction
building art and synchronized emblem on their owner-colored flags. Check
capture changes the emblem to the new owner's faction. Draw and deploy ground
and air units, launch and refuel aircraft, and verify the faction-restricted
research and production offers. Run an AI turn with a Vessels player. These
runtime checks require the Unity editor; asset references and counts have been
checked statically.
