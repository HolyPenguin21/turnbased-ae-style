#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Cards;
using Game.Players;

namespace Game.EditorTests
{
    // Reads committed attachment content without requiring native AssetDatabase APIs.
    // Shared by Research and Production tests; gameplay still uses Unity serialization.
    internal static class AttachmentContentTestData
    {
        internal const string NeutralPath = "Assets/Cards/Neutral/CardCatalog_Neutral.asset";
        internal static string Root => new[] { ".", "src" }.Select(Path.GetFullPath)
            .First(p => File.Exists(Path.Combine(p, NeutralPath)));
        internal static string Text(string path) => File.ReadAllText(Path.Combine(Root, path));
        internal static string Scalar(string block, string field) => Regex.Match(block,
            @"(?m)^[ \t]+" + field + @":[ \t]*([^\r\n]*)$").Groups[1].Value.Trim();
        internal static int Number(string block, string field) => int.Parse(Scalar(block, field));
        internal static IEnumerable<string> Blocks(string path)
        {
            string cards = Regex.Match(Text(path),
                @"(?ms)^  cards:\r?\n(.*?)(?=^  [A-Za-z]\w*:|\z)").Groups[1].Value;
            return Regex.Split("\n" + cards, @"\r?\n  - id: ").Skip(1);
        }
        internal static List<T> Packed<T>(string block, string field) where T : struct
        {
            string hex = Scalar(block, field);
            var result = new List<T>();
            for (int i = 0; i < hex.Length; i += 8)
            {
                byte[] bytes = Enumerable.Range(0, 4)
                    .Select(n => Convert.ToByte(hex.Substring(i + n * 2, 2), 16)).ToArray();
                result.Add((T)Enum.ToObject(typeof(T), BitConverter.ToInt32(bytes, 0)));
            }
            return result;
        }
        internal static List<string> Tags(string block, string field, int indent = 6)
        {
            string prefix = new string(' ', indent);
            string body = Regex.Match(block, @"(?m)^" + prefix + field
                + @":([^\r\n]*)(?:\r?\n" + prefix + @"- [^\r\n]*)*").Value;
            return Regex.Matches(body, @"(?m)^" + prefix + @"- ([^\r\n]*)").Cast<Match>()
                .Select(m => m.Groups[1].Value.Trim()).ToList();
        }
        internal static CardDefinition Read(string block) => new CardDefinition
        {
            authoredKey = Scalar(block, "authoredKey"), displayName = Scalar(block, "displayName"),
            cardType = (CardType)Number(block, "cardType"),
            attachmentSlot = string.IsNullOrEmpty(Scalar(block, "attachmentSlot"))
                ? AttachmentSlot.Equipment : (AttachmentSlot)Number(block, "attachmentSlot"),
            faction = (Faction)Number(block, "faction"), apCost = Number(block, "apCost"),
            activationApCost = Number(block, "activationApCost"), fate = Number(block, "fate"),
            resourceCost = new ResourceCost { human = Number(block, "human"), energy = Number(block, "energy"),
                materials = Number(block, "materials"), tech = Number(block, "tech") },
            attack = Number(block, "attack"), defenseRating = Number(block, "defenseRating"),
            resistanceRating = Number(block, "resistanceRating"), range = Number(block, "range"),
            hitPoints = Number(block, "hitPoints"), moveMax = Number(block, "moveMax"),
            initiative = Number(block, "initiative"), commandRating = Number(block, "commandRating"),
            antiAirRadius = Number(block, "antiAirRadius"),
            isAviation = Number(block, "isAviation") != 0,
            unitTypeTags = Packed<UnitTypeTag>(block, "unitTypeTags"),
            grantedAbilities = Tags(block, "grantedAbilities", 4),
            equipment = new EquipmentGrant
            {
                hostTypeTags = Packed<UnitTypeTag>(block, "hostTypeTags"),
                hostKinds = Packed<EquipmentHostKind>(block, "hostKinds"),
                clearAbilityFamilies = Packed<AbilityFamily>(block, "clearAbilityFamilies"),
                removeAbilities = Tags(block, "removeAbilities"), addAbilities = Tags(block, "addAbilities"),
                statChanges = Regex.Matches(block,
                    @"- stat: (\d+)\s+amount: (-?\d+)\s+isOverride: (\d+)").Cast<Match>()
                    .Select(m => new EquipmentStatChange { stat = (EquipmentStat)int.Parse(m.Groups[1].Value),
                        amount = int.Parse(m.Groups[2].Value), isOverride = m.Groups[3].Value != "0" }).ToList(),
            },
        };
    }
}
#endif
