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
    // Runtime controller for the Campaign scene. Every panel, plaque, button and modal window is an
    // ordinary scene object (authored once by Tools/UI/Build Campaign Screen) and is edited in the
    // editor; this class only binds listeners, fills text/sprites and toggles which parts are visible.
    // Only the region meshes (CampaignMapView) and the per-row prefabs are created at run time.
    public sealed class CampaignUI : MonoBehaviour
    {
        [Serializable]
        private sealed class FactionRowRefs { public GameObject root; public Image logo, bar; public TMP_Text factionName, count; }
        [Serializable]
        private sealed class HistoryRowRefs { public GameObject root; public Image attackerLogo; public TMP_Text round, attacker, region, outcome; }

        [SerializeField] private GameConfig gameConfig;
        [SerializeField] private bool enableHumanTestAutoResolve;
        [SerializeField] private Sprite endTurnSprite, actionButtonSprite;
        [SerializeField] private Sprite ironConcordLogo, ashenLogo, vesselsLogo;

        [Header("Map panel")]
        [SerializeField] private Image surfaceImage;
        [SerializeField] private RawImage cloudImage;
        [SerializeField] private CampaignMapView map;
        [SerializeField] private TMP_Text heading, hoverLabel;
        [SerializeField] private Button menuButton;

        [Header("Header plaques (optional)")]
        [SerializeField] private TMP_Text planetNameLabel;
        [SerializeField] private TMP_Text campaignIdLabel, roundLabel, turnFactionName, turnFactionCaption;
        [SerializeField] private Image turnFactionLogo;

        [Header("Region information")]
        [SerializeField] private FactionRowRefs[] standings;
        [SerializeField] private TMP_Text sidebarTitle;
        [SerializeField] private GameObject noSelectionPanel, regionPanel;
        [SerializeField] private Image humanLogo;
        [SerializeField] private TMP_Text humanFactionName;
        [SerializeField] private RawImage terrainPreview;
        [SerializeField] private Image ownerLogo;
        [SerializeField] private TMP_Text ownerName, attackUnavailableLabel;
        [SerializeField] private RectTransform neighborsContent;
        [SerializeField] private CampaignNeighborRowView neighborRowPrefab;
        [SerializeField] private Button attackButton;
        [Header("Region information (optional)")]
        [SerializeField] private RectTransform neighborEmblemsContent;
        [SerializeField] private Image neighborEmblemPrefab;
        // When assigned, replaces the single Attack Region button with one row per legal attack.
        [SerializeField] private RectTransform attacksContent;
        [SerializeField] private CampaignAttackRowView attackRowPrefab;
        [SerializeField] private TMP_Text noAttacksLabel;

        [Header("History footer")]
        [SerializeField] private TMP_Text statusLabel, noBattlesLabel, summaryLabel;
        [SerializeField] private TMP_Text[] historyEntries;
        [SerializeField] private Button endTurnButton, resumeButton;
        [Header("History footer (optional)")]
        [SerializeField] private TMP_Text statusTitle;
        // When assigned, replaces historyEntries with column rows.
        [SerializeField] private HistoryRowRefs[] historyRows;
        [SerializeField] private Image playerEmblem;
        [SerializeField] private TMP_Text playerFactionName;

        [Header("Modal windows")]
        [SerializeField] private GameObject modalBackdrop;
        [SerializeField] private GameObject confirmWindow, deckWindow, resultWindow, errorWindow;
        [SerializeField] private TMP_Text confirmText, deckInfo, deckEmptyLabel, resultText, errorText;
        [SerializeField] private Button confirmChooseDeck, confirmCancel, deckStart, deckCancel, deckAutoResolve, resultContinue, errorRetry, errorMenu;
        [SerializeField] private RectTransform deckListContent;
        [SerializeField] private CampaignDeckRowView deckRowPrefab;

        private Material cloudMaterial, consoleMaterial, surfacePanelMaterial, regionPreviewMaterial;
        private RectTransform modalContent;
        private int? selected, hovered;
        private int confirmFrom, confirmTarget;
        private string selectedDeckId;
        private Action cancelModal, retryAction;
        private bool rewardOpen, defenceDeferred, acting, continueAfterCapture;
        private bool started;
        private CampaignController presentationOwner;
        private string presentationOperationId;
        private readonly CampaignBattleResolver resolver = new CampaignBattleResolver();
        private CampaignController Controller => CampaignMatchBridge.Controller;
        private bool ModalOpen => modalContent != null;
        private void Start()
        {
            BindStatic();
            CloseModal();
            SetupMaterials();
            if (!CampaignMatchBridge.Load(out string error)) { Failure(error ?? "No saved campaign.", () => SceneManager.LoadScene(SceneNames.MainMenu)); return; }
            map.EmblemFor = LogoFor;
            try { var state = Controller.View; map.Build(state.Regions, ClickRegion, HoverRegion, state.BackgroundId, gameConfig.moveArrowStyle); }
            catch (Exception ex) { Failure(ex.Message, () => SceneManager.LoadScene(SceneNames.Campaign)); return; }
            if (!ProgressionContext.Initialize(gameConfig)) { Failure(ProgressionContext.Error, () => SceneManager.LoadScene(SceneNames.Campaign)); return; }
            started = true; Recover();
        }
        private void BindStatic()
        {
            hoverLabel.text = "";
            menuButton.onClick.AddListener(() => SceneManager.LoadScene(SceneNames.MainMenu));
            attackButton.onClick.AddListener(AttackSelected);
            endTurnButton.onClick.AddListener(() => Attempt(() => { Controller.EndTurn(); selected = null; Recover(); }));
            resumeButton.onClick.AddListener(() => { defenceDeferred = false; OpenDeckSelection(); });
            confirmChooseDeck.onClick.AddListener(() => Attempt(() => { if (acting) return; Controller.BeginAttack(confirmFrom, confirmTarget, true); PresentConfirmedAttack(); }));
            confirmCancel.onClick.AddListener(CancelAttackConfirmation);
            deckStart.onClick.AddListener(StartBattle);
            deckCancel.onClick.AddListener(CancelDeck);
            deckAutoResolve.onClick.AddListener(() => Attempt(() => ResolveTest(selectedDeckId)));
            resultContinue.onClick.AddListener(ContinueResult);
            errorRetry.onClick.AddListener(() => { CloseModal(); retryAction?.Invoke(); });
            errorMenu.onClick.AddListener(() => SceneManager.LoadScene(SceneNames.MainMenu));
        }
        // Shader-driven surfaces get per-screen material instances; the objects themselves are scene-authored.
        private void SetupMaterials()
        {
            var cloudShader = GameAssetCatalog.Load<Shader>("Shaders/CampaignSurface");
            if (cloudShader == null) { cloudImage.enabled = false; return; }
            consoleMaterial = new Material(cloudShader) { name = "CampaignConsole (runtime)" };
            consoleMaterial.SetFloat("_Atmosphere", 1);
            consoleMaterial.SetFloat("_EdgeFeather", 1);
            consoleMaterial.SetFloat("_CornerRadius", 32);
            consoleMaterial.SetVector("_SurfaceSize", new Vector4(1868, 1040, 0, 0));
            // A template for the map and cropped terrain masks, not the nine-sliced shell.
            surfacePanelMaterial = new Material(consoleMaterial) { name = "CampaignMapPanel (runtime)" };
            var surfaceSize = surfaceImage.rectTransform.rect.size;
            surfacePanelMaterial.SetVector("_SurfaceSize", new Vector4(surfaceSize.x, surfaceSize.y, 0, 0));
            surfaceImage.material = surfacePanelMaterial;
            cloudImage.enabled = cloudImage.texture != null;
            if (!cloudImage.enabled) return;
            var uv = cloudImage.uvRect;
            cloudMaterial = new Material(cloudShader) { name = "CampaignAtmosphere (runtime)" };
            cloudMaterial.SetFloat("_Atmosphere", 1);
            cloudMaterial.SetFloat("_CloudOverlay", 1);
            cloudMaterial.SetFloat("_CloudInteriorOpacity", .08f);
            cloudMaterial.SetFloat("_CloudCornerOpacity", .72f);
            cloudMaterial.SetVector("_UVRect", new Vector4(uv.x, uv.y, uv.width, uv.height));
            cloudMaterial.SetVector("_MapViewport", new Vector4(12f / surfaceSize.x, 12f / surfaceSize.y, (surfaceSize.x - 24) / surfaceSize.x, (surfaceSize.y - 24) / surfaceSize.y));
            cloudMaterial.SetVector("_SurfaceSize", new Vector4(surfaceSize.x, surfaceSize.y, 0, 0));
            cloudImage.material = cloudMaterial;
        }
        private void Recover()
        {
            CloseModal(); rewardOpen = false;
            if (!CampaignMatchBridge.RecoverReward(gameConfig, out string error)) { Failure(error, Recover); return; }
            Render();
            var op = Controller.View.PendingOperation;
            if (op?.Manual == true && op.ResultRecorded && !op.RewardAcknowledged)
            {
                rewardOpen = true; RefreshMap();
                var rewards = gameObject.GetComponent<CollectionRewardUI>() ?? gameObject.AddComponent<CollectionRewardUI>();
                rewards.Resume(gameConfig, () => { rewardOpen = false; Recover(); });
                return;
            }
            if (Controller.View.Phase == CampaignPhase.ShowingResult) ShowResult();
            else if (op?.Manual == true && !op.ResultRecorded && !defenceDeferred) OpenDeckSelection();
            else if (op != null && !op.Manual && !op.ResultRecorded)
                Attempt(() => { Controller.ResolveFast(CampaignDeck.Standard(op.AttackerFaction, ProgressionContext.Collection.Rules, Controller.View.Factions.Find(f => f.Faction == op.AttackerFaction).ActiveDeckReference), CampaignDeck.Standard(op.DefenderFaction, ProgressionContext.Collection.Rules, Controller.View.Factions.Find(f => f.Faction == op.DefenderFaction).ActiveDeckReference), resolver); Render(); ShowResult(); });
        }
        private void Update()
        {
            if (continueAfterCapture)
            {
                if (map.IsCapturePlaying) return;
                continueAfterCapture = false; CompleteResult(); return;
            }
            if (acting)
            {
                if (map.AttackAnimationPlaying) return;
                map.CancelAttackAnimation(); acting = false;
                if (Controller == presentationOwner && Controller.View.PendingOperation?.OperationId == presentationOperationId) Attempt(Recover);
                return;
            }
            bool escape = UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame;
            if (escape && !ModalOpen && !rewardOpen && selected.HasValue) { selected = null; hovered = null; Render(); return; }
            if (escape && ModalOpen)
            {
                cancelModal?.Invoke();
                // Results require explicit Continue; ESC never acknowledges an operation.
                return;
            }
            if (Controller == null || ModalOpen || rewardOpen || acting) return;
            if (Controller.Phase != CampaignPhase.AwaitingFactionAction || !Controller.IsAiTurn) return;
            Attempt(() => { Controller.TakeAiTurn(ProgressionContext.Collection.Rules, resolver, resolveImmediately: false); selected = null; defenceDeferred = false; PresentConfirmedAttack(); });
        }
        // Uses the existing UI Update presentation gate; no timers or callbacks in strategic logic.
        // This is the only attack animation launch point, reached only after a new attack is confirmed.
        // Recover/load/resume/result rendering never launch an old operation again.
        private void PresentConfirmedAttack()
        {
            if (acting || continueAfterCapture) return;
            if (Controller.View.PendingOperation == null) { Recover(); return; }
            acting = true;
            presentationOwner = Controller; presentationOperationId = Controller.View.PendingOperation.OperationId;
            CloseModal(); selected = null; hovered = null; hoverLabel.text = ""; Render();
            map.PlayAttack(Controller.View.PendingOperation);
        }
        private void OnEnable() { if (started && Controller != null) Recover(); }
        private void OnDisable()
        {
            acting = continueAfterCapture = false; presentationOwner = null; presentationOperationId = null;
            if (map != null) { map.CancelAttackAnimation(); map.CancelCapture(); }
            UIFocusUtility.SetOverlay(this, false);
        }
        private void Attempt(Action action)
        { try { action(); } catch (Exception ex) { Failure(ex.Message, Recover); } }
        private void HoverRegion(int? id)
        {
            if (continueAfterCapture || acting) return;
            hovered = id;
            var r = id.HasValue ? Controller.View.Regions.Find(x => x.RegionId == id) : null;
            hoverLabel.text = r == null ? "" : r.Name + "  ·  " + FactionName(r.OwnerFaction);
            RefreshMap();
        }
        private void ClickRegion(int id)
        {
            if (ModalOpen || rewardOpen || acting || continueAfterCapture) return;
            var state = Controller.View;
            if (selected.HasValue && CampaignRules.CanAttack(state, state.HumanFaction, selected.Value, id)) { ConfirmAttack(selected.Value, id); return; }
            selected = id; Render();
        }
        private void AttackSelected()
        {
            var s = Controller.View;
            if (!selected.HasValue) return;
            var from = s.Regions.FirstOrDefault(r => CampaignRules.CanAttack(s, s.HumanFaction, r.RegionId, selected.Value));
            if (from != null) ConfirmAttack(from.RegionId, selected.Value);
        }
        private void Render()
        {
            if (Controller == null) return;
            var s = Controller.View;
            if (heading != null) heading.text = s.PlanetName + "  ·  Round " + s.RoundNumber + "  ·  " + FactionName(s.CurrentFaction);
            RenderHeader(s);
            for (int i = 0; i < standings.Length; i++)
            {
                var row = standings[i]; bool used = i < s.Factions.Count;
                row.root.SetActive(used);
                if (!used) continue;
                var f = s.Factions[i];
                SetLogo(row.logo, f.Faction);
                row.factionName.text = FactionName(f.Faction) + (f.Eliminated ? " — eliminated" : "");
                int owned = s.Regions.Count(r => r.OwnerFaction == f.Faction);
                row.count.text = owned.ToString();
                if (row.bar != null)
                {
                    // A Filled image uses fillAmount; any other image is stretched across its track by anchors.
                    float share = s.Regions.Count == 0 ? 0 : (float)owned / s.Regions.Count;
                    if (row.bar.type == Image.Type.Filled) row.bar.fillAmount = share;
                    else row.bar.rectTransform.anchorMax = new Vector2(row.bar.rectTransform.anchorMin.x + share * (1 - row.bar.rectTransform.anchorMin.x), row.bar.rectTransform.anchorMax.y);
                    row.bar.color = map.Highlight.MapColor(f.Faction);
                }
            }
            var region = selected.HasValue ? s.Regions.Find(r => r.RegionId == selected.Value) : null;
            sidebarTitle.text = region == null ? "SELECT A REGION" : region.Name.ToUpperInvariant();
            noSelectionPanel.SetActive(region == null); regionPanel.SetActive(region != null);
            if (region != null) RenderRegion(s, region);
            else { SetLogo(humanLogo, s.HumanFaction); humanFactionName.text = FactionName(s.HumanFaction); }
            RenderFooter(s);
            RefreshMap();
        }
        private void RenderHeader(CampaignState s)
        {
            bool finished = s.Phase == CampaignPhase.CampaignFinished;
            if (planetNameLabel != null) planetNameLabel.text = s.PlanetName.ToUpperInvariant();
            if (campaignIdLabel != null) campaignIdLabel.text = string.IsNullOrEmpty(s.CampaignId) ? "" : s.CampaignId.Substring(0, Math.Min(10, s.CampaignId.Length)).ToUpperInvariant();
            if (roundLabel != null) roundLabel.text = "ROUND " + s.RoundNumber;
            if (turnFactionLogo != null) SetLogo(turnFactionLogo, s.CurrentFaction);
            if (turnFactionName != null) turnFactionName.text = FactionName(s.CurrentFaction).ToUpperInvariant();
            if (turnFactionCaption != null) turnFactionCaption.text = finished ? "CAMPAIGN OVER" : s.CurrentFaction == s.HumanFaction ? "YOUR TURN" : "ENEMY TURN";
            if (playerEmblem != null) SetLogo(playerEmblem, s.HumanFaction);
            if (playerFactionName != null) playerFactionName.text = FactionName(s.HumanFaction).ToUpperInvariant();
        }
        private void ShowTerrain(RawImage preview, RegionState region)
        {
            preview.texture = map.SurfaceTexture;
            var center = map.SurfaceUVAt(CampaignRegionGraphic.VisibleCenter(region));
            var size = preview.rectTransform.rect.size;
            if (size.x <= 0 || size.y <= 0) return;
            float previewHeight = .32f * (size.y / size.x) * ((float)map.SurfaceTexture.width / map.SurfaceTexture.height);
            preview.uvRect = new Rect(Mathf.Clamp(center.x - .16f, 0, .68f), Mathf.Clamp(center.y - previewHeight / 2, 0, 1 - previewHeight), .32f, previewHeight);
        }
        private void RenderRegion(CampaignState s, RegionState region)
        {
            ShowTerrain(terrainPreview, region);
            var size = terrainPreview.rectTransform.rect.size;
            if (consoleMaterial != null)
            {
                if (regionPreviewMaterial == null)
                {
                    regionPreviewMaterial = new Material(consoleMaterial) { name = "CampaignRegionPreview (runtime)" };
                    regionPreviewMaterial.SetFloat("_CornerRadius", 32);
                    regionPreviewMaterial.SetVector("_SurfaceSize", new Vector4(size.x, size.y, 0, 0));
                    terrainPreview.material = regionPreviewMaterial;
                }
                var uv = terrainPreview.uvRect; var uvRect = new Vector4(uv.x, uv.y, uv.width, uv.height);
                regionPreviewMaterial.SetVector("_UVRect", uvRect);
                // The preview's Mask renders a cached stencil copy of this material; update the copy too,
                // otherwise its rounded mask keeps the first region's UV rect and crops the picture.
                if (terrainPreview.materialForRendering != regionPreviewMaterial) terrainPreview.materialForRendering.SetVector("_UVRect", uvRect);
            }
            SetLogo(ownerLogo, region.OwnerFaction); ownerName.text = FactionName(region.OwnerFaction);
            ClearChildren(neighborsContent);
            int row = 0;
            foreach (int id in region.NeighborIds)
            {
                var neighbor = s.Regions.Find(r => r.RegionId == id);
                var view = Instantiate(neighborRowPrefab, neighborsContent, false);
                ((RectTransform)view.transform).anchoredPosition = new Vector2(0, -row * 30);
                view.Dot.color = CampaignMapView.ColorFor(neighbor.OwnerFaction); view.Label.text = neighbor.Name; row++;
            }
            neighborsContent.sizeDelta = new Vector2(neighborsContent.sizeDelta.x, Math.Max(106, row * 30));
            if (neighborEmblemsContent != null && neighborEmblemPrefab != null)
            {
                ClearChildren(neighborEmblemsContent);
                foreach (int id in region.NeighborIds)
                    SetLogo(Instantiate(neighborEmblemPrefab, neighborEmblemsContent, false), s.Regions.Find(r => r.RegionId == id).OwnerFaction);
            }
            bool can = s.Regions.Where(r => r.OwnerFaction == s.HumanFaction).Any(r => CampaignRules.CanAttack(s, s.HumanFaction, r.RegionId, region.RegionId));
            string unavailable = region.OwnerFaction == s.HumanFaction ? "Select a highlighted enemy neighbor." : "Attack unavailable this turn.";
            if (attacksContent != null && attackRowPrefab != null)
            {
                attackButton.gameObject.SetActive(false); attackUnavailableLabel.gameObject.SetActive(false);
                RenderAttacks(s, region, region.OwnerFaction == s.HumanFaction ? "No attacks available from this region." : unavailable);
                return;
            }
            attackButton.gameObject.SetActive(can); attackUnavailableLabel.gameObject.SetActive(!can);
            attackUnavailableLabel.text = unavailable;
        }
        // From an own region: every legal target next to it. From an enemy region: that region, if any own region can reach it.
        private void RenderAttacks(CampaignState s, RegionState region, string emptyText)
        {
            ClearChildren(attacksContent);
            var attacks = region.OwnerFaction == s.HumanFaction
                ? region.NeighborIds.Where(id => CampaignRules.CanAttack(s, s.HumanFaction, region.RegionId, id)).Select(id => (from: region.RegionId, target: id)).ToList()
                : s.Regions.Where(r => CampaignRules.CanAttack(s, s.HumanFaction, r.RegionId, region.RegionId)).Take(1).Select(r => (from: r.RegionId, target: region.RegionId)).ToList();
            foreach (var (from, targetId) in attacks)
            {
                var target = s.Regions.Find(r => r.RegionId == targetId);
                var view = Instantiate(attackRowPrefab, attacksContent, false);
                if (view.TerrainPreview != null) ShowTerrain(view.TerrainPreview, target);
                if (view.OwnerLogo != null) SetLogo(view.OwnerLogo, target.OwnerFaction);
                if (view.RegionName != null) view.RegionName.text = target.Name;
                if (view.OwnerName != null) view.OwnerName.text = "Owner: " + FactionName(target.OwnerFaction);
                view.AttackButton.onClick.AddListener(() => { if (!ModalOpen && !rewardOpen && !acting && !continueAfterCapture) ConfirmAttack(from, targetId); });
            }
            if (noAttacksLabel != null) { noAttacksLabel.gameObject.SetActive(attacks.Count == 0); noAttacksLabel.text = emptyText; }
        }
        private void RenderFooter(CampaignState s)
        {
            bool finished = s.Phase == CampaignPhase.CampaignFinished;
            // Title/body pairs; without a separate title slot they join into the original one-line status.
            var (title, separator, body) = finished ? (s.Factions.Find(f => f.Faction == s.HumanFaction).Eliminated ? ("CAMPAIGN DEFEAT", " — ", "your faction has been eliminated.") : ("PLANET CONQUERED", " — ", "campaign completed."))
                : s.PendingOperation != null ? ("Pending battle", " — ", s.Phase.ToString()) : s.CurrentFaction == s.HumanFaction ? ("Your turn:", " ", "attack an adjacent enemy region or end turn.") : ("", "", "AI faction is choosing an attack.");
            if (statusTitle != null) { statusTitle.text = title.ToUpperInvariant(); statusLabel.text = body.ToUpperInvariant(); }
            else statusLabel.text = title + separator + body;
            bool columns = historyRows != null && historyRows.Length > 0;
            var history = s.BattleHistory.AsEnumerable().Reverse().Take(columns ? historyRows.Length : historyEntries.Length).ToList();
            noBattlesLabel.gameObject.SetActive(history.Count == 0);
            for (int i = 0; columns && i < historyRows.Length; i++)
            {
                var row = historyRows[i]; row.root.SetActive(i < history.Count);
                if (i >= history.Count) continue;
                var h = history[i];
                if (row.round != null) row.round.text = "Round " + h.Round;
                if (row.attackerLogo != null) SetLogo(row.attackerLogo, h.Attacker);
                if (row.attacker != null) row.attacker.text = FactionName(h.Attacker);
                if (row.region != null) row.region.text = h.RegionName;
                if (row.outcome != null) row.outcome.text = h.Outcome == CampaignOutcome.Draw ? "Draw" : h.Captured ? "Conquered" : "Defended";
            }
            for (int i = 0; i < historyEntries.Length; i++)
            {
                if (columns) { historyEntries[i].gameObject.SetActive(false); continue; }
                historyEntries[i].gameObject.SetActive(i < history.Count);
                if (i >= history.Count) continue;
                var h = history[i];
                historyEntries[i].text = "Round " + h.Round + ": " + FactionName(h.Attacker) + " → " + h.RegionName + " · "
                    + (h.Outcome == CampaignOutcome.Draw ? "draw" : h.Captured ? "captured" : "defended");
            }
            bool humanTurn = s.Phase == CampaignPhase.AwaitingFactionAction && s.CurrentFaction == s.HumanFaction;
            endTurnButton.gameObject.SetActive(humanTurn);
            resumeButton.gameObject.SetActive(!humanTurn && s.PendingOperation?.Manual == true && !s.PendingOperation.ResultRecorded);
            summaryLabel.gameObject.SetActive(finished);
            if (!finished) return;
            var mine = s.BattleHistory.Where(b => b.Attacker == s.HumanFaction || b.Defender == s.HumanFaction).ToList();
            int wins = mine.Count(b => b.Outcome != CampaignOutcome.Draw && (b.Outcome == CampaignOutcome.AttackerVictory ? b.Attacker : b.Defender) == s.HumanFaction);
            summaryLabel.text = $"Rounds: {s.RoundNumber}  ·  Wins: {wins}  ·  Losses: {mine.Count(b => b.Outcome != CampaignOutcome.Draw) - wins}  ·  Captures: {mine.Count(b => b.Attacker == s.HumanFaction && b.Captured)}";
        }
        private void RefreshMap()
        {
            if (Controller == null || map == null) return;
            var s = Controller.View;
            bool enabled = !ModalOpen && !rewardOpen && !acting && !continueAfterCapture && (s.Phase == CampaignPhase.AwaitingFactionAction && s.CurrentFaction == s.HumanFaction || s.Phase == CampaignPhase.CampaignFinished);
            map.Refresh(s, selected, hovered, enabled);
        }
        private void OpenModal(GameObject window, Action onCancel = null)
        {
            CloseModal();
            cancelModal = onCancel;
            modalBackdrop.SetActive(true); window.SetActive(true); modalContent = (RectTransform)window.transform;
            UIFocusUtility.SetOverlay(this, true); RefreshMap();
        }
        private void CloseModal()
        {
            UIFocusUtility.SetOverlay(this, false);
            foreach (var window in new[] { confirmWindow, deckWindow, resultWindow, errorWindow }) window.SetActive(false);
            modalBackdrop.SetActive(false); modalContent = null; cancelModal = null;
        }
        private void CancelAttackConfirmation() { CloseModal(); RefreshMap(); }
        private void ConfirmAttack(int from, int target)
        {
            var s = Controller.View; if (!CampaignRules.CanAttack(s, s.HumanFaction, from, target)) return;
            confirmFrom = from; confirmTarget = target;
            OpenModal(confirmWindow, CancelAttackConfirmation);
            confirmText.text = s.Regions.Find(r => r.RegionId == from).Name + " → " + s.Regions.Find(r => r.RegionId == target).Name;
        }
        private void OpenDeckSelection()
        {
            var s = Controller.View; var op = s.PendingOperation;
            if (op == null || !op.Manual || op.ResultRecorded) return;
            OpenModal(deckWindow, CancelDeck);
            bool attack = op.AttackerFaction == s.HumanFaction;
            string message = "Region: " + s.Regions.Find(r => r.RegionId == op.TargetRegionId).Name + "\nOperation: " + (attack ? "ATTACK" : "DEFENCE")
                + "\nAttacker: " + FactionName(op.AttackerFaction) + "  ·  Defender: " + FactionName(op.DefenderFaction);
            if (s.Phase == CampaignPhase.BattleInProgress) message += "\nInterrupted battle: restart with a fresh tactical map. Territory unchanged.";
            deckInfo.text = message;
            ClearChildren(deckListContent);
            var decks = ProgressionContext.Collection.Decks(s.HumanFaction);
            if (!decks.Any(d => d.deckId == selectedDeckId))
                selectedDeckId = decks.Find(d => d.deckId == op.SelectedHumanDeckId)?.deckId
                    ?? ProgressionContext.Collection.DefaultDeck(s.HumanFaction)?.deckId;
            int i = 0;
            foreach (var deck in decks)
            {
                var validation = ProgressionContext.Collection.Rules.Validate(deck, ProgressionContext.Collection.Owned);
                var row = Instantiate(deckRowPrefab, deckListContent, false);
                ((RectTransform)row.transform).anchoredPosition = new Vector2(0, -i * 105);
                row.ButtonLabel.text = (selectedDeckId == deck.deckId ? "Selected: " : "") + deck.name + "  ·  " + validation.Points + " / 100";
                row.Button.interactable = validation.IsValid;
                row.Button.onClick.AddListener(() => { selectedDeckId = deck.deckId; OpenDeckSelection(); });
                row.Status.text = validation.IsValid ? "Valid saved deck" : string.Join("; ", validation.Errors); i++;
            }
            deckListContent.sizeDelta = new Vector2(deckListContent.sizeDelta.x, Math.Max(310, i * 105));
            deckEmptyLabel.gameObject.SetActive(decks.Count == 0);
            var selectedDeck = decks.Find(d => d.deckId == selectedDeckId);
            deckStart.GetComponentInChildren<TMP_Text>().text = attack ? "Start Battle" : "Defend Region";
            deckStart.interactable = !op.TestAutoResolve && selectedDeck != null && ProgressionContext.Collection.Rules.Validate(selectedDeck, ProgressionContext.Collection.Owned).IsValid;
            deckAutoResolve.gameObject.SetActive(enableHumanTestAutoResolve || op.TestAutoResolve);
        }
        private void StartBattle()
        {
            if (!CampaignMatchBridge.PrepareMatch(gameConfig, selectedDeckId, out string error)) { Failure(error, OpenDeckSelection); return; }
            SceneManager.LoadScene(SceneNames.Game);
        }
        private void ResolveTest(string deckId)
        {
            var s = Controller.View; var op = s.PendingOperation;
            if (!enableHumanTestAutoResolve && !op.TestAutoResolve) throw new InvalidOperationException("Test Auto Resolve is disabled.");
            var deck = ProgressionContext.Collection.GetDeck(deckId, s.HumanFaction)
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
            var op = Controller?.View.PendingOperation;
            if (op == null) { CloseModal(); RefreshMap(); return; }
            if (op.ResultRecorded) return;
            // Never cancel a started match receipt; defending and interrupted battles stay pending.
            if (Controller.View.Phase == CampaignPhase.PreparingBattle && op.AttackerFaction == Controller.View.HumanFaction)
                { try { Controller.CancelHumanAttack(); } catch (Exception ex) { Failure(ex.Message, Recover); return; } }
            else defenceDeferred = true;
            CloseModal(); Render();
        }
        private void ShowResult()
        {
            var s = Controller.View; var op = s.PendingOperation;
            if (s.Phase != CampaignPhase.ShowingResult || op == null) return;
            OpenModal(resultWindow);
            string text = "Region: " + s.Regions.Find(r => r.RegionId == op.TargetRegionId).Name
                + "\nAttacker: " + FactionName(op.AttackerFaction) + "  ·  " + op.AttackerDeckName
                + "\nDefender: " + FactionName(op.DefenderFaction) + "  ·  " + op.DefenderDeckName;
            if (!op.Manual || op.TestAutoResolve) text += $"\nPredicted chances: {op.WinChance:P0} / {1 - op.WinChance:P0}\nDeck scores: {op.AttackerScore:F1} / {op.DefenderScore:F1}";
            resultText.text = text + "\n\n" + s.PendingNotification;
        }
        private void ContinueResult()
        {
            var op = Controller.View.PendingOperation;
            if (op == null || acting || continueAfterCapture) return;
            presentationOwner = Controller; presentationOperationId = op.OperationId;
            Attempt(() =>
            {
                // The result is acknowledged first; the existing capture presentation then precedes turn advance.
                if (CampaignMatchBridge.TryTakeOwnershipChange(out var change) && map.PlayCapture(change, Controller.View))
                {
                    continueAfterCapture = true; selected = null; hovered = null; hoverLabel.text = "";
                    CloseModal(); RefreshMap(); return;
                }
                CompleteResult();
            });
        }
        private void CompleteResult()
        {
            if (Controller != presentationOwner || Controller.View.PendingOperation?.OperationId != presentationOperationId) return;
            Attempt(() => { Controller.Continue(presentationOperationId); CloseModal(); selected = null; Recover(); });
        }
        private void Failure(string error, Action retry)
        {
            acting = continueAfterCapture = false; map?.CancelAttackAnimation(); map?.CancelCapture();
            OpenModal(errorWindow); errorText.text = error; retryAction = retry;
        }
        public static string FactionName(Faction f) => f == Faction.IronConcord ? "Iron Concord" : f == Faction.Ashen ? "The Ashen" : "The Vessels";
        private Sprite LogoFor(Faction faction) => faction == Faction.IronConcord ? ironConcordLogo : faction == Faction.Ashen ? ashenLogo : vesselsLogo;
        private void SetLogo(Image image, Faction faction)
        {
            image.sprite = LogoFor(faction);
            image.color = image.sprite == null ? CampaignMapView.ColorFor(faction) : Color.white;
        }
        private static void ClearChildren(Transform root)
        { foreach (Transform child in root) { child.gameObject.SetActive(false); Destroy(child.gameObject); } }
        private void OnDestroy()
        {
            UIFocusUtility.SetOverlay(this, false);
            if (cloudMaterial != null) Destroy(cloudMaterial);
            if (consoleMaterial != null) Destroy(consoleMaterial);
            if (surfacePanelMaterial != null) Destroy(surfacePanelMaterial);
            if (regionPreviewMaterial != null) Destroy(regionPreviewMaterial);
        }
    }
}

