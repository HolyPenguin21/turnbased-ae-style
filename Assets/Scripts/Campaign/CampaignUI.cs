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
        private Material cloudMaterial, consoleMaterial, surfacePanelMaterial, regionPreviewMaterial;
        private static readonly Color PaperInk = CollectionUIElements.CollectionInk;
        private static readonly Color Cream = CollectionUIElements.CollectionText;
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
            frameSprite = Resources.Load<Sprite>("Campaign/MetalFrameRounded");
            paperSprite = Resources.Load<Sprite>("Campaign/Parchment");
            metalSprite = Resources.Load<Sprite>("Campaign/ConsoleMetalClean") ?? Resources.Load<Sprite>("Campaign/ConsoleMetal");
            var background = CollectionUIElements.Panel(canvas, "PlanetBackground"); CollectionUIElements.Stretch(background);
            Skin(background, false, false);
            // Reuse the collection's authored metal shell and plaques as one campaign console.
            var console = CollectionUIElements.Panel(canvas, "CampaignConsole"); CollectionUIElements.Place(console, 26, 18, 1868, 1040);
            CollectionSkin(console, "Panel_Outer"); console.GetComponent<Image>().raycastTarget = false;
            var cloudShader = Resources.Load<Shader>("Campaign/CampaignSurface");
            if (cloudShader != null)
            {
                consoleMaterial = new Material(cloudShader) { name = "CampaignConsole (runtime)" };
                consoleMaterial.SetFloat("_Atmosphere", 1);
                consoleMaterial.SetFloat("_EdgeFeather", 1);
                consoleMaterial.SetFloat("_CornerRadius", 32);
                consoleMaterial.SetVector("_SurfaceSize", new Vector4(1868, 1040, 0, 0));
                // A template for the map and cropped terrain masks, not the nine-sliced shell.
            }
            var header = CollectionUIElements.Panel(console, "CampaignHeader"); CollectionUIElements.Place(header, 24, 16, 1376, 70);
            CollectionSkin(header, "Plaque"); header.GetComponent<Image>().raycastTarget = false;
            heading = Label(header, "PLANETARY CAMPAIGN", 28, 8, 1320, 52, 31);
            heading.color = PaperInk; heading.fontStyle = FontStyles.Bold; heading.alignment = TextAlignmentOptions.Center;
            var menu = Button(console, "MENU", 1646, 23, 176, 48, () => SceneManager.LoadScene(SceneNames.MainMenu));
            CollectionSkin((RectTransform)menu.transform, "Plaque"); menu.GetComponentInChildren<TMP_Text>().color = PaperInk;
            var surface = CollectionUIElements.Panel(console, "PlanetSurface"); CollectionUIElements.Place(surface, 24, 100, 1376, 768);
            Skin(surface, false, false);
            surface.GetComponent<Image>().raycastTarget = false;
            if (cloudShader != null)
            {
                surfacePanelMaterial = new Material(consoleMaterial) { name = "CampaignMapPanel (runtime)" };
                surfacePanelMaterial.SetVector("_SurfaceSize", new Vector4(1376, 768, 0, 0));
                surface.GetComponent<Image>().material = surfacePanelMaterial;
                surface.gameObject.AddComponent<Mask>();
            }
            var mapRect = CollectionUIElements.Rect(surface, "Regions"); CollectionUIElements.Stretch(mapRect);
            mapRect.offsetMin = new Vector2(12, 12); mapRect.offsetMax = new Vector2(-12, -12);
            mapRect.gameObject.AddComponent<RectMask2D>();
            map = mapRect.gameObject.AddComponent<CampaignMapView>();
            // Above the terrain: dense over the background, restrained over the region footprint.
            var clouds = CollectionUIElements.Rect(surface, "AtmosphericClouds"); CollectionUIElements.Stretch(clouds);
            var cloudImage = clouds.gameObject.AddComponent<RawImage>();
            cloudImage.texture = Resources.Load<Texture2D>("Campaign/AtmosphericClouds");
            cloudImage.uvRect = new Rect(.04f, .04f, .92f, .92f);
            cloudImage.color = new Color(.90f, .87f, .81f, .94f); cloudImage.raycastTarget = false;
            cloudImage.enabled = cloudImage.texture != null && cloudShader != null;
            if (cloudImage.enabled)
            {
                cloudMaterial = new Material(cloudShader) { name = "CampaignAtmosphere (runtime)" };
                cloudMaterial.SetFloat("_Atmosphere", 1);
                cloudMaterial.SetFloat("_CloudOverlay", 1);
                cloudMaterial.SetFloat("_CloudInteriorOpacity", .18f);
                cloudMaterial.SetFloat("_CloudCornerOpacity", .72f);
                cloudMaterial.SetVector("_UVRect", new Vector4(.04f, .04f, .92f, .92f));
                cloudMaterial.SetVector("_MapViewport", new Vector4(12f / 1376, 12f / 768, 1352f / 1376, 744f / 768));
                cloudMaterial.SetVector("_SurfaceSize", new Vector4(1376, 768, 0, 0));
                cloudImage.material = cloudMaterial;
            }
            Frame(surface);
            sidebar = CollectionUIElements.Panel(console, "RegionInformation"); CollectionUIElements.Place(sidebar, 1414, 100, 430, 768);
            CollectionSkin(sidebar, "Panel_Inset"); sidebar.GetComponent<Image>().raycastTarget = false;
            footer = CollectionUIElements.Panel(console, "CampaignHistory"); CollectionUIElements.Place(footer, 24, 889, 1820, 130);
            CollectionSkin(footer, "Panel_Inset"); footer.GetComponent<Image>().raycastTarget = false;
            hoverLabel = Label(surface, "", 30, 24, 1310, 34, 19); hoverLabel.color = Cream;
            if (!CampaignMatchBridge.Load(out string error)) { Failure(error ?? "No saved campaign.", () => SceneManager.LoadScene(SceneNames.MainMenu)); return; }
            try { var state = Controller.Snapshot; map.Build(state.Regions, ClickRegion, HoverRegion, state.BackgroundId); }
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
            Plaque(sidebar, "TERRITORIES", 24, 18, 382, 46, 26);
            int y = 82;
            foreach (var f in s.Factions)
            {
                var row = CollectionUIElements.Panel(sidebar, "FactionStanding"); CollectionUIElements.Place(row, 28, y, 374, 48);
                CollectionSkin(row, "Button"); row.GetComponent<Image>().raycastTarget = false;
                Logo(row, f.Faction, 12, 6, 36);
                var factionLabel = Label(row, FactionName(f.Faction) + (f.Eliminated ? " — eliminated" : ""), 60, 8, 232, 32, 22);
                factionLabel.textWrappingMode = TextWrappingModes.NoWrap; factionLabel.overflowMode = TextOverflowModes.Ellipsis;
                var count = Label(row, s.Regions.Count(r => r.OwnerFaction == f.Faction).ToString(), 298, 8, 58, 32, 25);
                count.alignment = TextAlignmentOptions.Right; y += 55;
            }
            Rule(sidebar, 28, 253, 374);
            var region = selected.HasValue ? s.Regions.Find(r => r.RegionId == selected.Value) : null;
            Plaque(sidebar, region == null ? "SELECT A REGION" : region.Name.ToUpperInvariant(), 24, 268, 382, 62, 25);
            if (region != null)
            {
                var preview = CollectionUIElements.Rect(sidebar, "RegionTerrainPreview"); CollectionUIElements.Place(preview, 30, 344, 370, 122);
                var terrain = preview.gameObject.AddComponent<RawImage>(); terrain.texture = map.SurfaceTexture; terrain.raycastTarget = false;
                if (consoleMaterial != null)
                {
                    if (regionPreviewMaterial == null)
                    {
                        regionPreviewMaterial = new Material(consoleMaterial) { name = "CampaignRegionPreview (runtime)" };
                        regionPreviewMaterial.SetFloat("_CornerRadius", 32);
                        regionPreviewMaterial.SetVector("_SurfaceSize", new Vector4(370, 122, 0, 0));
                    }
                    terrain.material = regionPreviewMaterial;
                    preview.gameObject.AddComponent<Mask>();
                }
                var center = map.SurfaceUVAt(CampaignRegionGraphic.VisibleCenter(region));
                float previewHeight = .32f * (122f / 370f) * ((float)map.SurfaceTexture.width / map.SurfaceTexture.height);
                terrain.uvRect = new Rect(Mathf.Clamp(center.x - .16f, 0, .68f), Mathf.Clamp(center.y - previewHeight / 2, 0, 1 - previewHeight), .32f, previewHeight);
                if (regionPreviewMaterial != null)
                    regionPreviewMaterial.SetVector("_UVRect", new Vector4(terrain.uvRect.x, terrain.uvRect.y, terrain.uvRect.width, terrain.uvRect.height));
                Frame(preview);
                Label(sidebar, "OWNER", 30, 482, 88, 34, 20);
                Logo(sidebar, region.OwnerFaction, 122, 478, 36);
                Label(sidebar, FactionName(region.OwnerFaction), 170, 482, 230, 34, 23);
                Plaque(sidebar, "NEIGHBORING REGIONS", 28, 530, 374, 34, 20);
                var neighbors = CollectionUIElements.Scroll(sidebar, "Neighbors", 28, 574, 374, 106, true);
                neighbors.parent.parent.GetComponent<Image>().color = new Color(0, 0, 0, .32f);
                int row = 0;
                foreach (int id in region.NeighborIds)
                {
                    var neighbor = s.Regions.Find(r => r.RegionId == id);
                    var dot = CollectionUIElements.Panel(neighbors, "OwnerColor"); CollectionUIElements.Place(dot, 12, row * 30 + 10, 8, 8);
                    dot.GetComponent<Image>().color = CampaignMapView.ColorFor(neighbor.OwnerFaction); dot.GetComponent<Image>().raycastTarget = false;
                    var name = Label(neighbors, neighbor.Name, 30, row * 30, 308, 30, 20);
                    name.textWrappingMode = TextWrappingModes.NoWrap; name.overflowMode = TextOverflowModes.Ellipsis; row++;
                }
                neighbors.sizeDelta = new Vector2(neighbors.sizeDelta.x, Math.Max(106, row * 30));
                bool can = s.Regions.Where(r => r.OwnerFaction == s.HumanFaction).Any(r => CampaignRules.CanAttack(s, s.HumanFaction, r.RegionId, region.RegionId));
                if (can)
                    Button(sidebar, "ATTACK REGION", 28, 704, 374, 48, () =>
                    { int from = s.Regions.First(r => CampaignRules.CanAttack(s, s.HumanFaction, r.RegionId, region.RegionId)).RegionId; ConfirmAttack(from, region.RegionId); });
                else Label(sidebar, region.OwnerFaction == s.HumanFaction ? "Select a highlighted enemy neighbor." : "Attack unavailable this turn.", 30, 704, 370, 48, 19);
            }
            else
            {
                Label(sidebar, "Select a territory to see its owner and neighboring regions.\n\nSelect your territory to highlight available attacks.", 36, 356, 358, 154, 22);
                Logo(sidebar, s.HumanFaction, 143, 534, 144);
                var faction = Label(sidebar, FactionName(s.HumanFaction), 30, 694, 370, 34, 24);
                faction.alignment = TextAlignmentOptions.Center;
            }
            string status = s.Phase == CampaignPhase.CampaignFinished ? (s.Factions.Find(f => f.Faction == s.HumanFaction).Eliminated ? "CAMPAIGN DEFEAT — your faction has been eliminated." : "PLANET CONQUERED — campaign completed.")
                : s.PendingOperation != null ? "Pending battle — " + s.Phase : s.CurrentFaction == s.HumanFaction ? "Your turn: attack an adjacent enemy region or end turn." : "AI faction is choosing an attack.";
            Label(footer, status, 28, 16, s.Phase == CampaignPhase.CampaignFinished ? 880 : 1510, 32, 23).fontStyle = FontStyles.Bold;
            Rule(footer, 28, 54, 1570);
            var history = s.BattleHistory.AsEnumerable().Reverse().Take(4).ToList();
            Plaque(footer, "RECENT BATTLES", 28, 64, 205, 46, 20);
            if (history.Count == 0) Label(footer, "No battles recorded yet.", 248, 68, 650, 30, 19);
            for (int i = 0; i < history.Count; i++)
            {
                var h = history[i];
                var entry = Label(footer, "Round " + h.Round + ": " + FactionName(h.Attacker) + " → " + h.RegionName + " · "
                    + (h.Outcome == CampaignOutcome.Draw ? "draw" : h.Captured ? "captured" : "defended"),
                    248 + (i % 2) * 665, 64 + (i / 2) * 25, 650, 24, 17);
                entry.textWrappingMode = TextWrappingModes.NoWrap;
                entry.overflowMode = TextOverflowModes.Ellipsis;
            }
            if (s.Phase == CampaignPhase.AwaitingFactionAction && s.CurrentFaction == s.HumanFaction)
                EndTurnButton(footer, () => Attempt(() => { Controller.EndTurn(); selected = null; Recover(); }));
            else if (s.PendingOperation?.Manual == true && !s.PendingOperation.ResultRecorded)
                Button(footer, "RESUME BATTLE", 1604, 38, 192, 56, () => { defenceDeferred = false; OpenDeckSelection(); });
            if (s.Phase == CampaignPhase.CampaignFinished)
            {
                var h = s.BattleHistory.Where(b => b.Attacker == s.HumanFaction || b.Defender == s.HumanFaction).ToList();
                int wins = h.Count(b => b.Outcome != CampaignOutcome.Draw && (b.Outcome == CampaignOutcome.AttackerVictory ? b.Attacker : b.Defender) == s.HumanFaction);
                Label(footer, $"Rounds: {s.RoundNumber}  ·  Wins: {wins}  ·  Losses: {h.Count(b => b.Outcome != CampaignOutcome.Draw) - wins}  ·  Captures: {h.Count(b => b.Attacker == s.HumanFaction && b.Captured)}", 950, 17, 820, 34, 18);
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
            modalContent = CollectionUIElements.Panel(modal, title); CollectionUIElements.Place(modalContent, 485, 190, 950, 700);
            CollectionSkin(modalContent, "Panel_Inset");
            Plaque(modalContent, title, 35, 25, 880, 58, 30); UIFocusUtility.SetOverlay(this, true); RefreshMap();
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
            label.color = Cream; return label;
        }
        private void CollectionSkin(RectTransform rect, string asset)
        {
            var image = rect.GetComponent<Image>();
            string path = asset == "Panel_Outer" ? "Campaign/ConsoleFrame"
                : asset == "Panel_Inset" ? "Campaign/InsetFrame"
                : asset == "Plaque" ? "Campaign/SectionPlaque" : null;
            // The shared collection sprites bake opaque metal into the frame slices.
            // Campaign cutouts reveal one continuous backing through and around the rails.
            var sprite = path == null ? null : asset == "Plaque"
                ? Resources.LoadAll<Sprite>(path).FirstOrDefault() : Resources.Load<Sprite>(path);
            if (sprite == null) { CollectionUIElements.ApplySurface(image, asset); return; }
            image.sprite = sprite; image.color = Color.white; image.type = Image.Type.Sliced;
            // Keep inset rails legible at full HD instead of collapsing their detail to a few pixels.
            image.pixelsPerUnitMultiplier = asset == "Plaque" ? 8 : asset == "Panel_Outer" ? 3 : 2;
            image.fillCenter = asset == "Plaque";
        }
        private void Plaque(Transform parent, string value, float x, float y, float w, float h, int size)
        {
            var rect = CollectionUIElements.Panel(parent, "SectionPlaque"); CollectionUIElements.Place(rect, x, y, w, h);
            CollectionSkin(rect, "Plaque"); rect.GetComponent<Image>().raycastTarget = false;
            var text = Label(rect, value, 18, 4, w - 36, h - 8, size);
            text.color = PaperInk; text.fontStyle = FontStyles.Bold; text.alignment = TextAlignmentOptions.Center;
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
            CollectionUIElements.ApplyControlsSkin(button.transform);
            var text = button.GetComponentInChildren<TMP_Text>();
            text.color = actionButtonSprite != null && image.sprite == actionButtonSprite ? PaperInk : Cream; text.fontStyle = FontStyles.Bold;
            return button;
        }
        private void EndTurnButton(Transform parent, Action action)
        {
            var button = Button(parent, "", 1684, 9, 112, 112, action, 22);
            button.gameObject.name = "EndTurn";
            // This sprite already contains the action text.
            if (endTurnSprite == null) button.GetComponentInChildren<TMP_Text>().text = "END TURN";
            var image = button.GetComponent<Image>(); image.sprite = endTurnSprite; image.type = Image.Type.Simple; image.preserveAspect = true;
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
            var image = rect.GetComponent<Image>(); image.color = new Color(.60f, .43f, .23f, .65f); image.raycastTarget = false;
        }
        private void OnDestroy()
        {
            UIFocusUtility.SetOverlay(this, false);
            if (canvas != null) Destroy(canvas.gameObject);
            if (cloudMaterial != null) Destroy(cloudMaterial);
            if (consoleMaterial != null) Destroy(consoleMaterial);
            if (surfacePanelMaterial != null) Destroy(surfacePanelMaterial);
            if (regionPreviewMaterial != null) Destroy(regionPreviewMaterial);
        }
    }
}

