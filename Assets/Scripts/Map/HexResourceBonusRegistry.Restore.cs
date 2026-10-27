using System;
using System.Collections.Generic;
using Game.Economy;
using Game.HexGrid;

namespace Game.Map
{
    [Serializable] public sealed class HexResourceBonusState
    {
        public HexCoord Hex;
        public ResourceYields Bonus;
    }

    public static partial class HexResourceBonusRegistry
    {
        // ResourceYields is mutable. Neither export nor replacement shares its values with
        // the registry; this preserves the owner even if a caller edits a captured DTO later.
        public static List<HexResourceBonusState> CaptureState()
        {
            var result = new List<HexResourceBonusState>();
            foreach (var pair in ByHex)
                result.Add(new HexResourceBonusState { Hex = pair.Key, Bonus = CopyBonus(pair.Value) });
            return result;
        }

        // Validate and copy everything before replacing the live table. Do not use Set here:
        // reconstruction must not publish newly observed physical content to vision listeners.
        public static void RestoreState(IEnumerable<HexResourceBonusState> state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var replacement = new Dictionary<HexCoord, ResourceYields>();
            foreach (var entry in state)
            {
                if (entry == null || replacement.ContainsKey(entry.Hex))
                    throw new ArgumentException("Missing or duplicate resource bonus entry.", nameof(state));
                replacement.Add(entry.Hex, CopyBonus(entry.Bonus));
            }
            ByHex.Clear();
            foreach (var pair in replacement) ByHex.Add(pair.Key, pair.Value);
        }

        private static ResourceYields CopyBonus(ResourceYields value) => value == null ? null : new ResourceYields
        {
            human = value.human, energy = value.energy, materials = value.materials, tech = value.tech,
        };
    }
}
