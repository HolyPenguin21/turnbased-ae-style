using System;
using System.Linq;
using Game.Core;
using Game.Players;
using Game.Progression;
using Game.UI;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Campaign
{
    public sealed class CampaignUI : MonoBehaviour
    {
        [SerializeField] private GameConfig gameConfig;
        [SerializeField] private bool enableHumanTestAutoResolve;
        [SerializeField] private Sprite endTurnSprite, actionButtonSprite;
        [SerializeField] private Sprite ironConcordLogo, ashenLogo, vesselsLogo;
        private Sprite frameSprite, paperSprite, metalSprite;
        private static readonly Color PaperInk = new Color(.17f, .135f, .095f);
        private static readonly Color Cream = new Color(.90f, .83f, .67f);
        private RectTransform canvas, sidebar, footer, modal, modalContent;
        private TMP_Text heading, hoverLabel;
        private CampaignMapView map;
        private int? selected, hovered;
        private string selectedDeckId;
        private Action cancelModal;
        private bool rewardOpen, defenceDeferred, acting;
        private readonly CampaignBattleResolver resolver = new CampaignBattleResolver();
        private CampaignController Controller => CampaignMatchBridge.Controller;
        private void Start()
        {
            canvas = CollectionUIElements.Canvas("PlanetaryCampaign", new Vector2(1920, 1080));
            frameSprite = Resources.Load<Sprite>("Campaign/MetalFrame");
            paperSprite = Resources.Load<Sprite>("Campaign/Parchment");
            metalSprite = Resources.Load<Sprite>("Campaign/ConsoleMetal");
            var background = CollectionUIElements.Panel(canvas, "PlanetBackground"); CollectionUIElements.Stretch(background);
            Skin(background, false, false);
            var header = CollectionUIElements.Panel(canvas, "CampaignHeader"); CollectionUIElements.Place(header, 26, 18, 1868, 82);
            Skin(header, false);
            heading = Label(header, "PLANETARY CAMPAIGN", 28, 16, 1530, 50, 31);
            heading.color = Cream; heading.fontStyle = FontStyles.Bold;
            Button(header, "MENU", 1678, 17, 164, 48, () => SceneManager.LoadScene(SceneNames.MainMenu));
            var surface = CollectionUIElements.Panel(canvas, "PlanetSurface"); CollectionUIElements.Place(surface, 26, 112, 1400, 780);
            Skin(surface, false);
            surface.GetComponent<Image>().raycastTarget = false;
            var mapRect = CollectionUIElements.Rect(surface, "Regions"); CollectionUIElements.Stretch(mapRect);
            mapRect.offsetMin = new Vector2(20, 20); mapRect.offsetMax = new Vector2(-20, -20);
            mapRect.gameObject.AddComponent<RectMask2D>();
            map = mapRect.gameObject.AddComponent<CampaignMapView>();
            sidebar = CollectionUIElements.Panel(canvas, "RegionInformation"); CollectionUIElements.Place(sidebar, 1440, 112, 454, 780);
            Skin(sidebar, true);
            footer = CollectionUIElements.Panel(canvas, "CampaignHistory"); CollectionUIElements.Place(footer, 26, 907, 1868, 150);
            Skin(footer, true);
            hoverLabel = Label(surface, "", 30, 24, 1310, 34, 19); hoverLabel.color = Cream;
            if (!CampaignMatchBridge.Load(out string error)) { Failure(error ?? "No saved campaign.", () => SceneManager.LoadScene(SceneNames.MainMenu)); return; }
            try { map.Build(Controller.Snapshot.Regions, ClickRegion, HoverRegion); }
            catch (Exception ex) { Failure(ex.Message, () => SceneManager.LoadScene(SceneNames.Campaign)); return; }
            if (!ProgressionContext.Initialize(gameConfig)) { Failure(ProgressionContext.Error, () => SceneManager.LoadScene(SceneNames.Campaign)); return; }
            Recover();
        }
        private void Recover()
        {
            CloseModal(); rewardOpen = false;
            if (!CampaignMatchBridge.RecoverReward(gameConfig, out string error)) { Failure(error, Recover); return; }
            Render();
            var op = Controller.Snapshot.PendingOperation;
            if (op?.Manual == true && op.ResultRecorded && !op.RewardAcknowledged)
            {
                rewardOpen = true; RefreshMap();
                var rewards = gameObject.GetComponent<CollectionRewardUI>() ?? gameObject.AddComponent<CollectionRewardUI>();
                rewards.Resume(gameConfig, () => { rewardOpen = false; Recover(); });
                return;
            }
            if (Controller.Snapshot.Phase == CampaignPhase.ShowingResult) ShowResult();
            else if (op?.Manual == true && !op.ResultRecorded && !defenceDeferred) OpenDeckSelection();
            else if (op != null && !op.Manual && !op.ResultRecorded)
                Attempt(() => { Controller.ResolveFast(CampaignDeck.Standard(op.AttackerFaction, ProgressionContext.Collection.Rules), CampaignDeck.Standard(op.DefenderFaction, ProgressionContext.Collection.Rules), resolver); Render(); ShowResult(); });
        }
        private void Update()
        {
            bool escape = UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame;
            if (escape && modal == null && !rewardOpen && selected.HasValue) { selected = null; hovered = null; Render(); return; }
            if (escape && modal != null)
            {
                cancelModal?.Invoke();
                // Results require explicit Continue; ESC never acknowledges an operation.
                return;
            }
            if (Controller == null || modal != null || rewardOpen || acting) return;
            if (Controller.Phase != CampaignPhase.AwaitingFactionAction || !Controller.IsAiTurn) return;
            acting = true;
            Attempt(() => { Controller.TakeAiTurn(ProgressionContext.Collection.Rules, resolver); selected = null; defenceDeferred = false; Recover(); });
            acting = false;
        }
        private void Attempt(Action action)
        { try { action(); } catch (Exception ex) { Failure(ex.Message, Recover); } }
        private void HoverRegion(int? id)
        {
            hovered = id;
            var r = id.HasValue ? Controller.Snapshot.Regions.Find(x => x.RegionId == id) : null;
            hoverLabel.text = r == null ? "" : r.Name + "  ·  " + FactionName(r.OwnerFaction);
            RefreshMap();
        }
        private void ClickRegion(int id)
        {
            if (modal != null || rewardOpen) return;
            var state = Controller.Snapshot;
            if (selected.HasValue && CampaignRules.CanAttack(state, state.HumanFaction, selected.Value, id)) { ConfirmAttack(selected.Value, id); return; }
            selected = id; Render();
        }
        private void Render()
        {
            if (Controller == null) return;
            var s = Controller.Snapshot;
            heading.text = s.PlanetName + "  ·  Round " + s.RoundNumber + "  ·  " + FactionName(s.CurrentFaction);
            CollectionUIElements.Clear(sidebar); CollectionUIElements.Clear(footer);
            Skin(sidebar, true); Skin(footer, true);
            var title = Label(sidebar, "TERRITORIES", 28, 26, 398, 36, 26); title.fontStyle = FontStyles.Bold;
            int y = 78;
            foreach (var f in s.Factions)
            {
                Logo(sidebar, f.Faction, 30, y, 42);
                Label(sidebar, FactionName(f.Faction) + (f.Eliminated ? " — eliminated" : ""), 85, y + 4, 276, 36, 23);
                var count = Label(sidebar, s.Regions.Count(r => r.OwnerFaction == f.Faction).ToString(), 360, y + 4, 62, 36, 25);
                count.alignment = TextAlignmentOptions.Right; y += 55;
            }
            Rule(sidebar, 28, 249, 398);
            var region = selected.HasValue ? s.Regions.Find(r => r.RegionId == selected.Value) : null;
            var regionTitle = Label(sidebar, region == null ? "SELECT A REGION" : region.Name.ToUpperInvariant(), 28, 269, 398, 64, 28);
            regionTitle.fontStyle = FontStyles.Bold;
            if (region != null)
            {
                var preview = CollectionUIElements.Rect(sidebar, "RegionTerrainPreview"); CollectionUIElements.Place(preview, 30, 340, 394, 126);
                var terrain = preview.gameObject.AddComponent<RawImage>(); terrain.texture = map.SurfaceTexture; terrain.raycastTarget = false;
                var center = CampaignMapView.SurfaceUV(CampaignRegionGraphic.VisibleCenter(region));
                terrain.uvRect = new Rect(Mathf.Clamp(center.x - .16f, 0, .68f), Mathf.Clamp(center.y - .10f, 0, .80f), .32f, .20f);
                Frame(preview);
                Label(sidebar, "OWNER", 30, 482, 98, 34, 20);
                Logo(sidebar, region.OwnerFaction, 132, 478, 36);
                Label(sidebar, FactionName(region.OwnerFaction), 179, 482, 245, 34, 23);
                Label(sidebar, "NEIGHBORING REGIONS", 30, 530, 394, 32, 20).fontStyle = FontStyles.Bold;
                var neighbors = CollectionUIElements.Scroll(sidebar, "Neighbors", 28, 565, 396, 118, true);
                neighbors.parent.parent.GetComponent<Image>().color = new Color(0, 0, 0, .055f);
                int row = 0;
                foreach (int id in region.NeighborIds)
                {
                    var neighbor = s.Regions.Find(r => r.RegionId == id);
                    var dot = CollectionUIElements.Panel(neighbors, "OwnerColor"); CollectionUIElements.Place(dot, 12, row * 30 + 10, 8, 8);
                    dot.GetComponent<Image>().color = CampaignMapView.ColorFor(neighbor.OwnerFaction); dot.GetComponent<Image>().raycastTarget = false;
                    Label(neighbors, neighbor.Name, 30, row * 30, 330, 30, 20); row++;
                }
                neighbors.sizeDelta = new Vector2(neighbors.sizeDelta.x, Math.Max(118, row * 30));
                bool can = s.Regions.Where(r => r.OwnerFaction == s.HumanFaction).Any(r => CampaignRules.CanAttack(s, s.HumanFaction, r.RegionId, region.RegionId));
                if (can)
                    Button(sidebar, "ATTACK REGION", 28, 708, 398, 50, () =>
                    { int from = s.Regions.First(r => CampaignRules.CanAttack(s, s.HumanFaction, r.RegionId, region.RegionId)).RegionId; ConfirmAttack(from, region.RegionId); });
                else Label(sidebar, region.OwnerFaction == s.HumanFaction ? "Select a highlighted enemy neighbor." : "Attack unavailable this turn.", 30, 707, 394, 54, 19);
            }
            else Label(sidebar, "Select a territory to see its owner and neighboring regions.\n\nSelect your territory to highlight available attacks.", 30, 358, 394, 200, 23);
            string status = s.Phase == CampaignPhase.CampaignFinished ? (s.Factions.Find(f => f.Faction == s.HumanFaction).Eliminated ? "CAMPAIGN DEFEAT — your faction has been eliminated." : "PLANET CONQUERED — campaign completed.")
                : s.PendingOperation != null ? "Pending battle — " + s.Phase : s.CurrentFaction == s.HumanFaction ? "Your turn: attack an adjacent enemy region or end turn." : "AI faction is choosing an attack.";
            Label(footer, status, 28, 17, s.Phase == CampaignPhase.CampaignFinished ? 880 : 1510, 32, 23).fontStyle = FontStyles.Bold;
            Rule(footer, 28, 54, 1570);
            var history = s.BattleHistory.AsEnumerable().Reverse().Take(4).ToList();
            Label(footer, "RECENT BATTLES", 28, 70, 205, 40, 20).fontStyle = FontStyles.Bold;
            for (int i = 0; i < history.Count; i++)
            {
                var h = history[i];
                var entry = Label(footer, "Round " + h.Round + ": " + FactionName(h.Attacker) + " → " + h.RegionName + " · "
                    + (h.Outcome == CampaignOutcome.Draw ? "draw" : h.Captured ? "captured" : "defended"),
                    244 + (i % 2) * 665, 65 + (i / 2) * 30, 650, 27, 17);
                entry.textWrappingMode = TextWrappingModes.NoWrap;
                entry.overflowMode = TextOverflowModes.Ellipsis;
            }
            if (s.Phase == CampaignPhase.AwaitingFactionAction && s.CurrentFaction == s.HumanFaction)
                EndTurnButton(footer, () => Attempt(() => { Controller.EndTurn(); selected = null; Recover(); }));
            else if (s.PendingOperation?.Manual == true && !s.PendingOperation.ResultRecorded)
                Button(footer, "RESUME BATTLE", 1620, 56, 220, 56, () => { defenceDeferred = false; OpenDeckSelection(); });
            if (s.Phase == CampaignPhase.CampaignFinished)
            {
                var h = s.BattleHistory.Where(b => b.Attacker == s.HumanFaction || b.Defender == s.HumanFaction).ToList();
                int wins = h.Count(b => b.Outcome != CampaignOutcome.Draw && (b.Outcome == CampaignOutcome.AttackerVictory ? b.Attacker : b.Defender) == s.HumanFaction);
                Label(footer, $"Rounds: {s.RoundNumber}  ·  Wins: {wins}  ·  Losses: {h.Count(b => b.Outcome != CampaignOutcome.Draw) - wins}  ·  Captures: {h.Count(b => b.Attacker == s.HumanFaction && b.Captured)}", 950, 17, 850, 34, 18);
            }
            RefreshMap();
        }
        private void RefreshMap()
        {
            if (Controller == null || map == null) return;
            var s = Controller.Snapshot;
            bool enabled = modal == null && !rewardOpen && (s.Phase == CampaignPhase.AwaitingFactionAction && s.CurrentFaction == s.HumanFaction || s.Phase == CampaignPhase.CampaignFinished);
            map.Refresh(s, selected, hovered, enabled);
        }
        private void Modal(string title, Action onCancel = null)
        {
            CloseModal(); modal = CollectionUIElements.Panel(canvas, "ModalBackdrop"); CollectionUIElements.Stretch(modal);
            cancelModal = onCancel;
            modal.GetComponent<Image>().color = new Color(0, 0, 0, .8f);
            modalContent = CollectionUIElements.Panel(modal, title); CollectionUIElements.Place(modalContent, 485, 190, 950, 700); Skin(modalContent, true);
            Label(modalContent, title, 35, 25, 880, 58, 30); UIFocusUtility.SetOverlay(this, true); RefreshMap();
        }
        private void CloseModal()
        { UIFocusUtility.SetOverlay(this, false); if (modal != null) { modal.gameObject.SetActive(false); Destroy(modal.gameObject); } modal = modalContent = null; cancelModal = null; }
        private void CancelAttackConfirmation() { CloseModal(); RefreshMap(); }
        private void ConfirmAttack(int from, int target)
        {
            var s = Controller.Snapshot; if (!CampaignRules.CanAttack(s, s.HumanFaction, from, target)) return;
            Modal("CONFIRM ATTACK", CancelAttackConfirmation);
            Label(modalContent, s.Regions.Find(r => r.RegionId == from).Name + " → " + s.Regions.Find(r => r.RegionId == target).Name, 35, 110, 880, 90, 26);
            Button(modalContent, "Choose Deck", 35, 570, 400, 56, () => Attempt(() => { Controller.BeginAttack(from, target, true); OpenDeckSelection(); }));
            Button(modalContent, "Cancel", 490, 570, 400, 56, CancelAttackConfirmation);
        }
        private void OpenDeckSelection()
        {
            var s = Controller.Snapshot; var op = s.PendingOperation;
            if (op == null || !op.Manual || op.ResultRecorded) return;
            Modal("CAMPAIGN BATTLE", CancelDeck);
            bool attack = op.AttackerFaction == s.HumanFaction;
            string message = "Region: " + s.Regions.Find(r => r.RegionId == op.TargetRegionId).Name + "\nOperation: " + (attack ? "ATTACK" : "DEFENCE")
                + "\nAttacker: " + FactionName(op.AttackerFaction) + "  ·  Defender: " + FactionName(op.DefenderFaction);
            if (s.Phase == CampaignPhase.BattleInProgress) message += "\nInterrupted battle: restart with a fresh tactical map. Territory unchanged.";
            Label(modalContent, message, 35, 92, 880, 125, 20);
            var list = CollectionUIElements.Scroll(modalContent, "SavedDecks", 35, 232, 880, 310, true);
            list.parent.parent.GetComponent<Image>().color = new Color(0, 0, 0, .07f);
            var decks = ProgressionContext.Collection.Snapshot.savedDecks.Where(d => d.faction == s.HumanFaction).ToList();
            if (!decks.Any(d => d.deckId == selectedDeckId))
                selectedDeckId = decks.Find(d => d.deckId == op.SelectedHumanDeckId)?.deckId
                    ?? ProgressionContext.Collection.DefaultDeck(s.HumanFaction)?.deckId;
            int i = 0;
            foreach (var deck in decks)
            {
                var validation = ProgressionContext.Collection.Rules.Validate(deck, ProgressionContext.Collection.Owned);
                string value = (selectedDeckId == deck.deckId ? "Selected: " : "") + deck.name + "  ·  " + validation.Points + " / 100";
                var button = Button(list, value, 8, i * 105 + 6, 830, 42, () =>
                { selectedDeckId = deck.deckId; OpenDeckSelection(); }, 20);
                button.interactable = validation.IsValid;

                Label(list, validation.IsValid ? "Valid saved deck" : string.Join("; ", validation.Errors), 12, i * 105 + 50, 820, 52, 16); i++;
            }
            list.sizeDelta = new Vector2(list.sizeDelta.x, Math.Max(310, i * 105));
            if (decks.Count == 0) Label(modalContent, "No saved decks for this faction. Create one in Collection / Decks.", 35, 240, 880, 70, 20);
            var selectedDeck = decks.Find(d => d.deckId == selectedDeckId);
            var start = Button(modalContent, attack ? "Start Battle" : "Defend Region", 35, 584, 415, 52, () =>
            {
                if (!CampaignMatchBridge.PrepareMatch(gameConfig, selectedDeckId, out string error)) { Failure(error, OpenDeckSelection); return; }
                SceneManager.LoadScene(SceneNames.Game);
            }, 22);
            start.interactable = !op.TestAutoResolve && selectedDeck != null && ProgressionContext.Collection.Rules.Validate(selectedDeck, ProgressionContext.Collection.Owned).IsValid;
            Button(modalContent, "Cancel", 490, 584, 415, 52, CancelDeck);
            if (enableHumanTestAutoResolve || op.TestAutoResolve)
                Button(modalContent, "Test Auto Resolve (no collection rewards)", 35, 648, 870, 36,
                    () => Attempt(() => ResolveTest(selectedDeckId)), 16);
        }
        private void ResolveTest(string deckId)
        {
            var s = Controller.Snapshot; var op = s.PendingOperation;
            if (!enableHumanTestAutoResolve && !op.TestAutoResolve) throw new InvalidOperationException("Test Auto Resolve is disabled.");
            var deck = ProgressionContext.Collection.Snapshot.savedDecks.Find(d => d.deckId == deckId && d.faction == s.HumanFaction)
                ?? throw new InvalidOperationException("Test deck is no longer available.");
            var human = CampaignDeck.Saved(deck, ProgressionContext.Collection);
            var enemy = CampaignDeck.Standard(op.AttackerFaction == s.HumanFaction ? op.DefenderFaction : op.AttackerFaction, ProgressionContext.Collection.Rules);
            Controller.SelectTestDeck(deck.deckId);
            if (op.TestAutoResolve) Controller.ResolveFast(op.AttackerFaction == s.HumanFaction ? human : enemy, op.AttackerFaction == s.HumanFaction ? enemy : human, resolver);
            else Controller.ResolveHumanTest(op.AttackerFaction == s.HumanFaction ? human : enemy, op.AttackerFaction == s.HumanFaction ? enemy : human, resolver);
            CloseModal(); Recover();
        }
        private void CancelDeck()
        {
            var op = Controller?.Snapshot.PendingOperation;
            if (op == null) { CloseModal(); RefreshMap(); return; }
            if (op.ResultRecorded) return;
            // Never cancel a started match receipt; defending and interrupted battles stay pending.
            if (Controller.Snapshot.Phase == CampaignPhase.PreparingBattle && op.AttackerFaction == Controller.Snapshot.HumanFaction)
                { try { Controller.CancelHumanAttack(); } catch (Exception ex) { Failure(ex.Message, Recover); return; } }
            else defenceDeferred = true;
            CloseModal(); Render();
        }
        private void ShowResult()
        {
            var s = Controller.Snapshot; var op = s.PendingOperation;
            if (s.Phase != CampaignPhase.ShowingResult || op == null) return;
            Modal("CAMPAIGN BATTLE RESULT");
            string text = "Region: " + s.Regions.Find(r => r.RegionId == op.TargetRegionId).Name
                + "\nAttacker: " + FactionName(op.AttackerFaction) + "  ·  " + op.AttackerDeckName
                + "\nDefender: " + FactionName(op.DefenderFaction) + "  ·  " + op.DefenderDeckName;
            if (!op.Manual || op.TestAutoResolve) text += $"\nPredicted chances: {op.WinChance:P0} / {1 - op.WinChance:P0}\nDeck scores: {op.AttackerScore:F1} / {op.DefenderScore:F1}";
            text += "\n\n" + s.PendingNotification;
            Label(modalContent, text, 35, 120, 880, 390, 25);
            Button(modalContent, "Continue", 35, 580, 880, 58, () => Attempt(() => { Controller.Continue(op.OperationId); CloseModal(); selected = null; Recover(); }));
        }
        private void Failure(string error, Action retry)
        {
            Modal("CAMPAIGN ERROR"); Label(modalContent, error, 35, 120, 880, 360, 23);
            Button(modalContent, "Retry", 35, 570, 420, 56, () => { CloseModal(); retry(); });
            Button(modalContent, "Main Menu", 485, 570, 420, 56, () => SceneManager.LoadScene(SceneNames.MainMenu));
        }
        public static string FactionName(Faction f) => f == Faction.IronConcord ? "Iron Concord" : f == Faction.Ashen ? "The Ashen" : "The Vessels";
        // Campaign-only skin. Collection/deck screens keep their existing helper behaviour.
        private TMP_Text Label(Transform parent, string value, float x, float y, float w, float h, int size = 16)
        {
            var label = CollectionUIElements.Label(parent, value, x, y, w, h, size);
            label.color = PaperInk; return label;
        }
        private void Skin(RectTransform rect, bool paper, bool framed = true)
        {
            var image = rect.GetComponent<Image>(); image.sprite = paper ? paperSprite : metalSprite;
            image.color = paper ? Color.white : new Color(.82f, .82f, .82f);
            if (framed) Frame(rect);
        }
        private void Frame(RectTransform parent)
        {
            if (frameSprite == null) return;
            var rect = CollectionUIElements.Rect(parent, "MetalBorder"); CollectionUIElements.Stretch(rect);
            var image = rect.gameObject.AddComponent<Image>(); image.sprite = frameSprite;
            image.type = Image.Type.Sliced; image.fillCenter = false; image.pixelsPerUnitMultiplier = 4;
            image.raycastTarget = false;
        }
        private Button Button(Transform parent, string value, float x, float y, float w, float h, Action action, int size = 22)
        {
            var button = CollectionUIElements.Button(parent, value, x, y, w, h, action, size);
            var image = button.GetComponent<Image>(); image.sprite = actionButtonSprite; image.color = Color.white;
            var text = button.GetComponentInChildren<TMP_Text>(); text.color = PaperInk; text.fontStyle = FontStyles.Bold;
            var colors = button.colors; colors.highlightedColor = new Color(1.12f, 1.08f, .95f);
            colors.pressedColor = new Color(.75f, .68f, .57f); colors.disabledColor = new Color(.48f, .46f, .42f);
            button.colors = colors; return button;
        }
        private void EndTurnButton(Transform parent, Action action)
        {
            var button = Button(parent, "", 1688, 19, 112, 112, action, 22);
            button.gameObject.name = "EndTurn";
            // This sprite already contains the action text.
            if (endTurnSprite == null) button.GetComponentInChildren<TMP_Text>().text = "END TURN";
            var image = button.GetComponent<Image>(); image.sprite = endTurnSprite; image.preserveAspect = true;
        }
        private void Logo(Transform parent, Faction faction, float x, float y, float size)
        {
            var rect = CollectionUIElements.Rect(parent, "FactionLogo"); CollectionUIElements.Place(rect, x, y, size, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = faction == Faction.IronConcord ? ironConcordLogo : faction == Faction.Ashen ? ashenLogo : vesselsLogo;
            image.preserveAspect = true; image.raycastTarget = false;
            if (image.sprite == null) image.color = CampaignMapView.ColorFor(faction);
        }
        private static void Rule(Transform parent, float x, float y, float width)
        {
            var rect = CollectionUIElements.Panel(parent, "Divider"); CollectionUIElements.Place(rect, x, y, width, 1);
            var image = rect.GetComponent<Image>(); image.color = new Color(.27f, .20f, .12f, .55f); image.raycastTarget = false;
        }
        private void OnDestroy() { UIFocusUtility.SetOverlay(this, false); if (canvas != null) Destroy(canvas.gameObject); }
    }
}

