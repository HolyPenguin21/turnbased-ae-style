#if UNITY_INCLUDE_TESTS
using System;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Cards;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class UnitAbilityCatalogTests
    {
        private const string CatalogPath = "Assets/Cards/UnitAbilityCatalog.asset";

        // File-backed: the authored asset lists every tag once, sorted by its synced title, and
        // every ability has a description for the detail panels.
        [Test]
        public void AuthoredCatalog_CoversEveryTag_SortedByTitle_WithDescriptions()
        {
            var entries = Regex.Split(AttachmentContentTestData.Text(CatalogPath)
                    .Split(new[] { "  knownAbilities:" }, StringSplitOptions.None)[1]
                    .Split(new[] { "\n  criticalDamageMultiplier:" }, StringSplitOptions.None)[0], @"(?m)^  - tag: ")
                .Skip(1)
                .Select(block => new
                {
                    tag = block.Split('\n')[0].Trim(),
                    title = AttachmentContentTestData.Scalar(block, "title").Trim('\''),
                    description = AttachmentContentTestData.Scalar(block, "description").Trim('\''),
                }).ToList();

            CollectionAssert.AreEquivalent(UnitAbilities.All, entries.Select(e => e.tag));
            foreach (var entry in entries)
            {
                Assert.AreEqual(UnitAbilities.PrettyName(entry.tag), entry.title, entry.tag);
                Assert.IsNotEmpty(entry.description, entry.tag);
            }
            CollectionAssert.AreEqual(
                entries.Select(e => e.title).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList(),
                entries.Select(e => e.title).ToList());
        }

        [Test]
        public void Format_UsesAbbreviationOrTitle_AndDetailedBlocks()
        {
            var catalog = ScriptableObject.CreateInstance<UnitAbilityCatalog>();
            try
            {
                catalog.knownAbilities.Add(new UnitAbilityCatalog.AbilityEntry
                {
                    tag = UnitAbilities.RapidReaction, title = "Rapid Reaction", abbreviation = "Rapid", description = "0 AP",
                });
                catalog.knownAbilities.Add(new UnitAbilityCatalog.AbilityEntry
                {
                    tag = UnitAbilities.CeramicArmor, title = "Ceramic Armor", abbreviation = string.Empty, description = string.Empty,
                });
                string[] tags = { UnitAbilities.RapidReaction, UnitAbilities.CeramicArmor };

                Assert.AreEqual("Rapid Ceramic Armor", catalog.FormatAbilities(tags));
                Assert.AreEqual("\nRapid Reaction\n  0 AP\n\nCeramic Armor", catalog.FormatAbilitiesDetailed(tags));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(catalog);
            }
        }
    }
}
#endif
