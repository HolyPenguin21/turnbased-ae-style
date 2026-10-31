# Пакет 4: проверка и границы результата

База: `5f752169`, ветка `refactor/non-ai-package-4`. Функциональные изменения — только движение/observation и необходимые integration gates. Существующие GUID и save DTO/frozen-tree schema сохранены.

Повторный аудит: **783/783** explicit managed checks; broad differential **1019 cases / 783 passed / 236 прежних failed**, с одной явно мигрированной neutral-query проверкой. Подробности исправлений, evidence и native limits: [NonAI_Package4_BottomUp_Audit.md](NonAI_Package4_BottomUp_Audit.md).

## Выполнено

- ArmyMovementOperation: copied route, first legal activation, per-step MP/index, surviving formation revalidation, one terminal result. ArmyController не содержит CanEnter, списания MP либо оплаты activation.
- Selection не вызывает ObserveArmy, ObserveBuilding, ReconcileVisibleHex или EndTurn; memory принадлежит MapObservationSystem/turn owner.
- Observation payload копируется при разрешённом наблюдении, включая departure boundary. Hidden destination defense/roster/appearance не читаются при historical projection. Endpoint сохраняет прежнюю семантику last-seen и добавляет только everSeen.
- Display projections не расходуют live IDs, не регистрируются, не привязывают transport и не заполняют activation coverage. Полезные MP самолёта с emergency penalty заморожены без повторного деления.
- Обычный save API и повторный order учитывают domain busy, в том числе Pending без moving marker. Headless ForOrder имеет AA resolver по умолчанию.
- Выход resident из stealth обрабатывается прежним TryBeginBattleAt/encounter entrypoint; отключение input-компонента не отписывает очередь. В движении (включая headless) resident resolution не обходит step effects.

## Проверка снизу вверх

Прослежены registry relocation/removal/reset, transport/activation, AA/presenter cancellation, stealth/detection, clean events/takeover, encounter entry, current/visited/everSeen, per-viewer snapshots, frozen projection/coverage и save busy. Независимое ревью выявило cancellation pose и недостаточный save gate, а также необходимость default headless AA. Исправления дополнены отдельными регрессиями; дополнительные edge cases устранены при проверке автора. Последующий независимый аудит observation и movement, найденные пропуски и итог повторных прогонов описаны в [NonAI_Package4_BottomUp_Audit.md](NonAI_Package4_BottomUp_Audit.md).

Сохранены совместимые MoveAlong/SettleThen и StepResolutionOutcome; rules и effects не дублируются в animation. Существующие reflection-based tests на перенесённые helpers обновлены на их новых владельцев. Старое ожидание `HumanMemoryRestoreUsesObservedSnapshotAndReservesDestroyedSourceIdentity` заменено тестом `HumanMemoryRestoreUsesObservedSnapshotWithoutAdvancingLiveIdentity` в соответствии с явным требованием пакета; high-water marks остаются у MatchIdentityState.

## Первичный прогон: среда и результаты

Unity Editor 6000.5.4f1 не установлен. Runtime source компилировался Roslyn 4.11 в отдельном managed harness против legacy Unity 2021.3 reference DLL, Mono и внешних compatibility stubs. Эти adapters не добавлены в Assets. Такой результат **не доказывает сборку и запуск в Unity проекта**.

База: 891 cases / 551 passed / 340 failed (337 reference/content errors, 2 invalid parameter sources, 1 прежний inventory assertion). Первичный широкий прогон: **1011 cases / 775 passed / 236 failed** (235 reference/native/content errors и тот же inventory assertion). Ни один проходивший тест базы не упал после изменений; старый identity-reservation test сопоставлен его явно переименованному новому контракту. Единственное новое имя среди error cases — добавленный native frozen-departure тест, который нельзя выполнить в reference среде. Сравнение выполняется по именам проходивших тестов; большее число cases/passed у текущей версии нельзя трактовать как исправление среды — native/content parameter sources и порядок инициализации влияют на discovery.

Отдельный явный список passing cases: `verification/final-passing.xml`, 775/775 passed. Тесты с намеренно падающим notification subscriber логируют ожидаемое исключение, при этом assertions и возврат процесса успешны. Mono печатает предупреждения об отсутствующих Unity internal calls; они не скрываются под заявлением о native success.

## Обязательные fixtures в широком managed прогоне

| Fixture | Passed | Ошибки среды / native |
| --- | ---: | ---: |
| `HexPathfinderAStarTests` | 0 | 4 |
| `HexPathfinderTurnsTests` | 0 | 3 |
| `HumanVisualMemoryTests` | 9 | 0 |
| `RememberedMapVisualStateTests` | 0 | 5 |
| `StealthBuildingContactTests` | 26 | 0 |
| `VisibleHexMutationNotificationTests` | 0 | 3 |
| `MatchPresentationCodecTests` | 0 | 5 |

Нативная coroutine fixture дополнена cancel до/после шага, сохранением legal pose без следующего приказа, отсутствием stale animation, default headless AA и save/order gates. Она компилируется в отдельном compile-only режиме с временными TestTools declarations; native методы не выполняются. Существующие resident reveal, AA/Explosion disposal, ordinary ground activation и смерть в пути также остаются для настоящего runner.

## Осталось в Unity

Полный EditMode suite, затем Extended (включая ArmyMovementLifecyclePlayModeTests); визуально проверить отмену посреди анимации, AA/stealth/contact, два viewer, hidden departure/frozen appearance, save/restore. Команда для проекта:

```text
Unity.exe -batchmode -projectPath <checkout> -runTests -testPlatform EditMode -testResults <out.xml> -logFile <out.log>
```

## Прежние замечания базы

`MatchStateInventoryTests.EveryStatefulStaticTypeIsClassified` уже на базе сообщает о `Game.Combat.BattleEncounterCoordinator.CurrentEncounters` и `Game.Map.ArmyDestructionEffect.FrameCache`. Новая wiring MapObservationSystem классифицирована; два старых пропуска не исправлялись вне пакета. Это реальный прежний тестовый failure, не ошибка native среды.

## Непройденные cases первичного широкого прогона

Ниже приведён точный список имён, чтобы ошибки среды не выдавались за успешную проверку. Новая `HiddenDepartureProjectsFrozenAppearanceWithoutReadingChangedLiveMarker` требует native SpriteRenderer и тоже остаётся неподтверждённой в Unity.

- `Game.EditorTests.ArmyDestructionEffectTests.AccentKeepsFactionColorOnFirstFrameThenTurnsToAsh` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ArmyDestructionEffectTests.BuiltInGroundAndAirMarkersUseActualBakedFrames("MapObject_Army_Base","GroundBase")` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ArmyDestructionEffectTests.BuiltInGroundAndAirMarkersUseActualBakedFrames("MapObject_Army_Air_Base","AirBase")` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ArmyDestructionEffectTests.BuiltInGroundAndAirMarkersUseActualBakedFrames("MapObject_Army_FactionAccent","GroundAccent")` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ArmyDestructionEffectTests.BuiltInGroundAndAirMarkersUseActualBakedFrames("MapObject_Army_Air_FactionAccent","AirAccent")` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ArmyDestructionEffectTests.CapturedArtStillPlaysAfterLiveArmyMarkerIsDestroyed` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ArmyDestructionEffectTests.HiddenArmyCannotLeaveAnInformationLeakingDestructionEffect` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ArmyDestructionEffectTests.ViewerChangeBetweenBattleAndOutcomeCannotRevealOldMarker` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.AircraftReturnPreservesBothSlotsWithoutReattachingOrPayment` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.ConsumingEquipmentDoesNotSpendOrRefundBankResourcesOrReservedFunds` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.DeployCarriesBothSlotsIntoArmyOrGarrison(False,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.DeployCarriesBothSlotsIntoArmyOrGarrison(True,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.DeployCarriesBothSlotsIntoArmyOrGarrison(False,True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.DeployCarriesBothSlotsIntoArmyOrGarrison(True,True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.DevelopmentPortfolioProtectsAnotherOwnersHoldAcrossRecipientAlternatives` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.DevelopmentRecomputesMarginalValueAfterAnAttachmentInTheOtherSlot` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.DevelopmentRetainsRecipientAlternativesAndRefreshesBothSlots` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.HandAndLiveTransactionsAllowBothSlotsAndRejectOnlyTheOccupiedOne` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.HandCardRebindDuringAttachmentPreviewKeepsNewHostNameAndArt(Equipment)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.HandCardRebindDuringAttachmentPreviewKeepsNewHostNameAndArt(Mutator)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.LiveMutatorPublishesVisibilityAndContentThroughExistingRefreshPath` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.RepairAfterMaximumClampClearsOnlyHitPointConsumption` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.SameTransactionChargesInstanceCostsOnce(Equipment,False,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.SameTransactionChargesInstanceCostsOnce(Equipment,True,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.SameTransactionChargesInstanceCostsOnce(Mutator,False,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.SameTransactionChargesInstanceCostsOnce(Mutator,True,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.SameTransactionChargesInstanceCostsOnce(Equipment,False,True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.SameTransactionChargesInstanceCostsOnce(Equipment,True,True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.SameTransactionChargesInstanceCostsOnce(Mutator,False,True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.SameTransactionChargesInstanceCostsOnce(Mutator,True,True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.StandaloneAttachmentDoesNotPayForARecipientRemovedAfterPlanning(Equipment)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.StandaloneAttachmentDoesNotPayForARecipientRemovedAfterPlanning(Mutator)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.StandaloneAttachmentRechecksOtherOwnersHoldBeforePayment(Equipment,Tech,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.StandaloneAttachmentRechecksOtherOwnersHoldBeforePayment(Mutator,Tech,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.StandaloneAttachmentRechecksOtherOwnersHoldBeforePayment(Equipment,ActionPoints,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.StandaloneAttachmentRechecksOtherOwnersHoldBeforePayment(Mutator,ActionPoints,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.StandaloneAttachmentRechecksOtherOwnersHoldBeforePayment(Equipment,Tech,True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.AttachmentLifecycleTests.StandaloneAttachmentRechecksOtherOwnersHoldBeforePayment(Mutator,Tech,True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.BattleTacticalParticipationTests.BlockedShooter_UsesSideStepToReachFiringPosition` — System.TypeInitializationException : The type initializer for 'UnityEngine.Vector2Int' threw an exception.
- `Game.EditorTests.BattleTacticalParticipationTests.LiveAndProjectedMovement_ChooseTheSameCell(1)` — System.Reflection.TargetInvocationException : Exception has been thrown by the target of an invocation.
- `Game.EditorTests.BattleTacticalParticipationTests.LiveAndProjectedMovement_ChooseTheSameCell(2)` — System.Reflection.TargetInvocationException : Exception has been thrown by the target of an invocation.
- `Game.EditorTests.BattleTacticalParticipationTests.LiveAndProjectedMovement_ChooseTheSameCell(3)` — System.Reflection.TargetInvocationException : Exception has been thrown by the target of an invocation.
- `Game.EditorTests.BattleTacticalParticipationTests.PureRangeThreeArtilleryCanDeployIntoOpeningRange` — System.TypeInitializationException : The type initializer for 'UnityEngine.Vector2Int' threw an exception.
- `Game.EditorTests.BattleTacticalParticipationTests.TacticalProjections_DoNotMutateLiveStats` — System.TypeInitializationException : The type initializer for 'UnityEngine.Vector2Int' threw an exception.
- `Game.EditorTests.BattleTacticalParticipationTests.TwoEqualRangeTwoShooters_BothJoinTheOpeningLine(0)` — System.TypeInitializationException : The type initializer for 'UnityEngine.Vector2Int' threw an exception.
- `Game.EditorTests.BattleTacticalParticipationTests.TwoEqualRangeTwoShooters_BothJoinTheOpeningLine(1)` — System.TypeInitializationException : The type initializer for 'UnityEngine.Vector2Int' threw an exception.
- `Game.EditorTests.BattleTacticalParticipationTests.TwoEqualRangeTwoShooters_BothJoinTheOpeningLine(2)` — System.TypeInitializationException : The type initializer for 'UnityEngine.Vector2Int' threw an exception.
- `Game.EditorTests.BattleTacticalParticipationTests.TwoEqualRangeTwoShooters_BothJoinTheOpeningLine(3)` — System.TypeInitializationException : The type initializer for 'UnityEngine.Vector2Int' threw an exception.
- `Game.EditorTests.BattleTacticalParticipationTests.TwoEqualRangeTwoShooters_BothJoinTheOpeningLine(4)` — System.TypeInitializationException : The type initializer for 'UnityEngine.Vector2Int' threw an exception.
- `Game.EditorTests.BattleTacticalParticipationTests.TwoEqualRangeTwoShooters_BothJoinTheOpeningLine(5)` — System.TypeInitializationException : The type initializer for 'UnityEngine.Vector2Int' threw an exception.
- `Game.EditorTests.BattleTacticalParticipationTests.TwoEqualRangeTwoShooters_BothJoinTheOpeningLine(6)` — System.TypeInitializationException : The type initializer for 'UnityEngine.Vector2Int' threw an exception.
- `Game.EditorTests.BattleTacticalParticipationTests.TwoEqualRangeTwoShooters_BothJoinTheOpeningLine(7)` — System.TypeInitializationException : The type initializer for 'UnityEngine.Vector2Int' threw an exception.
- `Game.EditorTests.DisruptionTests.TerrainFormationAndOwnDefense_RemainInBreakdown` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexMapLayoutTests.FogOverlayCoversTheSavedDecorativeCellsWithoutUsingCurrentSizeDefaults` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexMapLayoutTests.GeneratedMapRetainsItsVisualLayoutAfterGeneratorIsRemoved` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexMapLayoutTests.LayoutSnapshotsDoNotExposeTheirNestedArraysToCallers` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexMapLayoutTests.RestoreDoesNotFallBackToAnotherBiomeWhenTheSavedPaletteIsMissing` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexMapLayoutTests.RestoreRejectsIncompleteOrDuplicatedPlayableFootprints` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexMapLayoutTests.RestoreRejectsMissingContentBeforeReplacingTheCurrentMap` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexMapLayoutTests.RestoreRetainsComplexPartOrderRotationAndAnimationPhase` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexMapLayoutTests.RestoreUsesCapturedMeshValuesAndDoesNotConsumeRandomNumbers` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexMapLayoutTests.TextureKeysPreserveActualCellTexturesWhenVariantsAreReordered` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexMapLayoutTests.UnityJsonRoundTripPreservesPrimitiveLayoutForRestore` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexPathfinderAStarTests.AStarCostEqualsDijkstraCost_OnRandomTerrainWithWalls` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexPathfinderAStarTests.CachedRoutes_AfterBlockersAppearAndExpire_EqualFreshSearches` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexPathfinderAStarTests.CostTree_MatchesPointToPointRoutes_WithTheDestinationExemption` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexPathfinderAStarTests.MaxStepCost_NeverEntersCostlierTerrain_AndEqualsTheBlockedDijkstra` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexPathfinderTurnsTests.AvoidsWastingAPointBeforeACostTwoHex` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexPathfinderTurnsTests.ImpassableForThisMoverIsRoutedAround` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.HexPathfinderTurnsTests.NeverNeedsMoreTurnsThanTheCheapestRoute` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryCollectionRewardTests.AiEntryPointsDoNotStopMoveSpawnGuardOrConsumeLibrary` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryCollectionRewardTests.AuthoredLibraryHasThreeRealGuardVariantsAndNoFixedRewards` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryCollectionRewardTests.FailedDiskSaveDoesNotPublishReward` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryCollectionRewardTests.FailedHumanExplorationRemovesOnlyLibraryGuardBeforeAiArrival(True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryCollectionRewardTests.FailedHumanExplorationRemovesOnlyLibraryGuardBeforeAiArrival(False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryCollectionRewardTests.ForeignFactionCardIsPermanentlySavedWithoutChangingSavedDeck` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryCollectionRewardTests.FullCardsAndSystemCardsCannotBeChosen` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryCollectionRewardTests.HeroAndBlueprintUseExistingOneCopyLimits` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryCollectionRewardTests.HeroOnlyLibraryVisitorIsTheCaptureKillTarget` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryCollectionRewardTests.LibraryDoesNotConsumeOrRewriteMatchReward` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryCollectionRewardTests.LibraryGrantAddsSameCardToFullHandAndCollectionOnlyOnce` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryCollectionRewardTests.PendingLibraryClaimUsesExploreActionWithoutRespawningGuard` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryCollectionRewardTests.ReplayedLibraryVisitRestoresOriginalHandCardWithoutPermanentDuplicate` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryCollectionRewardTests.SharedNeutralCardIsEligibleAndCannotExceedItsLimit` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryCollectionRewardTests.SurvivingExplorerHeroStillFacesLibraryGuard` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.BackupRecoveryKeepsReceiptAndPermanentCopyTogether` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.CurrentSchemaMissingLedgerRecoversReceiptFromBackup` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.DuplicateReceiptsAndMalformedIdentitiesAreRejected` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.EmptyRewardIdentityCannotGrantOrCreateProfile(null,"3:1")` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.EmptyRewardIdentityCannotGrantOrCreateProfile("","3:1")` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.EmptyRewardIdentityCannotGrantOrCreateProfile(" ","3:1")` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.EmptyRewardIdentityCannotGrantOrCreateProfile("match",null)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.EmptyRewardIdentityCannotGrantOrCreateProfile("match","")` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.EmptyRewardIdentityCannotGrantOrCreateProfile("match"," ")` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.EventReceiptDoesNotConsumeOrRepeatTheMatchReward` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.ExhaustedResultSurvivesReloadAndNewEligibleContent` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.FailedDiskWriteDoesNotPublishReceiptAndRetryGrantsOnce` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.FutureSchemaNeverFallsBackToAnOlderReceiptLedger` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.LegacyProfileMigrationRetainsOwnershipAndStartsAnEmptyLedger` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.MatchAndEventIdentitiesBothScopeTheReceipt` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.OverlongRewardIdentitiesAreRejectedBeforeAnyGrant` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.OversizedProfileWriteLeavesKnownGoodProfileIntact` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.ReceiptLimitRejectsNewGrantWithoutDiscardingOldReplay` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.ReplayingPreEventSaveReturnsExactCardWithoutSecondPermanentCopy` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.ReplayKeepsExactResultAfterLimitAndEligibilityChange` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.LibraryRewardReceiptTests.UnavailableSavedCardFailsWithoutRerollAndCanRetryWhenRestored` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MapPresentationRestoreTests.EventRebuildRemovesConsumedUnskippedAndMissingMarkers` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MapPresentationRestoreTests.EventRebuildRetainsMarkersWithoutLearningAnotherPlayersDiscovery` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MapPresentationRestoreTests.ResourceRebuildUsesRestoredBonusesIncludingPreviouslyEmptyHexes` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchLoadoutStateTests.RestoredLoadoutUsesItsOwnStartingCardsAndBlueprintsAfterProfileDeckChanges` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchLoadoutStateTests.RestoringHumanAndAiHandsDoesNotDealOrConsumeRandomness` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchPresentationCodecTests.FrozenRendererRoundtrip_ReobservationRefreshesOriginalRendererRoles` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchPresentationCodecTests.FrozenRendererRoundtrip_UsesClonePoseTintSortingAndFlagFrame` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchPresentationCodecTests.JsonRoundtrip_RetainsIndependentHistoricalRosterAfterHiddenCapture` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchPresentationCodecTests.Validate_RejectsMissingAndDuplicateAppearanceBeforeKnowledgeMutation` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchPresentationCodecTests.Validate_RejectsNonfinitePoseAndUnissuedHistoricalIdentity` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchResourcePlacementTests.ActualStartGeometryCanLimitAnOrdinarySmallFourPlayerMapToThreeFarPoints` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchResourcePlacementTests.AllSizesAndPlayerCountsKeepCountsSpacingAndCentralTechAcrossSeeds` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchResourcePlacementTests.CrowdedMapReturnsOnlyLegalPointsAndTechNeverAddsExtraHexes` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchResourcePlacementTests.InwardShiftedCitadelsStillGetCompleteNearAndFarLayouts` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchResourcePlacementTests.JointPlannerRetainsCompleteRichLayoutWhenIndependentFarQualityWouldBlockNearBands` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchResourcePlacementTests.NeutralProposalsImproveCoverageSpacingAndStartBalanceAtTheSameCount` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchResourcePlacementTests.PartialPlanRebuildsTypesForActualCountInsteadOfTruncatingQuotas` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchResourcePlacementTests.RegionalBudgetsAndActualQuotasPreserveEveryRareType` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchResourcePlacementTests.RegionalTypedPlansAcrossTerrainCostsAndShiftedStarts` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchResourcePlacementTests.ResourcePassStampsExactRegionalPlanAndPreservesCitadelYields` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchResourcePlacementTests.ResourceWriteRefreshesVisibleKnowledgeWithoutVisionChangeOrHiddenLeak` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchResourcePlacementTests.SameTerrainAndCitadelsProduceManyDifferentLayouts` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchResourcePlacementTests.SingletonTypeAccessibilityImprovesAfterTypedAssignment` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchResourcePlacementTests.UnreachableAndAlreadyBonusedHexesAreExcludedWithoutMutation` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MatchStateInventoryTests.EveryStatefulStaticTypeIsClassified` — classify these in MatchStateInventory:
- `Game.EditorTests.MatchStateInventoryTests.NewGameBootstrapResetsOldWorldBeforeDefaultOrderMapVisuals` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.MutatorContentTests.UnityImportsActualDefinitionsAndResolvesAllPlayableFactions` — System.NotSupportedException : Asset loading requires Unity Editor.
- `Game.EditorTests.ProductionEquipmentContentTests.UnityCatalogResolvesFortySevenCardsWithBothSpriteReferencesForAllFactions` — System.NotSupportedException : Asset loading requires Unity Editor.
- `Game.EditorTests.RememberedMapVisualStateTests.DestroyedHiddenArmyRestoresItsFrozenRosterAndPerHexMarkerIndex` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.RememberedMapVisualStateTests.HiddenBuildingRestoresHistoricalColorAndFlagAfterUnseenChangeOrDestruction(False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.RememberedMapVisualStateTests.HiddenBuildingRestoresHistoricalColorAndFlagAfterUnseenChangeOrDestruction(True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.RememberedMapVisualStateTests.HiddenDepartureProjectsFrozenAppearanceWithoutReadingChangedLiveMarker` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.RememberedMapVisualStateTests.MissingOrLiveAppearanceIsRejectedWithoutInventingKnowledge` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.AlternateSameOwnerRootCannotFundAttempt` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.ExpectedCostCannotFundAnUnaffordableSuccessfulChain` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.FreeLostAttachmentAttemptStillInvalidatesTheDecisionSnapshot` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.GeneratedNonCombatAlsoRequiresTheFullChainBeforePayment` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.GenerationRechecksApReservationsBeforePayment(EconomyBuildCompletion,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.GenerationRechecksApReservationsBeforePayment(StrategicReactionPass,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.GenerationRechecksApReservationsBeforePayment(EconomyBuildCompletion,True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.GenerationRechecksApReservationsBeforePayment(StrategicReactionPass,True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.HumanBlueprintQuotaRejectsDirectBypassAndAllowsUnlimitedRealAttempts` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.HumanWithoutSelectedLoadoutHasNoOffersAndCannotPay(Research)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.HumanWithoutSelectedLoadoutHasNoOffersAndCannotPay(Production)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.PaidAiAttachmentAttemptEntersHistoryOnceOnWinOrLoss(Equipment,0,True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.PaidAiAttachmentAttemptEntersHistoryOnceOnWinOrLoss(Equipment,6,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.PaidAiAttachmentAttemptEntersHistoryOnceOnWinOrLoss(Mutator,0,True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.PaidAiAttachmentAttemptEntersHistoryOnceOnWinOrLoss(Mutator,6,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.RecipientAttachmentIdentityIsAnExactStableAdmissionInput(Equipment)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.RecipientAttachmentIdentityIsAnExactStableAdmissionInput(Mutator)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.RejectedAiAttachmentAttemptDoesNotEnterHistory` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.RemovedCatalogOfferRejectsBeforePayment` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.ResearchAttemptRevalidatesRevealsAndPaysAsOneCommit` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.StaleGeneratedAttachmentRejectsBeforePaymentOrHistory(Equipment,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.StaleGeneratedAttachmentRejectsBeforePaymentOrHistory(Mutator,False)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.StaleGeneratedAttachmentRejectsBeforePaymentOrHistory(Equipment,True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.StaleGeneratedAttachmentRejectsBeforePaymentOrHistory(Mutator,True)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.UnaffordableAttemptDoesNotPartiallyRevealOrPay` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.UnselectedBlueprintCannotBeOfferedOrPaidInEitherFacility(Research)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.ResearchProductionAttemptTransactionTests.UnselectedBlueprintCannotBeOfferedOrPaidInEitherFacility(Production)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.CheapestScoutIsGuaranteedAndEverySlotCanReceiveIt` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.EmptyAndScoutFreeDecksRemainSafeAndDoNotInventCards` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.EqualApPrefersLowerResourceCost` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.EqualCostScoutsAreBothEligible` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.EveryAuthoredAiOpeningRespectsPoolCapacityAndBlueprints(IronConcord,"concord.card.scout")` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.EveryAuthoredAiOpeningRespectsPoolCapacityAndBlueprints(Ashen,"ashen.card.hooded")` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.EveryAuthoredAiOpeningRespectsPoolCapacityAndBlueprints(Vessels,"vessels.10.whisper-scout")` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.HeroesAircraftBuildingsAndImmobileUnitsCannotReplaceTheOpeningGroundScout` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.NonPositiveHandSizeDoesNotConsumeAnyCards(0)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.NonPositiveHandSizeDoesNotConsumeAnyCards(-1)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.RapidReactionUsesTheActualDeployApRule` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.ReserveRapidHeroAddsOneRapidHeroOnlyWhenRequested` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.ScoutSelectionDoesNotIssueCardRuntimeIdentities` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.SelectedHumanLoadoutAndEveryAiFactionUseTheOpeningScoutRule(IronConcord)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.SelectedHumanLoadoutAndEveryAiFactionUseTheOpeningScoutRule(Ashen)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.SelectedHumanLoadoutAndEveryAiFactionUseTheOpeningScoutRule(Vessels)` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.ShortDeckPreservesDuplicateCopies` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.StartingHandTests.VisionOnlyGroundScoutIsEligible` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.AirCostDoesNotReuseGroundRouteCache` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.AnimatedPartsStaySynchronousWithoutChangingMapRevision` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.AnimationClockLoopsSevenFramesAndSupportsPhaseOffset` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.AuthoredFootprintIsTranslatedWithoutRotation` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.CenterBandKeepsEveryCellInsideItAndOffTheEdgeMargin` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.CloseGroundPassRestoresSiegeAndDefenderPin` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.ComplexThatCutsNarrowBridgeIsRejected` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.ComplexTotalIsSplitByTemplateShares` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.CurrentGroundContactAcrossSnakeIsNotSiegeButAirIs` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.DisabledTemplateRemainsStructurallyValidButCannotBePlaced` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.EmptyAndOversizedFootprintsRemainInvalid` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.ExclusiveGroupKeepsExactlyOneAlternativeAtEvenOdds` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.ForbiddenDestinationCannotBeExemptedByThreatBlocker` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.ForwardAndReverseGroundFieldsExcludeObstacles` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.GeneratorPublishesMatchingRotatedTerrainAndMeshUsingOriginalTextures` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.GroundCannotEnterButAirCan` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.GroundPathDetoursButAirPathRemainsFlat` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.GroundRetreatFailsWhenEveryNeighborIsImpassable` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.GroundRouteCacheInvalidatesWhenTerrainChanges` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.HistoricalContactMayAdvanceAndUnreachableOriginDoesNotRemoveRisk` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.ImpassableComplexesNeverTouchEachOther` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.ImpassableTerrainProducesNoResourcesEvenWithBonus` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.InvalidDisconnectedOrDuplicateShapesRejected` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.LakeDoesNotKeepHomeGapOrShoreInformationAlive` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.MeshRotationChangesOnlyUvsAndKeepsGlobalPartAlignment` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.MissingComplexListsResolveToEmptyForBothBiomes` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.MovementFactsSurviveReconHistoryAndChangeThreatRefreshKey` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.OldTerrainIncludingMountainsRemainsPassable` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.OutOfMapPairRejectedAtomically` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.OverlapRejectsWholeComplex` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.PairAcceptedAndValidationDoesNotMutateData` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.ProtectedTerrainIsNotOverwritten` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.PublishedLayoutRejectsDataOnlyTerrainMutation` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.ReservedCellRejectsWholeComplex` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.RotatedFootprintChecksProtectedCellsAndRejectsAtomically` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.RotationChoiceIsUniformAndDoesNotChangeFamilyWeights` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.ScoutEstimatorDoesNotReplaceUnreachableRouteWithStraightDistance` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.SingleHexAcceptedWithoutChangingAssignment` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.SingleHexBubbleAnimationUsesOneMaterialAndLoops` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.SingleHexStillRejectsProtectedCellsAndBrokenConnectivity` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.SixRotationsPreservePartOrderAndReturnToAuthoredFootprint` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.StaleMovementRouteDoesNotSpendMovementOrEnterObstacle` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.TerrainMutationBumpsRevisionIncludingMutatedSharedEntry` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.UnequalAnimationSequencesRejected` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.WallMakesGroundTargetUnreachable` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.TerrainComplexTests.ZeroComplexCountPlacesNoComplexes` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.UnitDetailFormatterTests.DetailNamesBothAttachmentsAndKeepsHeroStatConvention(False)` — System.TypeInitializationException : The type initializer for 'Game.UI.UnitDetailFormatter' threw an exception.
- `Game.EditorTests.UnitDetailFormatterTests.DetailNamesBothAttachmentsAndKeepsHeroStatConvention(True)` — System.TypeInitializationException : The type initializer for 'Game.UI.UnitDetailFormatter' threw an exception.
- `Game.EditorTests.VisibleHexMutationNotificationTests.FacilityPlacementRefreshesVisibleBuildingSlotsWithoutVisionChange` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.VisibleHexMutationNotificationTests.RepairOnVisibleHexRefreshesObservedHpWithoutRevealingOthers` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.VisibleHexMutationNotificationTests.TransferWithinVisibleHexPublishesFinalRosterOnce` — System.NullReferenceException : Object reference not set to an instance of an object
- `Game.EditorTests.WorldStatePreparationTests.EconomyRestorePreservesContributionOrderWithoutBuyingOrPublishingChanges` — System.NullReferenceException : Object reference not set to an instance of an object

Итоговые артефакты внешней проверки: `verified-broad.xml`, `complete-base.xml`, `final-passing.xml`, `final-extended-compile-build.log`, `summary.json` в каталоге verification рабочего сеанса. Полный список имён выше совпадает с окончательным verified-broad.xml.
