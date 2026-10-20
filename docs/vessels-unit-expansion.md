# The Vessels unit expansion

32 visually reviewed units added to the existing faction catalog. New cards have no abilities; existing cards retain their stats and abilities. Stable keys use `vessels.unit.*`; existing keys and indices remain unchanged.

64 prepared textures moved to `Assets/Textures/Units/4_TheVessels/GameCards` and `DetailView` together with their existing `.meta` files, without changing bytes or GUIDs. After checking all 32 pairs and confirming the source GUIDs are not referenced by card catalogs or scripts, the 32 original inputs and their `.meta` files were deleted at the project owner's request; no `SourceArt` folder is retained.

Costs use the existing Vessels energy/materials/tech convention (human cost 0). The comparison column identifies an existing unit of a similar combat tier; skill effects are not copied. All new units cost 1 AP to activate. Ground deployment requires Barracks; aviation uses the existing airfield deployment rule.

| Unit | Visual role | Comparison card | Deck points | AP | E/M/T | Move | Attack | Defense | Range | HP | Initiative | Launch/fuel turns |
|---|---|---|---:|---:|---|---:|---:|---:|---:|---:|---:|---|
| Anchor Warden | Armored infantry with an anchor weapon | `concord.card.trench-guardian` | 3 | 4 | 2/2/1 | 2 | 3 | 4 | 1 | 5 | 1 | — |
| Bell Breacher | Heavy infantry with a short bell cannon | `concord.card.heavy-infantry` | 3 | 4 | 2/2/0 | 2 | 4 | 3 | 1 | 5 | 1 | — |
| Bell Tortoise | Low tracked armored gun carrier | `concord.card.light-tank` | 4 | 4 | 1/2/1 | 2 | 5 | 3 | 2 | 6 | 1 | — |
| Casket Ram | Tracked siege ram with a frontal blade | `ashen.card.rust-breacher` | 4 | 4 | 2/2/1 | 3 | 5 | 3 | 1 | 6 | 1 | — |
| Censer Battery | Tracked battery with two vertical launchers | `concord.card.artillery-tank` | 6 | 8 | 5/2/4 | 2 | 7 | 2 | 4 | 5 | 2 | — |
| Censer Keeper | Infantry carrying a censer and short gun | `concord.card.flamer` | 3 | 3 | 2/1/1 | 2 | 4 | 2 | 1 | 5 | 2 | — |
| Chime Grenadier | Infantry with a large grenade launcher | `concord.card.siege-grenadier` | 4 | 4 | 2/2/2 | 2 | 4 | 3 | 2 | 4 | 1 | — |
| Chime Suppressor | Infantry carrying a heavy multi-barrel gun | `ashen.card.rattle-gunner` | 3 | 3 | 1/1/1 | 2 | 4 | 2 | 2 | 5 | 1 | — |
| Choir Roller | Three-wheel vehicle with a bank of launch tubes | `concord.card.sand-organ` | 4 | 4 | 3/1/2 | 3 | 4 | 1 | 3 | 4 | 2 | — |
| Chord Tug | Flying craft with four engines and a light gun | `concord.card.sky-tug` | 4 | 4 | 2/1/2 | 5 | 4 | 2 | 2 | 5 | 1 | 2/2 |
| Chordbreaker | Tracked platform with twin long rail barrels | `concord.card.needle-rail-tank` | 5 | 6 | 2/2/3 | 2 | 6 | 2 | 3 | 5 | 2 | — |
| Core Tender | Light bipedal mechanical frame with a pistol | `concord.card.watcher-drone` | 2 | 2 | 1/1/0 | 3 | 3 | 2 | 2 | 4 | 1 | — |
| Counterweight Lobber | Tracked catapult artillery | `ashen.card.slag-lobber` | 4 | 5 | 2/2/2 | 2 | 6 | 1 | 3 | 4 | 2 | — |
| Duplex Crawler | Low tracked light combat vehicle | `concord.card.crawler` | 3 | 3 | 1/2/0 | 3 | 4 | 2 | 2 | 5 | 1 | — |
| Halo Interceptor | Wheeled vehicle with two raised gun arms | `concord.card.roadwarden-aa` | 4 | 5 | 2/2/2 | 3 | 5 | 3 | 2 | 6 | 1 | — |
| Halo Kite | Light ring-wing aircraft | `concord.card.mosquito` | 2 | 2 | 1/0/1 | 6 | 3 | 1 | 2 | 3 | 2 | 1/2 |
| Lensbreaker | Heavy tracked vehicle with a long recessed gun | `concord.card.heavy-tank` | 6 | 6 | 2/4/2 | 2 | 6 | 4 | 2 | 7 | 1 | — |
| Mantle Skirmisher | Light infantry with a pistol and a sloped shield | `concord.card.light-infantry` | 2 | 2 | 1/1/0 | 3 | 3 | 2 | 2 | 4 | 2 | — |
| Mirror Duelist | Mechanical infantry with a blade and fan shield | `concord.card.medium-infantry` | 3 | 3 | 2/1/1 | 3 | 4 | 3 | 1 | 4 | 2 | — |
| Mnemonic Carrier | Wheeled armored cargo carrier with a light gun | `concord.card.barracuda` | 3 | 3 | 1/2/1 | 4 | 3 | 2 | 2 | 5 | 1 | — |
| Mnemonic Loom | Large walking frame with a broad panel of emitters | `concord.card.arsenal` | 6 | 7 | 3/4/3 | 2 | 7 | 3 | 3 | 8 | 1 | — |
| Moth Bomber | Wide-wing aircraft carrying several bombs | `concord.card.storm-petrel` | 6 | 5 | 3/2/3 | 8 | 5 | 3 | 2 | 6 | 2 | 2/2 |
| Palisade Frame | Heavy walker behind two tall shield plates | `ashen.card.scrap-mountain` | 6 | 7 | 3/4/3 | 2 | 4 | 5 | 2 | 10 | 1 | — |
| Pendulum Frame | Heavy humanoid frame with a large arm cannon | `concord.card.atlas` | 4 | 5 | 2/2/1 | 2 | 5 | 4 | 2 | 6 | 1 | — |
| Pilgrim Strider | Light long-legged walker with a rifle | `concord.card.grapeshot` | 3 | 3 | 1/2/0 | 4 | 4 | 1 | 2 | 4 | 2 | — |
| Prism Lancer | Light walker with a long energy lance | `concord.card.coil-marksman` | 3 | 3 | 2/1/1 | 3 | 4 | 2 | 3 | 3 | 2 | — |
| Reliquary Crab | Heavy multi-legged armored claw machine | `concord.card.breakwater-crab` | 5 | 5 | 1/3/2 | 2 | 5 | 4 | 1 | 7 | 1 | — |
| Shear Vessel | Fast bipedal machine with two cutting blades | `ashen.card.rivet-maw` | 3 | 3 | 1/2/0 | 3 | 4 | 2 | 1 | 5 | 2 | — |
| Silt Pilgrim | Light robed infantry with a staff | `vessels.15.vessel-rifleman` | 1 | 1 | 1/0/0 | 3 | 3 | 1 | 1 | 4 | 2 | — |
| Spindle Gunner | Infantry with a long automatic gun | `concord.card.medium-infantry` | 3 | 2 | 1/1/0 | 3 | 3 | 2 | 2 | 4 | 1 | — |
| Tuning Blade | Armored infantry carrying a long blade | `ashen.card.rad-leaper` | 2 | 2 | 1/1/0 | 3 | 4 | 2 | 1 | 4 | 2 | — |
| Tuning Fork | Small walker with tall twin emitters | `concord.card.pulse-cannon` | 5 | 6 | 3/1/4 | 2 | 6 | 2 | 3 | 4 | 2 | — |

## AI decks

The existing Vessels deck keeps its one-of-each-unit composition and gains one of every new unit. Its eight heroes, three Whisper Scouts, facilities and 48 equipment / 20 mutator blueprints remain intact. Concord and Ashen deck lists already use the current catalogs and retain their existing composition, heroes, three basic scouts, laboratory/factory and complete blueprint lists.

## Anti-air

All Hero and Unit definitions in all four catalogs (including Neutral) now have `antiAirRadius: 0`. Base/Facility/Equipment/Tactic fields and ability lists are untouched. Runtime spawning clamps this value to 1; AA still requires the AA ability, and explicit AAn abilities retain their own radius.

## Verification

Passed both project YAML validators for all five changed catalog assets and `git diff --cached --check`. Verified 64 byte-identical prepared PNG moves and 64 byte-identical .meta moves (128 exact Git renames); the 32 original source PNGs and their .meta files are intentionally deleted, all 32 new art/detail pairs and sprite importers, unique IDs/keys, empty new ability lists, and all 322 positive AI deck/blueprint entries. Compared all pre-existing definitions with base revision `90a2dd5`: only AA fields changed (101 nonzero values reset across 156 existing Hero/Unit definitions). New cards also have AA radius zero. AI draw pools contain 32 Concord, 40 Ashen and 68 Vessels cards; each retains eight heroes, three basic scouts, 48 equipment and 20 mutator blueprints. Unity is not installed here; import in Unity 6000.5.4f1, full EditMode tests and an in-game Vessels match were not run. No C# or AI evaluation/resource-reservation code changed.
