# Поэтапная подготовка производства — реализация 2026-10-08

Изменение поведения AI V2 на базе `26054f75f39facae30b16fa2ddcec5c9e88a04cf`.

## Поведение

`DevelopmentOpportunityEvaluator` остаётся единственным владельцем допуска. Он перечисляет отдельные текущие действия Facility и Operator для собственного Base. У этих кандидатов нет выбранного output или recipient. Каталог подтверждает существование режима; стоимость и польза конкретного будущего продукта не вычисляются. Другой компонент подтверждается реальной картой, размещённым героем, действующей доставкой, оставшейся колодой либо уже работающим источником квалифицированного Hero. Колода подтверждает путь, но не гарантирует draw.

Если отсутствуют оба компонента, оба первых шага перечисляются независимо. Недоступная по цене площадка не блокирует доступного оператора. Для второго шага завершённый компонент становится фактом мира, его цена повторно не включается. Capacity upgrade сохраняет составное действие с placement площадки: в текущую стоимость входят оба платежа.

Оператор может стоять на собственной базе без площадки. `OperatorPreparedAt` завершает доставку конкретного героя в Provisioning/Revalidation/Execution/Continuity. `ResearchProductionSystem.IsEligible` и `GenerationSource` сохраняют строгую проверку работающего производства: площадка, квалифицированный герой и незаблокированное место. Подготовка сама не разрешает Challenge.

Полная цепочка сохраняется: подготовка компонентов → Challenge → физическая карта в руке → deployment/attachment. Эти действия могут завершаться в разные ходы. Выбор полезного output/recipient, mission-context, signed losses, supply, repeat, saturation и авторитетные транзакции выпуска/применения сохраняются. Генерация Unit/Hero/Aviation остаётся в обычных materialization pathways.

## Оценка и экономика

Для совместимости используется существующий `nonCombatFacilityValue = 1.1`, одинаковый для двух подготовительных компонентов. Для реального generated operator полезность текущего Challenge умножается на его вероятность успеха; платежи не дисконтируются. Цена доставки размещённого героя и стоимость отказа от его другой задачи входят в существующий TaskScore.

`WorldTaskScore` сохраняет отдельную валюту задачи. `PreparationRank` применяется только при выборе ближайшего prerequisite: из task merit вычитаются AP текущей карточной стадии через `ActionPrice.ToCardScore` и ресурсы через `StrategicResourceCostValue`, с настоящим spendable delegate. Он не перезаписывает TaskScore и не добавляется к финальной оценке второй раз. При равенстве используются стабильные mode/hex/kind keys. Выбор hand/map/generated источника оператора также учитывает его текущую цену. Отсутствие текущей миссии/получателя не запрещает подготовку.

Факты желания собираются тем же enumerator без сегодняшнего window/affordability veto. Для каждого шага:

`headroom(step) = min(surplus[t], где cost[t] > 0)`; если ресурсного платежа нет, результат 1.

`PreparationHeadroom` — максимум по структурным текущим вариантам. Желание подготовки — существующий `Ramp(headroom, devSurplusRampLo, devSurplusRampHi) * devLatentPotential`. Итог Development использует максимум между ним и прежним желанием output. Поэтому пустой ненужный ресурс и отсутствие военного witness не подавляют подготовку. Общие all-resource поля и force-need других осей не меняют смысл.

Допуск исполнения отдельно проверяет положительную оценку текущего шага, ресурсы его платежа в двухходовом окне 0.60, настоящий свободный банк и текущий AP для карточного действия. Development infrastructure по-прежнему выполняется после карточного арбитража Phase A. Рост желания не отменяет эти причины ожидания.

Калибровка базовой стратегической ценности открытия производства и бонуса завершения подготовки остаётся отложенной. Новых коэффициентов нет. Нынешняя совместимая оценка может отклонять дорогую доставку или дорогой текущий шаг; реализация не обещает автоматического приоритета подготовки.

## Проверка банка снизу вверх

| Слой | Контракт |
|---|---|
| `TurnResourceBook.Free` | Свободный остаток с учётом ledger/lease; смысл не изменён |
| `StrategicSpendability` | Development сохраняет обычную authority, не получает Economy bypass |
| Preparation admission | Только `StageResourceCost`; будущие output/deploy/attach не требуют денег сегодня |
| Facility scorer | Фактические AP/resources upgrade + facility, с spendable delegate |
| Operator scorer | Фактический deployment и свободный банк, exact выбранная карта |
| `InfrastructureFulfillment.TryFulfill` | Повторно проверяет Phase-A room, свободные AP/resources, gameplay affordability до мутации |
| `GenerationSource` / `MaterializationExecutor` | Реальный source и retry identity; создание оператора — самостоятельный Challenge |
| Gameplay / world delta | Фактические confirmed расходы и истинная мутация сохраняются, включая частичный capacity результат |
| Следующий settled pass | Получает новый мир/руку; второй компонент не оплачивает первый повторно |

Новые долгосрочные резервы AP/resources не создаются. Generated operator claim защищает только конкретную карту, место и режим, переживает кратковременную нехватку AP и отсутствие площадки, имеет прежний конечный срок. Потеря базы, deployment, отсутствие карты или квалификации снимают claim при обычной reconciliation. Карта с активным claim не используется для другого места/режима. Действующая доставка живого оператора исключает повторную подготовку той же пары site/mode.

## Кеши и повторный допуск

| Данные | Запись, читатели и инвалидизация |
|---|---|
| Preparation snapshot facts | `WorldAnalysis` после Self/Development/Economy/Threat во всех Scan/Refresh путях; Desire и card-role path читают новые факты каждого settled snapshot |
| Admission fingerprint | Pipeline; exact AP/free resources/window/price остаются, добавлены preparation headroom/modes, effective deck abilities, generated card destination binding, owner/base/contested для площадки без facility |
| Recipient/target memo | Только scoped READY/output pass; старый hypothetical Equipment preparation preview и его cache удалены |
| ForceNeed cache | Прежний per-snapshot `ConditionalWeakTable`; preparation не требует вычисления будущих matchup для своих фактов |
| Generated operator claims | `DevelopmentLifecycleState`; запись после реального mint, finite expiry и structural reconciliation; read-only destination binding входит в fingerprint |

Общий admission fingerprint оставлен консервативным: он учитывает также READY dependencies. Для ключа используются факты/`ForceNeedModel.ChangeKey`, а не новый Monte Carlo расчёт будущего output. Сам preparation enumerator не выполняет подбор будущих Equipment/recipient пар.

## Проверки и ограничения

Unity `6000.5.4f1` в рабочей среде отсутствует. Полная Unity сборка, EditMode и PlayMode здесь **не запускались**.

Выполнено:

- Компиляция всех C# источников исходной ревизии и изменений Roslyn против доступных Unity reference DLL/stubs. В первоначальном baseline были три несовместимости reference/stub UI API; одинаковые verification-only адаптации применены к копиям baseline/current, в игровой код не перенесены. Обе копии компилируются без ошибок.
- Сравнение reflection-runner NUnit cases всех `Game.EditorTests` на исходной и текущей ревизиях: исходно 1111 passed / 615 failed; текущая версия 1121 passed / 623 failed. Из всех 1111 ранее проходивших cases новых регрессий нет. Девять новых cases проходят в доступной среде; ещё девять новых cases требуют Unity и не исполнены успешно. Один существующий raw-resource-read ratchet стал проходить после удаления старой подготовки. Остальные ограничения среды/старые ошибки не объявлены исправленными.
- `git diff --check`.

Reflection runner — вспомогательная дифференциальная проверка, не Unity/NUnitLite gate из репозитория. Он вызывает Test/TestCase и fixture SetUp/TearDown; результаты engine-bound cases не являются результатами игры. Checkout проверочной среды содержит источники без полного набора бинарных Assets, что также ограничивает asset-bound tests.

Добавлены проверки обоих первых шагов без output/recipient, operator-first при недоступной площадке и при её наличии только в колоде, facility-first при операторе в колоде, Prepared до Ready, непрерывной доставки, истечения/привязки generated claims, каждого ненужного ресурса, бесплатной доставки, совместимости mode/role и cache dependencies. Старый тест hypothetical Equipment preparation перенесён на READY output selection; у теста без preparation facts уточнён смысл.

Перед merge запустить полный EditMode suite в Unity, включая `AiProductionStagedPreparationTests`, `AiDevelopmentDecisionPathTests`, `AiDevelopmentRadarResourceGateTests`, `AiDevelopmentReadmissionTests`, `AiIndependentDevelopmentTests`, bank/attachment/generator и `ResearchProductionAttemptTransactionTests`. Дополнительно вручную проверить оба четырёхходовых маршрута (компонент 1, компонент 2, output, attachment), Challenge loss, успешный mint без deployment AP, доставку существующей армии, потерю подготовленной базы и capacity upgrade с отказом placement.

Пример команды после полного checkout репозитория:

```bash
"$UNITY_EDITOR" -batchmode -nographics -projectPath "$PROJECT_ROOT" \
  -runTests -testPlatform EditMode -testResults "$RESULTS_XML" -logFile "$UNITY_LOG"
```

Изменены существующие владельцы Analysis/Desire/Demand/Evaluation/Infrastructure/Provisioning/Execution/Continuity/State/Admission и существующие файлы тестов. Unity assets, GUID, сцены, gameplay costs и коэффициенты не менялись.
