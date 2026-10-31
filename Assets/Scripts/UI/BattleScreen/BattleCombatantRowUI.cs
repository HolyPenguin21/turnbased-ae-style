using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Styles;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One side's row in BattleAttackPopupUI's Roll state — faction logo, the acting/defending
    // unit's name and HP, that SIDE's Fate (from its hero, if any — Fate is a shared per-side
    // resource the hero contributes, per the manual, not something the attacking/defending unit
    // card itself needs to be a hero to use) + a Spend button, and a tokens-slot strip. Tokens slots
    // are TokenSlotUI (see its own comment) — same shared component/sprites as TokenRowUI's turn-
    // order tokens.
    public class BattleCombatantRowUI : MonoBehaviour
    {
        [SerializeField] private Image logoImage;
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private TMP_Text hpText;
        // Hidden entirely when this side has no hero (see Setup) — matches the reference, where
        // the side without a hero shows no Fate/Spend at all, not just a greyed-out zero.
        [SerializeField] private GameObject fateRoot;
        [SerializeField] private TMP_Text fateText;
        [SerializeField] private Button spendButton;
        [SerializeField] private Transform tokenContainer;
        [SerializeField] private TokenSlotUI tokenSlotPrefab;
        [SerializeField] private TMP_Text successText;
        // How many tokens this side's pool is about to roll (or just rolled) — set by
        // BattleAttackPopupUI.SetTokenPoolSize before/at Roll time, alongside successText's own
        // after-the-roll hit count, so the player can see the pool size up front instead of only
        // inferring it by counting tokens slots once they've already appeared (see the user's own
        // request).
        [SerializeField] private TMP_Text tokenCountText;

        private readonly List<TokenSlotUI> _tokenSlots = new List<TokenSlotUI>();
        private readonly List<TokenSlotUI> _fatePreviewSlots = new List<TokenSlotUI>();
        // Last tokens array SetTokens was actually given — a Fate-spend reroll calls SetTokens again
        // with the SAME length array, only one entry actually changed (see BattleAttackPopupUI.
        // RerollOneMiss). Diffed against this so only that one slot replays its roll animation
        // instead of the whole row spinning again on every single Spend click.
        private bool[] _lastTokens;
        private UnitData _sideHero;

        public event Action SpendClicked;

        internal Button TutorialSpendButton => spendButton;

        // Name colour of the card being built in a Research/Production Challenge (#4B341E).
        public static readonly Color CardNameColor = new Color32(0x4B, 0x34, 0x1E, 0xFF);

        private void Awake()
        {
            if (spendButton != null)
                spendButton.onClick.AddListener(() => SpendClicked?.Invoke());
            DisableDecorativeRaycasts(transform);
        }

        // Purely decorative graphics (labels, tokens slots) must not swallow clicks aimed at the
        // Spend button when they overlap it. Graphics belonging to a Selectable are left alone.
        private static void DisableDecorativeRaycasts(Transform root)
        {
            foreach (Graphic g in root.GetComponentsInChildren<Graphic>(true))
                if (g.GetComponentInParent<Selectable>(true) == null)
                    g.raycastTarget = false;
        }

        private int _presentationVersion;
        public void CancelPendingRolls()
        {
            ++_presentationVersion;
            foreach (var slot in _tokenSlots) if (slot != null) slot.StopAllCoroutines();
        }

        public void Setup(UnitData unit, UnitData sideHero, Sprite factionLogo)
        {
            CancelPendingRolls();
            ClearFatePreview();
            _sideHero = sideHero;

            if (logoImage != null)
            {
                logoImage.sprite = factionLogo;
                logoImage.gameObject.SetActive(factionLogo != null);
            }
            // factionLogo is resolved per-owner by the caller (BattleAttackPopupUI.Begin/
            // BeginCaptureKill, ultimately BattleScreenUI.ResolveCatalog) — this row just shows
            // whatever it's handed. Unit type names still collide across sides though (an enemy
            // Light Infantry looks and reads identically to the player's own) — the owner's own
            // colour is what tells the two rows apart (see PlayerColorPalette); the debug
            // (You)/(Enemy) suffix this used to add on top of that has been dropped per the
            // user's own request.
            if (nameText != null)
            {
                nameText.text = unit != null ? unit.Name : string.Empty;
                nameText.color = unit?.Owner != null
                    ? PlayerColorPalette.Colors[unit.Owner.ColorIndex]
                    : Color.white;
            }
            if (hpText != null)
                hpText.text = unit != null ? $"HP: {unit.HitPointsCurrent}/{unit.HitPointsMax}" : string.Empty;

            if (fateRoot != null)
                fateRoot.SetActive(sideHero != null);
            RefreshFate();

            UIUtility.DestroyAndClear(_tokenSlots);
            _lastTokens = null;
            if (successText != null)
                successText.text = string.Empty;
            if (tokenCountText != null)
                tokenCountText.text = string.Empty;
        }

        // Pool size is known as soon as this side's Attack/Defense (+ any bonus tokens) or
        // Capture Kill pool is computed — BattleAttackPopupUI calls this at the same point it
        // sets up the rest of the row, so it's visible before Roll Token is even clicked.
        //
        // terrainBonus/constructionBonus/baseDefense (defender side only — see BattleAttackPopupUI.
        // Begin, the attacker row and Capture Kill's pool-size override both leave these at their
        // default 0): when the hex is contributing to this side's pool, the count alone doesn't
        // say why it's bigger than the unit's own stat, so the prefix spells out the breakdown
        // instead (per the user's own request) — plain "Tokens: {count}" otherwise, unchanged.
        public void SetTokenPoolSize(int count, int terrainBonus = 0, int constructionBonus = 0,
            int baseDefense = 0, int formationBonus = 0)
        {
            if (tokenCountText == null)
                return;

            if (terrainBonus == 0 && constructionBonus == 0 && formationBonus == 0)
            {
                tokenCountText.text = $"Tokens: {count}";
                return;
            }

            var parts = new List<string>();
            if (terrainBonus != 0)
                parts.Add($"Terrain({terrainBonus})");
            if (constructionBonus != 0)
                parts.Add($"Construction({constructionBonus})");
            if (formationBonus != 0)
                parts.Add($"Guarded({formationBonus})");
            parts.Add($"Defence({baseDefense})");
            tokenCountText.text = $"{string.Join(" + ", parts)}, Tokens: {count}";
        }

        private void RefreshFate()
        {
            if (fateText != null)
                fateText.text = _sideHero != null ? $"FATE: {_sideHero.Fate}" : string.Empty;
        }

        // Called by BattleAttackPopupUI right after a Fate spend actually rerolls a die — the
        // hero's own Fate has already been decremented by then, this just repaints the number.
        public void OnFateSpent()
        {
            RefreshFate();
        }

        // Challenge sessions own local Fate counters, including temporary R/P Fate.
        public void SetFateDisplay(int fate)
        {
            if (fateText != null)
                fateText.text = _sideHero != null ? $"FATE: {Mathf.Max(0, fate)}" : string.Empty;
        }

        public void SetSpendInteractable(bool interactable)
        {
            if (spendButton != null)
                spendButton.interactable = interactable;
        }

        // First roll (or the slot count otherwise changed) rebuilds the strip from scratch and
        // spins every slot. A reroll (see BattleAttackPopupUI.RerollOneMiss) calls this again
        // with the SAME-length array where only the rerolled die actually changed — those
        // existing slots are reused and only the changed one plays its roll animation, so
        // spending Fate doesn't re-spin tokens that already settled.
        //
        // rerolledIndex (optional): the slot Fate was JUST spent on, if any — a plain by-value
        // diff (below) misses a reroll that lands on the SAME hit/miss result as before (still a
        // miss, just a different miss), which used to skip the flip animation entirely even
        // though a real reroll happened (see the user's own report). Forces that one slot to
        // replay regardless of whether its value actually changed.
        // onComplete (optional): fired once every slot THIS call touched has finished flipping
        // (immediately, if none needed to animate) — same moment the success count itself is
        // revealed (see ShowResults above). BattleAttackPopupUI waits on this both for the
        // initial roll (RunRollAndDuel, before opening the Fate duel/enabling Spend) and for
        // every later Fate-spend reroll (RunHumanTurn/RunAiTurn's own _rerollAnimDone wait), so
        // nothing becomes available to click, and no side reacts, before the tokens have actually
        // landed (per the user's own request, 2026-08-24).
        // --- Research/Production Challenge helpers (see BattleAttackPopupUI.BeginResearchProduction) ---
        // Deliberately isolated from SetTokens so its behaviour is untouched for Ground Combat /
        // Capture Kill / Aviation.

        // A Research/Production DEFENDER is a card, not a UnitData — just a name + logo, no HP,
        // no Fate row, no Spend button. Follow with SetFixedSuccesses.
        public void SetupCardDefender(string displayName, Sprite factionLogo)
        {
            CancelPendingRolls();
            ClearFatePreview();
            _sideHero = null;
            if (logoImage != null)
            {
                logoImage.sprite = factionLogo;
                logoImage.gameObject.SetActive(factionLogo != null);
            }
            if (nameText != null)
            {
                nameText.text = displayName ?? string.Empty;
                nameText.color = CardNameColor;
            }
            if (hpText != null)
                hpText.text = string.Empty;
            if (fateRoot != null)
                fateRoot.SetActive(false);
            if (spendButton != null)
                spendButton.interactable = false;
            UIUtility.DestroyAndClear(_tokenSlots);
            _lastTokens = null;
            if (successText != null)
                successText.text = string.Empty;
            if (tokenCountText != null)
                tokenCountText.text = string.Empty;
        }

        // The card defender's fixed defence: CardDefinition.fate guaranteed successes, shown at
        // full immediately with NO roll animation (a card doesn't roll its own defence). Rebuilds
        // the strip like SetTokens's count-changed branch, but every slot lands on hit via
        // TokenSlotUI.SetImmediate.
        public void SetFixedSuccesses(int count)
        {
            ClearFatePreview();
            count = Mathf.Max(0, count);
            UIUtility.DestroyAndClear(_tokenSlots);
            if (tokenContainer != null && tokenSlotPrefab != null)
            {
                for (int i = 0; i < count; i++)
                {
                    TokenSlotUI slot = Instantiate(tokenSlotPrefab, tokenContainer);
                    Game.Audio.SceneUIAudioBinder.BindCreatedRoot(slot);
                    slot.gameObject.SetActive(true);
                    DisableDecorativeRaycasts(slot.transform);
                    slot.SetImmediate(true);
                    _tokenSlots.Add(slot);
                }
            }
            _lastTokens = new bool[count];
            for (int i = 0; i < count; i++)
                _lastTokens[i] = true;
            if (successText != null)
                successText.text = count.ToString();
            if (tokenCountText != null)
                tokenCountText.text = $"Defense: {count}";
        }

        // The R/P Fate-overflow rule: when every existing attacker die is already a hit but the
        // success count is still below target, a spent Fate ADDS a die instead of rerolling one.
        // Appends exactly one new slot, rolls only it, leaves every existing slot untouched, then
        // refreshes _lastTokens / the success counter. onComplete fires when the new die lands.
        public void AppendToken(bool hit, System.Action onComplete = null)
        {
            int version = ++_presentationVersion;
            if (tokenContainer == null || tokenSlotPrefab == null)
            {
                if (_lastTokens != null)
                {
                    var grown = new bool[_lastTokens.Length + 1];
                    System.Array.Copy(_lastTokens, grown, _lastTokens.Length);
                    grown[grown.Length - 1] = hit;
                    _lastTokens = grown;
                }
                onComplete?.Invoke();
                return;
            }

            TokenSlotUI slot;
            if (_fatePreviewSlots.Count > 0)
            {
                slot = _fatePreviewSlots[0];
                _fatePreviewSlots.RemoveAt(0);
            }
            else
            {
                slot = Instantiate(tokenSlotPrefab, tokenContainer);
                Game.Audio.SceneUIAudioBinder.BindCreatedRoot(slot);
            }
            slot.gameObject.SetActive(true);
            DisableDecorativeRaycasts(slot.transform);
            _tokenSlots.Add(slot);

            int prevLen = _lastTokens != null ? _lastTokens.Length : 0;
            var next = new bool[prevLen + 1];
            if (_lastTokens != null)
                System.Array.Copy(_lastTokens, next, prevLen);
            next[prevLen] = hit;
            _lastTokens = next;

            if (tokenCountText != null)
                tokenCountText.text = $"Tokens: {next.Length}";

            void Done()
            {
                if (version != _presentationVersion) return;
                if (successText != null)
                {
                    int successes = 0;
                    foreach (bool h in _lastTokens)
                        if (h) successes++;
                    successText.text = successes.ToString();
                }
                onComplete?.Invoke();
            }
            slot.PlayRoll(hit, 0, 1, TokenSlotUI.FateRerollDuration, Done);
        }

        public void SetFatePreview(int fate)
        {
            int count = ResearchProductionSystem.FatePreviewCount(_lastTokens, fate);
            while (_fatePreviewSlots.Count > count)
            {
                int last = _fatePreviewSlots.Count - 1;
                TokenSlotUI slot = _fatePreviewSlots[last];
                _fatePreviewSlots.RemoveAt(last);
                slot.gameObject.SetActive(false);
                Destroy(slot.gameObject);
            }
            if (tokenContainer == null || tokenSlotPrefab == null) return;
            while (_fatePreviewSlots.Count < count)
            {
                TokenSlotUI slot = Instantiate(tokenSlotPrefab, tokenContainer);
                Game.Audio.SceneUIAudioBinder.BindCreatedRoot(slot);
                slot.gameObject.SetActive(true);
                DisableDecorativeRaycasts(slot.transform);
                slot.SetFatePreview();
                _fatePreviewSlots.Add(slot);
            }
        }

        private void ClearFatePreview()
        {
            foreach (TokenSlotUI slot in _fatePreviewSlots)
                if (slot != null) slot.gameObject.SetActive(false);
            UIUtility.DestroyAndClear(_fatePreviewSlots);
        }

        public void SetSuccessModifier(int modifier)
        {
            if (successText == null || _lastTokens == null) return;
            int hits = 0;
            foreach (bool hit in _lastTokens) if (hit) hits++;
            successText.text = modifier == 0 ? hits.ToString()
                : $"{Mathf.Max(0, hits + modifier)} ({hits}{(modifier > 0 ? "+" : "")}{modifier})";
        }

        public void SetTokens(bool[] tokens, int rerolledIndex = -1, System.Action onComplete = null)
        {
            int version = ++_presentationVersion;
            if (tokens == null)
            {
                onComplete?.Invoke();
                return;
            }

            var toAnimate = new List<int>();
            if (tokenContainer != null && tokenSlotPrefab != null && _tokenSlots.Count != tokens.Length)
            {
                UIUtility.DestroyAndClear(_tokenSlots);
                for (int i = 0; i < tokens.Length; i++)
                {
                    TokenSlotUI slot = Instantiate(tokenSlotPrefab, tokenContainer);
                    Game.Audio.SceneUIAudioBinder.BindCreatedRoot(slot);
                    // tokenSlotPrefab is wired (in the scene) to an inactive template object
                    // rather than TokenRow.prefab's always-active TokenSlot prefab asset — Unity's
                    // Instantiate carries an inactive source's activeSelf over to the clone, so
                    // without this the clone starts inactive and PlayRoll's own
                    // !gameObject.activeInHierarchy check silently skips straight to
                    // SetImmediate, no flip animation at all (see the user's own report: no
                    // animated tokens in the attack popup, unlike TokenRowUI's turn-order roll).
                    slot.gameObject.SetActive(true);
                    DisableDecorativeRaycasts(slot.transform);
                    _tokenSlots.Add(slot);
                    toAnimate.Add(i);
                }
            }
            else
            {
                for (int i = 0; i < tokens.Length && i < _tokenSlots.Count; i++)
                    if (i == rerolledIndex || _lastTokens == null || i >= _lastTokens.Length || _lastTokens[i] != tokens[i])
                        toAnimate.Add(i);
            }
            _lastTokens = (bool[])tokens.Clone();

            // Success count is revealed together with the tokens landing, not before — showing the
            // number while the strip is still mid-flip let a player read the outcome ahead of the
            // animation (per the user's own request, 2026-08-24: results only appear once the roll
            // animation has actually finished).
            void ShowResults()
            {
                if (version != _presentationVersion) return;
                if (successText != null)
                {
                    int successes = 0;
                    foreach (bool hit in tokens)
                        if (hit) successes++;
                    successText.text = successes.ToString();
                }
                onComplete?.Invoke();
            }

            if (toAnimate.Count == 0)
            {
                ShowResults();
                return;
            }
            int pending = toAnimate.Count;
            void SlotDone()
            {
                pending--;
                if (pending == 0)
                    ShowResults();
            }
            // index/count (position within THIS call's own toAnimate list, not the whole tokens
            // array) so a full first roll lands its tokens one after another across the row, and a
            // later single-die Fate reroll still gets the full Fate-reroll duration to itself
            // rather than landing near-instantly as if it were still part of an N-die group.
            for (int pos = 0; pos < toAnimate.Count; pos++)
            {
                int i = toAnimate[pos];
                float duration = rerolledIndex >= 0 ? TokenSlotUI.FateRerollDuration : TokenSlotUI.FullRollDuration;
                _tokenSlots[i].PlayRoll(tokens[i], pos, toAnimate.Count, duration, SlotDone);
            }
        }
    }
}
