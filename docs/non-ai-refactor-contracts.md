# Non-AI refactoring contracts

Package 5 base: b1e80d44f74d3e8bf8b68524811e437cef3cc374. Existing Docs/NonAI_Package1_Contract.md through Package4 remain authoritative.

## 5.1 Lifecycle

GameSetupModel owns the draft. PlayerSetupData.CopySetupDraft() copies configuration only. GameSession.TryPrepareMatch(List<PlayerSetupData>, GameConfig, out string) validates the copy and commits canonical runtime Players and MatchLaunchRequest only on success. IDs are new per launch. Failed preparation preserves live session inputs.

GameSession.Stage: Inactive, Prepared, InitializingWorld, BindingPresentation, Running, Exiting. InitializeWorld(int sceneHandle) owns inventory reset before generator work, idempotently within one request/scene; a new Prepared request rebuilds even the same scene. Direct reload discards the old running roster. InitializeNewMatchHands(StartingDeckCatalog,int,int) creates the human model once; restore never calls it. UI only binds it. MatchSceneAssembly.ConfigureWorld(GameConfig,HexMap) owns vision/observation/stealth/AI-memory provider assembly. Camera InitializeForMatch() is idempotent. No one-frame barrier owns correctness.

Generation and IsCurrent(long) invalidate callbacks before cancellation. MatchDecision has IsPending/TryComplete/Cancel: a real pending decision survives hidden presentation, completes once, and cannot complete after exit.
