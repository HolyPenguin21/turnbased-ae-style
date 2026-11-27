using System;
using System.Linq;
using Game.Economy;
using Game.Map;
using Game.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Campaign
{
    internal sealed class CampaignAttackSupplyView
    {
        private const float Inset = 10;
        private readonly ScrollRect scroll;
        private readonly Column attacker, defender;

        internal CampaignAttackSupplyView(RectTransform header, ScrollRect scroll, Sprite icon, TMP_FontAsset font, Color color)
        {
            this.scroll = scroll;
            float halfWidth = scroll.content.rect.width * .5f;
            var title = CollectionUIElements.Label(header, "Starting supply", Inset, 0, header.rect.width - 2 * Inset, 25, 20);
            title.font = font;
            title.color = color;
            title.fontStyle = FontStyles.Bold;
            attacker = new Column("Attacker", header, scroll.content, Inset, halfWidth - 2 * Inset, icon, font, color);
            defender = new Column("Defender", header, scroll.content, halfWidth + Inset, halfWidth - 2 * Inset, icon, font, color);
        }

        internal void Show(CampaignAttackSupply supply, int battlefieldId)
        {
            float attackerHeight = attacker.Show(supply.Attacker, battlefieldId);
            float defenderHeight = defender.Show(supply.Defender, battlefieldId);
            float height = Math.Max(attackerHeight, defenderHeight) + 2 * Inset;
            scroll.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Math.Max(scroll.viewport.rect.height, height));
            scroll.StopMovement();
            scroll.verticalNormalizedPosition = 1;
        }

        internal static string Describe(ResourceYields resources) => resources.HasAnyYield
            ? string.Join(" · ", CampaignAttackSupply.Types.Where(type => resources.Get(type) > 0)
                .Select(type => $"+{resources.Get(type)} {type}"))
            : "None";

        private sealed class Column
        {
            private readonly TMP_Text[] totals;
            private readonly TMP_Text details;

            internal Column(string title, RectTransform header, RectTransform content, float x, float width,
                Sprite icon, TMP_FontAsset font, Color color)
            {
                var heading = CollectionUIElements.Label(header, title, x, 28, width, 23, 18);
                heading.font = font;
                heading.color = color;
                heading.fontStyle = FontStyles.Bold;

                float slotWidth = width / CampaignAttackSupply.Types.Count;
                totals = new TMP_Text[CampaignAttackSupply.Types.Count];
                for (int i = 0; i < totals.Length; i++)
                {
                    ResourceType type = CampaignAttackSupply.Types[i];
                    float left = x + i * slotWidth;
                    if (icon != null)
                    {
                        var node = CollectionUIElements.Rect(header, type + "Icon");
                        CollectionUIElements.Place(node, left, 58, 14, 14);
                        var image = node.gameObject.AddComponent<Image>();
                        image.sprite = icon;
                        image.color = ResourceIconVisual.GetColor(type);
                        image.raycastTarget = false;
                    }
                    float textInset = icon != null ? 19 : 0;
                    totals[i] = CollectionUIElements.Label(header, "", left + textInset, 54, slotWidth - textInset, 24, 16);
                    totals[i].font = font;
                    totals[i].color = color;
                    totals[i].textWrappingMode = TextWrappingModes.NoWrap;
                    totals[i].fontSizeMin = 12;
                    totals[i].fontSizeMax = 16;
                    totals[i].enableAutoSizing = true;
                }

                details = CollectionUIElements.Label(content, "", x, Inset, width, 1, 16);
                details.font = font;
                details.color = color;
                details.richText = false;
            }

            internal float Show(CampaignAttackSupply.Side side, int battlefieldId)
            {
                for (int i = 0; i < totals.Length; i++)
                {
                    var type = CampaignAttackSupply.Types[i];
                    int amount = side.Total.Get(type);
                    totals[i].text = amount == 0 ? $"0 {type}" : $"+{amount} {type}";
                }
                details.text = side.Sources.Count == 0 ? "No supplying regions" : string.Join("\n", side.Sources
                    .Select(source => source.Region.RegionId == battlefieldId
                        ? $"{source.Region.Name} (Battlefield) · {Describe(source.Bonus)}"
                        : $"{source.Region.Name} · {Describe(source.Bonus)}"));
                var rect = details.rectTransform;
                float height = Mathf.Ceil(details.GetPreferredValues(details.text, rect.rect.width, Mathf.Infinity).y) + 2;
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
                return height;
            }
        }
    }
}
