# Abandoned command post

Authored identity: `event-abandoned-command-post` in `Assets/Cards/EventCatalog.asset`.

The event participates in the existing random event pool. All three variants are guarded:
`Event Light 01`, `Event Medium 01`, `Event Heavy 01`. The reward is a base, with no additional
resource or card payout. Exploring still ends the army's movement under the ordinary event rules.
The event cannot appear on a player's starting citadel or any of its six neighbouring hexes
(minimum distance 2 from every player's citadel). This applies to plain placement, guaranteed
ruins/neutral-army sites and the final placement boundary. Ordinary events retain their existing
permission to appear next to citadels. Constrained guaranteed sites receive compatible events first.

The surviving ground army's owner receives a level 1 base on the event hex. It uses the owner's
playable Base definition, not the excluded starting Citadel definition. No hero, Base card, free
hand slot, AP or resource payment is required. Human and AI players use the same reward path;
neutral guards cannot claim the reward. Skip, retreat and defeat do not grant a base.

Creation reuses the infrastructure spawn path: faction visual, authored stats and abilities,
two initially unlocked facility slots, garrison, airfield, vision and the BaseFounded notification.
An existing friendly resource site's facilities carry over. An already established friendly base
keeps its damage, upgrades and facilities. The event is consumed only after successful restoration.
Resource-site facilities retain their slot indices, including empty slots between facilities.
If restoration fails after combat, the defeated guard list is cleared, the event stays claimable,
and a human sees a retry message. The existing event snapshot/codec preserves that empty guard list,
so revisiting or restoring a snapshot does not require defeating the same guards again.

## Content

- Illustration: `Assets/Textures/Game/General/Events/AbandonedCommandPost.png` (1254 x 1254 RGBA).
- Description and result text are English. The result popup shows the faction base's detail art.
- Generated with the built-in image tool; `Bunker.png` was a style reference, not an edit target.
- Art direction: reclaimable concrete military command compound in a postapocalyptic desert;
  antennas, radar, two watchtowers, open steel gate, empty courtyard, supply crates and generator;
  detailed fine ink over sepia parchment watercolor, muted sand and brown, atmospheric washed sky;
  complete structure within frame; irregular torn parchment edges and transparent exterior;
  no people, text, logos, faction flags, UI frame or watermark.

## Validation

Passed both Unity YAML verifiers for the catalog and checked the event's unique key, texture GUID,
RGBA transparency, importer settings, all three guards and their unit keys, and each faction's
single playable Base definition. `git diff --check` passed.
C# syntax parsing passed for all changed scripts and the new tests. This does not check types
or replace compilation. The Game scene's catalog reference was also checked.

`CommandPostEventTests` covers human/AI rewards for all three factions, one-time claiming,
full-hand/no-hero behavior, neutral guard victory, live guard rejection, skip/retreat,
existing bases/resource sites, first discoverer versus final claimant, aircraft rejection and
authored content links. These tests require Unity and have not run in this environment.
Neither Unity nor .NET/Mono is installed; no successful compilation or runtime test is claimed.

In Unity, run EditMode tests and play through Explore -> guard victory -> reward. Check the event
illustration, faction marker, base window, deployment/production, airfield and upgrades; then
repeat after skipping, retreating and losing the guard fight, including an AI completion.
