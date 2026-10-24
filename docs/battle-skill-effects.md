# Battle skill effects

The approved PNGs are supplied separately and are not part of the code commit. Copy them without changing their names:

| File | Unity path | Format |
| --- | --- | --- |
| BodyguardShield.png | Assets/Textures/UI/Effects/BodyguardShield.png | Transparent shield sprite |
| ShockImpulse.png | Assets/Textures/UI/Effects/ShockImpulse.png | Transparent impulse sprite |
| Scorcher_Flipbook.png | Assets/Textures/UI/Effects/Flipbooks/Scorcher_Flipbook.png | Approved v2: 1536 × 1920, 4 × 5 cells, 19 active frames |
| Splash_Flipbook.png | Assets/Textures/UI/Effects/Flipbooks/Splash_Flipbook.png | Approved v2: 1536 × 1536, 4 × 4 cells, 15 active frames |

Opening the project or importing those files configures their texture import settings and registers the textures, sprites and blend shader in `Assets/Resources/GameAssetCatalog.asset`. Existing image GUIDs are retained. If needed, run **Tools → Register Battle Skill Effects**. Keep the generated catalog changes and image `.meta` files with your local art; this code commit does not invent references to unpublished images.

Scorcher plays for 1.38 seconds, including the extended engulf frames and cooling tail. Splash plays for 1.08 seconds with one source blast; additional neighbours receive its fragments and smoke. Adjacent frames blend over their final 20–40 milliseconds. Secondary damage appears 0.48 seconds after the trails start. Vertical Scorcher uses upright flame crowns instead of rotating a horizontal fire wave. Berserk reuses Drill; Bodyguard appears on the actual interceptor and Shock on the attacking skill carrier.

Incoming damage labels show `-N`, `BLOCK` or `MISS`. Removed units retain their cards until the effects finish. Counterattacks and subsequent Explosion challenges follow presentation; fast resolution and battles without human participants skip the visual wait.

## Verification

Portable comparison against base `e47aca20ca3842641416920cdce340b03cac307f`: 309 → 325 tests, 238 → 254 passed, the same 71 reference-DLL/native-engine failures, zero new failures. All 16 added portable cases passed. Runtime and importer C# syntax were compiled against the fallback reference assemblies with verification-only adapters outside the repository. This does not establish a Unity build.

Unity 6000.5.4f1 was unavailable in the implementation environment. Run the full EditMode suite in that editor, including the three `BattleHitLifecycleTests` that enter Play Mode. Visually check horizontal/vertical fire, one/two Splash neighbours, mixed Splash + Scorcher, lethal targets, Bodyguard interception, Berserk, Shock counter suppression, Explosion sequencing, cancel/reopen, canvas resizing and fast resolution. Confirm the four PNGs and shader are referenced in the generated catalog before building.
