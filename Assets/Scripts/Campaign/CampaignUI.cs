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
        private RectTransform canvas, sidebar, footer, modal, modalContent;
        private TMP_Text heading, hoverLabel;
        private CampaignMapView map;
        private int? selected, hovered;
        private string selectedDeckId;
        private bool rewardOpen, defenceDeferred, acting;
        private readonly CampaignBattleResolver resolver = new CampaignBattleResolver();
        private CampaignController Controller => CampaignMatchBridge.Controller;
        private void Start()
        {
            canvas = CollectionUIElements.Canvas("PlanetaryCampaign", new Vector2(1920, 1080));
            var background = CollectionUIElements.Panel(canvas, "PlanetBackground"); CollectionUIElements.Stretch(background);
            background.GetComponent<Image>().color = new Color(.035f, .047f, .052f);
            heading = CollectionUIElements.Label(canvas, "PLANETARY CAMPAIGN", 48, 26, 1490, 64, 30);
            CollectionUIElements.Button(canvas, "Menu", 1715, 34, 155, 50, () => SceneManager.LoadScene(SceneNames.MainMenu));
            var surface = CollectionUIElements.Panel(canvas, "PlanetSurface"); CollectionUIElements.Place(surface, 35, 115, 1390, 700);
            surface.GetComponent<Image>().color = new Color(.065f, .073f, .071f);
            var mapRect = CollectionUIElements.Rect(surface, "Regions"); CollectionUIElements.Stretch(mapRect);
            map = mapRect.gameObject.AddComponent<CampaignMapView>();
            sidebar = CollectionUIElements.Panel(canvas, "RegionInformation"); CollectionUIElements.Place(sidebar, 1450, 115, 430, 700);
            footer = CollectionUIElements.Panel(canvas, "CampaignHistory"); CollectionUIElements.Place(footer, 35, 830, 1845, 215);
            hoverLabel = CollectionUIElements.Label(surface, "", 20, 15, 1100, 38, 18);
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
            else if (op?.Manual == true && !op.ResultRecorded && !defenceDeferred)
            {
                if (op.TestAutoResolve)
                    OpenDeckSelection();
                else OpenDeckSelection();
            }
            else if (op != null && !op.Manual && !op.ResultRecorded)
                Attempt(() => { Controller.ResolveFast(CampaignDeck.Standard(op.AttackerFaction, ProgressionContext.Collection.Rules), CampaignDeck.Standard(op.DefenderFaction, ProgressionContext.Collection.Rules), resolver); Render(); ShowResult(); });
        }
        private void Update()
        {
            bool escape = UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame;
            if (escape && modal == null && !rewardOpen && selected.HasValue) { selected = null; hovered = null; Render(); return; }
            if (escape && modal != null)
            {
                var s = Controller?.Snapshot;
                if (s?.PendingOperation != null && !s.PendingOperation.ResultRecorded) CancelDeck();
                // Results require explicit Continue; ESC never acknowledges an operation.
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
            CollectionUIElements.Label(sidebar, "TERRITORIES", 24, 24, 380, 42, 24);
            int y = 80;
            foreach (var f in s.Factions)
            {
                var label = CollectionUIElements.Label(sidebar, FactionName(f.Faction) + "  " + s.Regions.Count(r => r.OwnerFaction == f.Faction) + (f.Eliminated ? " — eliminated" : ""), 24, y, 380, 38, 20);
                label.color = Color.Lerp(CampaignMapView.ColorFor(f.Faction), Color.white, .35f); y += 43;
            }
            var region = selected.HasValue ? s.Regions.Find(r => r.RegionId == selected.Value) : null;
            CollectionUIElements.Label(sidebar, region == null ? "Select a region" : region.Name, 24, 244, 380, 65, 26);
            if (region != null)
            {
                CollectionUIElements.Label(sidebar, "Owner: " + FactionName(region.OwnerFaction), 24, 318, 380, 45, 20);
                CollectionUIElements.Label(sidebar, "Neighbors:\n" + string.Join("\n", region.NeighborIds.Select(id => s.Regions.Find(r => r.RegionId == id).Name)), 24, 378, 380, 200, 18);
                bool can = s.Regions.Where(r => r.OwnerFaction == s.HumanFaction).Any(r => CampaignRules.CanAttack(s, s.HumanFaction, r.RegionId, region.RegionId));
                if (can)
                    CollectionUIElements.Button(sidebar, "Attack " + region.Name, 24, 600, 382, 50, () =>
                    { int from = s.Regions.First(r => CampaignRules.CanAttack(s, s.HumanFaction, r.RegionId, region.RegionId)).RegionId; ConfirmAttack(from, region.RegionId); });
                else CollectionUIElements.Label(sidebar, region.OwnerFaction == s.HumanFaction ? "Select a highlighted enemy neighbor." : "Attack unavailable this turn.", 24, 602, 380, 70, 18);
            }
            string status = s.Phase == CampaignPhase.CampaignFinished ? (s.Factions.Find(f => f.Faction == s.HumanFaction).Eliminated ? "CAMPAIGN DEFEAT — your faction has been eliminated." : "PLANET CONQUERED — campaign completed.")
                : s.PendingOperation != null ? "Pending battle — " + s.Phase : s.CurrentFaction == s.HumanFaction ? "Your turn: attack an adjacent enemy region or end turn." : "AI faction is choosing an attack.";
            CollectionUIElements.Label(footer, status, 24, 15, 1500, 40, 21);
            string history = string.Join("\n", s.BattleHistory.AsEnumerable().Reverse().Take(4).Select(h => "Round " + h.Round + ": " + FactionName(h.Attacker) + " → " + h.RegionName + " · " + (h.Captured ? "captured" : "defended")));
            CollectionUIElements.Label(footer, history, 24, 60, 1410, 136, 18);
            if (s.Phase == CampaignPhase.AwaitingFactionAction && s.CurrentFaction == s.HumanFaction)
                CollectionUIElements.Button(footer, "End Turn", 1530, 120, 270, 54, () => Attempt(() => { Controller.EndTurn(); selected = null; Recover(); }));
            else if (s.PendingOperation?.Manual == true && !s.PendingOperation.ResultRecorded)
                CollectionUIElements.Button(footer, "Resume battle", 1530, 120, 270, 54, () => { defenceDeferred = false; OpenDeckSelection(); });
            if (s.Phase == CampaignPhase.CampaignFinished)
            {
                var h = s.BattleHistory.Where(b => b.Attacker == s.HumanFaction || b.Defender == s.HumanFaction).ToList();
                int wins = h.Count(b => b.Outcome != CampaignOutcome.Draw && (b.Outcome == CampaignOutcome.AttackerVictory ? b.Attacker : b.Defender) == s.HumanFaction);
                CollectionUIElements.Label(footer, $"Rounds: {s.RoundNumber}  ·  Wins: {wins}  ·  Losses: {h.Count(b => b.Outcome != CampaignOutcome.Draw) - wins}  ·  Captures: {h.Count(b => b.Attacker == s.HumanFaction && b.Captured)}", 1000, 15, 795, 72, 18);
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
        private void Modal(string title)
        {
            CloseModal(); modal = CollectionUIElements.Panel(canvas, "ModalBackdrop"); CollectionUIElements.Stretch(modal);
            modal.GetComponent<Image>().color = new Color(0, 0, 0, .8f);
            modalContent = CollectionUIElements.Panel(modal, title); CollectionUIElements.Place(modalContent, 485, 190, 950, 700);
            CollectionUIElements.Label(modalContent, title, 35, 25, 880, 58, 30); UIFocusUtility.SetOverlay(this, true); RefreshMap();
        }
        private void CloseModal()
        { UIFocusUtility.SetOverlay(this, false); if (modal != null) { modal.gameObject.SetActive(false); Destroy(modal.gameObject); } modal = modalContent = null; }
        private void ConfirmAttack(int from, int target)
        {
            var s = Controller.Snapshot; if (!CampaignRules.CanAttack(s, s.HumanFaction, from, target)) return;
            Modal("CONFIRM ATTACK");
            CollectionUIElements.Label(modalContent, s.Regions.Find(r => r.RegionId == from).Name + " → " + s.Regions.Find(r => r.RegionId == target).Name, 35, 110, 880, 90, 26);
            CollectionUIElements.Button(modalContent, "Choose Deck", 35, 570, 400, 56, () => Attempt(() => { Controller.BeginAttack(from, target, true); OpenDeckSelection(); }));
            CollectionUIElements.Button(modalContent, "Cancel", 490, 570, 400, 56, () => { CloseModal(); RefreshMap(); });
        }
        private void OpenDeckSelection()
        {
            var s = Controller.Snapshot; var op = s.PendingOperation;
            if (op == null || !op.Manual || op.ResultRecorded) return;
            Modal("CAMPAIGN BATTLE");
            bool attack = op.AttackerFaction == s.HumanFaction;
            string message = "Region: " + s.Regions.Find(r => r.RegionId == op.TargetRegionId).Name + "\nOperation: " + (attack ? "ATTACK" : "DEFENCE")
                + "\nAttacker: " + FactionName(op.AttackerFaction) + "  ·  Defender: " + FactionName(op.DefenderFaction);
            if (s.Phase == CampaignPhase.BattleInProgress) message += "\nInterrupted battle: restart with a fresh tactical map. Territory unchanged.";
            CollectionUIElements.Label(modalContent, message, 35, 92, 880, 125, 20);
            var list = CollectionUIElements.Scroll(modalContent, "SavedDecks", 35, 232, 880, 310, true);
            var decks = ProgressionContext.Collection.Snapshot.savedDecks.Where(d => d.faction == s.HumanFaction).ToList();
            if (!decks.Any(d => d.deckId == selectedDeckId))
                selectedDeckId = decks.Find(d => d.deckId == op.SelectedHumanDeckId)?.deckId
                    ?? ProgressionContext.Collection.DefaultDeck(s.HumanFaction)?.deckId;
            int i = 0;
            foreach (var deck in decks)
            {
                var validation = ProgressionContext.Collection.Rules.Validate(deck, ProgressionContext.Collection.Owned);
                string value = (selectedDeckId == deck.deckId ? "Selected: " : "") + deck.name + "  ·  " + validation.Points + " / 100";
                var button = CollectionUIElements.Button(list, value, 8, i * 105 + 6, 830, 42, () =>
                { selectedDeckId = deck.deckId; OpenDeckSelection(); }, 20);
                button.interactable = validation.IsValid;

                CollectionUIElements.Label(list, validation.IsValid ? "Valid saved deck" : string.Join("; ", validation.Errors), 12, i * 105 + 50, 820, 52, 16); i++;
            }
            list.sizeDelta = new Vector2(list.sizeDelta.x, Math.Max(310, i * 105));
            if (decks.Count == 0) CollectionUIElements.Label(modalContent, "No saved decks for this faction. Create one in Collection / Decks.", 35, 240, 880, 70, 20);
            var selectedDeck = decks.Find(d => d.deckId == selectedDeckId);
            var start = CollectionUIElements.Button(modalContent, attack ? "Start Battle" : "Defend Region", 35, 584, 415, 52, () =>
            {
                if (!CampaignMatchBridge.PrepareMatch(gameConfig, selectedDeckId, out string error)) { Failure(error, OpenDeckSelection); return; }
                SceneManager.LoadScene(SceneNames.Game);
            }, 22);
            start.interactable = !op.TestAutoResolve && selectedDeck != null && ProgressionContext.Collection.Rules.Validate(selectedDeck, ProgressionContext.Collection.Owned).IsValid;
            CollectionUIElements.Button(modalContent, "Cancel", 490, 584, 415, 52, CancelDeck);
            if (enableHumanTestAutoResolve || op.TestAutoResolve)
                CollectionUIElements.Button(modalContent, "Test Auto Resolve (no collection rewards)", 35, 648, 870, 36,
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
            CollectionUIElements.Label(modalContent, text, 35, 120, 880, 390, 25);
            CollectionUIElements.Button(modalContent, "Continue", 35, 580, 880, 58, () => Attempt(() => { Controller.Continue(op.OperationId); CloseModal(); selected = null; Recover(); }));
        }
        private void Failure(string error, Action retry)
        {
            Modal("CAMPAIGN ERROR"); CollectionUIElements.Label(modalContent, error, 35, 120, 880, 360, 23);
            CollectionUIElements.Button(modalContent, "Retry", 35, 570, 420, 56, () => { CloseModal(); retry(); });
            CollectionUIElements.Button(modalContent, "Main Menu", 485, 570, 420, 56, () => SceneManager.LoadScene(SceneNames.MainMenu));
        }
        public static string FactionName(Faction f) => f == Faction.IronConcord ? "Iron Concord" : f == Faction.Ashen ? "The Ashen" : "The Vessels";
        private void OnDestroy() { UIFocusUtility.SetOverlay(this, false); if (canvas != null) Destroy(canvas.gameObject); }
    }
}
