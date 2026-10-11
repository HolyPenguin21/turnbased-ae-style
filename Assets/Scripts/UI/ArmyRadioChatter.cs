using System;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.UI
{
    public enum ArmyRadioLanguage { English, Russian }
    public enum ArmyRadioSituation { Contact, Waiting, Moving, Garrison, Airborne, Stealth, Damaged, Ready }

    [Serializable]
    public sealed class ArmyRadioChatterSettings
    {
        public bool enabled = true;
        public ArmyRadioLanguage language = ArmyRadioLanguage.English;
        [Min(5f)] public float ambientInterval = 18f;
        [Min(4f)] public float minimumGap = 8f;
        [Min(10f)] public float armyCooldown = 45f;
        [Min(2f)] public float displaySeconds = 4.5f;
        [Min(1f)] public float waitingSeconds = 12f;
        [Min(40f)] public float artHeight = 78f;
        [Min(140f)] public float messageWidth = 220f;
        [Min(12f)] public float fontSize = 16f;
    }

    // Cosmetic read-only policy. No tactical evaluation, purchases, or random game rolls.
    public static class ArmyRadioChatter
    {
        public static UnitData FirstUnit(ArmyData army)
        {
            if (army == null) return null;
            foreach (UnitData unit in army.Members)
                if (unit != null && !unit.IsPrisoner && unit.HitPointsCurrent > 0) return unit;
            return null;
        }

        public static Sprite DetailArt(UnitData unit) => unit == null ? null
            : unit.OriginatingCard != null && unit.OriginatingCard.detailArt != null
                ? unit.OriginatingCard.detailArt : unit.DetailArt;

        public static bool CanSpeak(ArmyData army, PlayerSetupData listener) =>
            army != null && listener != null && army.Owner == listener && !listener.IsNeutral
            && !listener.IsEliminated && !army.IsPrison && FirstUnit(army) != null;

        public static bool SeesEnemy(ArmyData army)
        {
            if (army == null || army.Owner == null) return false;
            HexCoord origin = army.Controller != null ? army.Controller.CurrentHex : army.Hex;
            foreach (HexCoord hex in HexGridMath.HexesInRange(origin, VisionSystem.ArmyVisionRadius(army)))
            {
                if (!VisionSystem.IsVisible(army.Owner, hex)) continue;
                foreach (ArmyData other in ArmyRegistry.AllAt(hex))
                    if (other != army && other.Owner != army.Owner && !other.IsPrison
                        && FirstUnit(other) != null && (other.Controller == null || !other.Controller.IsMoving)
                        && StealthSystem.HasAnyTargetableMember(other, army.Owner))
                        return true;
            }
            return false;
        }

        public static ArmyRadioSituation Situation(ArmyData army, bool newContact, bool moved, float quietSeconds)
        {
            if (newContact) return ArmyRadioSituation.Contact;
            foreach (UnitData unit in army.Members)
                if (unit != null && unit.HitPointsCurrent > 0 && unit.HitPointsCurrent < unit.HitPointsMax)
                    return ArmyRadioSituation.Damaged;
            if (StealthSystem.IsArmyFullyHidden(army)) return ArmyRadioSituation.Stealth;
            if (army.IsAirArmy) return ArmyRadioSituation.Airborne;
            if (army.IsGarrison || army.IsAirfield) return ArmyRadioSituation.Garrison;
            if (moved || army.HasActivatedThisTurn) return ArmyRadioSituation.Moving;
            return quietSeconds > 0f ? ArmyRadioSituation.Waiting : ArmyRadioSituation.Ready;
        }

        // Parallel tables keep localization independent of game mechanics. Variations are
        // selected by a presentation-owned counter; UnityEngine.Random is never consumed.
        private static readonly string[][] English =
        {
            new[] { "Enemy in sight. Awaiting orders.", "Contact reported. Weapons ready.", "Eyes on the enemy. Standing by.", "Visual contact. Keeping watch.", "We've got company. They look unfriendly." },
            new[] { "Holding position. Awaiting orders.", "Still here. Still breathing.", "Standing by. The dust isn't going anywhere.", "Quiet sector. Keeping watch.", "Any orders? The sand is getting comfortable." },
            new[] { "Position reached. Standing by.", "Boots full of sand. Still operational.", "We're here. What's next?", "Position report: still on the map.", "Radio check. All accounted for." },
            new[] { "Garrison reporting. Keeping watch.", "Our post is covered.", "Perimeter watch. Radio standing by.", "Home station. Standing by.", "Coffee's cold. Watch continues." },
            new[] { "Air wing on frequency.", "Flight crew reporting. Standing by.", "Air wing ready for orders.", "Radio check from the flight crew.", "Pilot here. Keeping an eye on things." },
            new[] { "Keeping a low profile.", "Quiet on the radio. Quiet on the ground.", "Staying out of sight.", "Low voices. Eyes open.", "We're just another patch of dust." },
            new[] { "Took some damage. Still here.", "Could use repairs. Standing by.", "A little battered. Radio still works.", "Damage report sent. Awaiting orders.", "Dents are temporary. Probably." },
            new[] { "Radio check. Loud and clear.", "Standing by for orders.", "All eyes open. Reporting in.", "Another day in the dust.", "On frequency. Try not to lose us." }
        };

        private static readonly string[][] Russian =
        {
            new[] { "Вижу противника. Ждём приказа.", "Есть контакт. Оружие наготове.", "Противник в поле зрения. На связи.", "Визуальный контакт. Наблюдаем.", "У нас компания. И совсем не дружелюбная." },
            new[] { "Держим позицию. Ждём приказа.", "Всё ещё здесь. Всё ещё дышим.", "Ждём. Пыль никуда не торопится.", "В секторе тихо. Наблюдаем.", "Есть приказы? Уже привыкли к песку." },
            new[] { "На позиции. Ждём приказа.", "Сапоги полны песка. Мы в порядке.", "Мы на месте. Что дальше?", "Доклад: с карты пока не исчезли.", "Проверка связи. Все на месте." },
            new[] { "Гарнизон на связи. Несём дозор.", "Наш пост под присмотром.", "Наблюдаем за периметром. На связи.", "Домашняя станция. Ждём приказа.", "Кофе остыл. Дозор продолжается." },
            new[] { "Авиакрыло на частоте.", "Лётный состав на связи.", "Авиакрыло ждёт приказа.", "Проверка связи от лётного состава.", "Пилот на связи. Осматриваемся." },
            new[] { "Не привлекаем внимания.", "Тихо в эфире. Тихо на земле.", "Остаёмся вне поля зрения.", "Говорим тихо. Смотрим внимательно.", "Мы просто ещё немного пыли." },
            new[] { "Есть повреждения. Мы ещё здесь.", "Не помешает ремонт. Ждём приказа.", "Немного потрепало. Рация работает.", "О повреждениях доложили. Ждём.", "Вмятины — дело временное. Наверное." },
            new[] { "Проверка связи. Слышим отлично.", "Ждём приказа.", "Наблюдаем. Докладываем обстановку.", "Ещё один день в пыли.", "Мы на частоте. Не потеряйте нас." }
        };

        public static string Phrase(ArmyRadioSituation situation, ArmyRadioLanguage language, int variation)
        {
            string[][] table = language == ArmyRadioLanguage.Russian ? Russian : English;
            string[] lines = table[(int)situation];
            return lines[(variation % lines.Length + lines.Length) % lines.Length];
        }
    }
}
