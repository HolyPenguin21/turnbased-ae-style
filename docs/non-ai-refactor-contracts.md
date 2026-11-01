# Non-AI refactoring contracts

Package 5 base: b1e80d44f74d3e8bf8b68524811e437cef3cc374. Existing Docs/NonAI_Package1_Contract.md through Package4 remain authoritative.

## 5.1 Lifecycle

GameSetupModel owns the draft. PlayerSetupData.CopySetupDraft() copies configuration only. GameSession.TryPrepareMatch(List<PlayerSetupData>, GameConfig, out string) validates the copy and commits canonical runtime Players and MatchLaunchRequest only on success. IDs are new per launch. Failed preparation preserves live session inputs.

GameSession.Stage: Inactive, Prepared, InitializingWorld, BindingPresentation, Running, Exiting. InitializeWorld(int sceneHandle) owns inventory reset before generator work, idempotently within one request/scene; a new Prepared request rebuilds even the same scene. Direct reload discards the old running roster. InitializeNewMatchHands(StartingDeckCatalog,int,int) creates the human model once; restore never calls it. UI only binds it. MatchSceneAssembly.ConfigureWorld(GameConfig,HexMap) owns vision/observation/stealth/AI-memory provider assembly. Camera InitializeForMatch() is idempotent. No one-frame barrier owns correctness.

Generation and IsCurrent(long) invalidate callbacks before cancellation. MatchDecision has IsPending/TryComplete/Cancel: a real pending decision survives hidden presentation, completes once, and cannot complete after exit.

## 5.2 Strategic turn effects

StrategicTurnEffects.PrepareRound(HexMap,GameConfig,List<PlayerSetupData>) performs physical income, Produce and initiative reset in original order. ApplyTurnOrder(order,players) records initiative history and applies rank/prison/ability AP. BeginPlayer(player) owns movement/AA replenishment. EndPlayer(player,selection) returns aviation notices after end effects/disband/regeneration. Eliminate(player,selection) returns whether newly eliminated, after world mutations; controller publishes outcomes. Resume never invokes these effects. Eliminated slots are skipped without ending the outgoing player twice. Private reflection consumers now call the actual owners, with no unused wrappers.
