# Non-AI refactoring contracts

Package 5 base: b1e80d44f74d3e8bf8b68524811e437cef3cc374. Existing Docs/NonAI_Package1_Contract.md through Package4 remain authoritative.

## 5.1 Lifecycle

GameSetupModel owns the draft. PlayerSetupData.CopySetupDraft() copies configuration only. GameSession.TryPrepareMatch(List<PlayerSetupData>, GameConfig, out string) validates the copy and commits canonical runtime Players and MatchLaunchRequest only on success. IDs are new per launch. Failed preparation preserves live session inputs.

GameSession.Stage: Inactive, Prepared, InitializingWorld, BindingPresentation, Running, Exiting. InitializeWorld(int sceneHandle) owns inventory reset before generator work, idempotently within one request/scene; a new Prepared request rebuilds even the same scene. Direct reload discards the old running roster. InitializeNewMatchHands(StartingDeckCatalog,int,int) creates the human model once; restore never calls it. UI only binds it. MatchSceneAssembly.ConfigureWorld(GameConfig,HexMap) owns vision/observation/stealth/AI-memory provider assembly. Camera InitializeForMatch() is idempotent. No one-frame barrier owns correctness.

Generation and IsCurrent(long) invalidate callbacks before cancellation. MatchDecision has IsPending/TryComplete/Cancel: a real pending decision survives hidden presentation, completes once, and cannot complete after exit.

## 5.2 Strategic turn effects

StrategicTurnEffects.PrepareRound(HexMap,GameConfig,List<PlayerSetupData>) performs physical income, Produce and initiative reset in original order. ApplyTurnOrder(order,players) records initiative history and applies rank/prison/ability AP. BeginPlayer(player) owns movement/AA replenishment. EndPlayer(player,selection) returns aviation notices after end effects/disband/regeneration. Eliminate(player,selection) returns whether newly eliminated, after world mutations; controller publishes outcomes. Resume never invokes these effects. Eliminated slots are skipped without ending the outgoing player twice. Private reflection consumers now call the actual owners, with no unused wrappers.

## 5.3 Save application and operation reads

IMatchSaveApplication.ListSlots(): IReadOnlyList<MatchSaveSlot>, Exists(int), Save(int), Delete(int), Load(int). Immutable DTO carries occupancy, header summary/date, backup notice and read error. MatchSaveApplication owns disk/capture/decode/preflight/navigation. Listing never quarantines. Load fully validates detached input before stopping the outgoing controller, then commits the validated request and navigates; accepted reconstruction errors use common cleanup/MainMenu notice. UI owns pagination, selection and confirmations only. AtomicJsonFile backup/future/content mismatch behavior and schema 1 are unchanged.

GameTurnController.ReadSaveConditions(): MatchSaveConditions reads movement, encounter, production quota, hand/army drag and actual decision lifetimes. Popup visibility/read-only inspection/overlay do not prove settlement. MatchDecision is one-shot and epoch-bound; explicit Hide cancels. BlueprintQuota.HasPendingOperations and ProductionAttempt generation guard remain model-owned.

## 5.4 Restore, turn status and exit

GameTurnController.Phase: StrategicTurnPhase exposes round preparation/order, player opening/confirmation/Ready, external/neutral turn, ending/finished/exit. Capture requires Ready and owner facts. Repeated order/external completion callbacks are guarded by receipt and generation. Resume imports state without any new-turn effects.

MatchStateInventory.EndMatch invalidates generation, cancels outgoing operations while registries exist, then resets world and clears facade. CancelWorldOperations closes movement before registry reset. Scene/UI owners explicitly cancel production, aviation, battle and card operations. Existing late callbacks cannot complete into a new generation.

MatchWorldGraph.RestoreWorld imports entities/relations; BindPresentation(HexMap) runs once after knowledge under MatchRestoreScope. File restore source marks consumed on entry to RestoreWorld, including failed reconstruction. The coordinator reserves IDs, reconstructs world/knowledge, binds presentation then Resume; no turn effects or hidden information observation. Shared FailReconstruction cleans up and records a MainMenu notice. Encounter weak bindings are reset explicitly; destruction sprite frames are classified content-only cache. Serialized DTOs, schema version 1 and asset references remain unchanged.
