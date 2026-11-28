using System;
using Game.Map;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.Players;
using Game.Progression;
using Game.UI;
using Game.Terrain;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Campaign
{
    // Runtime controller for the Campaign scene. Every panel, plaque, button and modal window is an
    // ordinary scene object, authored and edited in the
    // editor; this class only binds listeners, fills text/sprites and toggles which parts are visible.
    // Only the region meshes (CampaignMapView) and the per-row prefabs are created at run time.
    public sealed class CampaignUI : MonoBehaviour
    {
        [Serializable]
        private sealed class FactionRowRefs { public GameObject root; public Image logo, bar; public TMP_Text factionName, count; }
        [Serializable]
        private sealed class HistoryRowRefs { public GameObject root; public Image attackerLogo; public TMP_Text round, attacker, region, outcome; }
        [Serializable]
        private sealed class SupplyColumnRefs { public TMP_Text human, energy, materials, regions; }

        [SerializeField] private GameConfig gameConfig;
        [SerializeField] private bool enableHumanTestAutoResolve;
        [SerializeField] private Sprite endTurnSprite, actionButtonSprite;
        [SerializeField] private Sprite ironConcordLogo, ashenLogo, vesselsLogo;

        [Header("Map panel")]
        [SerializeField] private Image surfaceImage;
        [SerializeField] private RawImage cloudImage;
        [SerializeField] private CampaignMapView map;
        [SerializeField] private TMP_Text hoverLabel;
        [SerializeField] private Button menuButton;

        [Header("Header plaques (optional)")]
        [SerializeField] private TMP_Text planetNameLabel;
        [SerializeField] private TMP_Text campaignIdLabel, roundLabel, turnFactionName, turnFactionCaption;
        [SerializeField] private Image turnFactionLogo;

        [Header("Region information")]
        [SerializeField] private FactionRowRefs[] standings;
        [SerializeField] private TMP_Text sidebarTitle;
        [SerializeField] private GameObject noSelectionHint, regionPanel;
        [SerializeField] private RawImage terrainPreview;
        [SerializeField] private Image ownerLogo;
        [SerializeField] private TMP_Text ownerName, regionConditionsText;
        // When assigned, replaces the single Attack Region button with one row per legal attack.
        [SerializeField] private RectTransform attacksContent;
        [SerializeField] private CampaignAttackRowView attackRowPrefab;
        [SerializeField] private TMP_Text noAttacksLabel;

        [Header("History footer")]
        [SerializeField] private TMP_Text statusLabel, noBattlesLabel, summaryLabel;
        [SerializeField] private Button endTurnButton, resumeButton;
        [Header("History footer (optional)")]
        [SerializeField] private HistoryRowRefs[] historyRows;
        [SerializeField] private Image playerEmblem;
        [SerializeField] private TMP_Text playerFactionName;

        [Header("Modal windows")]
        [SerializeField] private GameObject modalBackdrop;
        [SerializeField] private GameObject confirmWindow, resultWindow, errorWindow;
        [SerializeField] private TMP_Text confirmHeading, confirmText, confirmTargetText, confirmConditionsText;
        [SerializeField] private TMP_Text confirmStartLabel, confirmDeckStatus, resultText, errorText;
        [SerializeField] private SupplyColumnRefs confirmSupplyAttacker, confirmSupplyDefender;
        [SerializeField] private ScrollRect confirmSupplyScroll;
        [SerializeField] private TMP_Dropdown confirmDeckDropdown;
        [SerializeField] private Button confirmStart, confirmCancel, confirmAutoResolve, resultContinue, errorRetry, errorMenu;

        private Material cloudMaterial, consoleMaterial, surfacePanelMaterial, regionPreviewMaterial;
        private readonly List<Material> attackPreviewMaterials = new List<Material>();
        private RectTransform modalContent;
        private int? selected, hovered;
        private int confirmFrom, confirmTarget;
        private readonly List<(string Id, bool Valid)> battleDecks = new List<(string, bool)>();
        private string selectedDeckId;
        private Action cancelModal, retryAction, afterAttackAnimation;
        private bool rewardOpen, defenceDeferred, acting, continueAfterCapture;
        private bool started;
        private CollectionRewardUI rewardPresentation;
        private CampaignController presentationOwner;
        private string presentationOperationId;
        private readonly CampaignBattleResolver resolver = new CampaignBattleResolver();
        private CampaignController Controller => CampaignMatchBridge.Controller;
        private bool ModalOpen => modalContent != null;
        private bool CanUseCampaignMap => isActiveAndEnabled && !ModalOpen && !rewardOpen && !acting
            && !continueAfterCapture && !SceneLoadCurtain.IsLoading;
        private void Start()
        {
            BindStatic();
            CloseModal();
            SetupMaterials();
            if (!CampaignMatchBridge.Load(out string error)) { Failure(error ?? "No saved campaign.", () => SceneManager.LoadScene(SceneNames.MainMenu)); return; }
            map.EmblemFor = LogoFor;
            try { var state = Controller.View; map.Build(state.CopyMapRegions(), ClickRegion, HoverRegion, state.BackgroundId); }
            catch (Exception ex)
            {
                Debug.LogWarning(ex);
                Failure("Could not open the campaign map. Try again.", () => SceneManager.LoadScene(SceneNames.Campaign));
                return;
            }
            if (!ProgressionContext.Initialize(gameConfig)) { Failure(ProgressionContext.Error, () => SceneManager.LoadScene(SceneNames.Campaign)); return; }
            started = true; Recover();
        }
        private void BindStatic()
        {
            hoverLabel.text = "";
            menuButton.onClick.AddListener(() =>
            {
                if (CanUseCampaignMap) SceneManager.LoadScene(SceneNames.MainMenu);
            });
            endTurnButton.onClick.AddListener(() =>
            {
                if (CanUseCampaignMap) Attempt(() => { Controller.EndTurn(); selected = null; Recover(); });
            });
            resumeButton.onClick.AddListener(() =>
            {
                if (!CanUseCampaignMap) return;
                defenceDeferred = false;
                Attempt(OpenPendingBattle);
            });
            confirmDeckDropdown.onValueChanged.AddListener(SelectBattleDeck);
            confirmStart.onClick.AddListener(() => ConfirmBattle(autoResolve: false));
            confirmCancel.onClick.AddListener(CancelBattleConfirmation);
            confirmAutoResolve.onClick.AddListener(() => ConfirmBattle(autoResolve: true));
            errorRetry.onClick.AddListener(() =>
            {
                if (!isActiveAndEnabled || modalContent != errorWindow.transform) return;
                var retry = retryAction;
                CloseModal();
                retry?.Invoke();
            });
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
            CloseRewardPresentation();
            CloseModal();
            var result = MatchResultOperation.Open(gameConfig, MatchResultOrigin.Campaign);
            var state = result.Advance();
            if (state.Stage == MatchResultStage.Retry) { Failure(state.Error, Recover); return; }
            Render();
            var op = Controller.View.PendingOperation;
            if (state.Stage == MatchResultStage.Reward)
            {
                rewardOpen = true; RefreshMap();
                if (rewardPresentation == null) rewardPresentation = GetComponent<CollectionRewardUI>();
                if (rewardPresentation == null) rewardPresentation = gameObject.AddComponent<CollectionRewardUI>();
                rewardPresentation.Present(gameConfig, result, () => { if (isActiveAndEnabled) Recover(); });
                return;
            }
            if (state.Stage == MatchResultStage.CampaignResult) ShowResult();
            else if (op?.Manual == true && !op.ResultRecorded && !defenceDeferred) OpenPendingBattle();
            else if (op != null && !op.Manual && !op.ResultRecorded)
                Attempt(() => { Controller.ResolvePendingAiBattle(ProgressionContext.Collection.Rules, resolver); Render(); ShowResult(); });
        }
        private void Update()
        {
            UIFocusUtility.KeepFocusWithinModal(restoreMissingSelection: ModalOpen && UIFocusUtility.IsTop(this));
            if (continueAfterCapture)
            {
                if (map.IsCapturePlaying) return;
                continueAfterCapture = false; CompleteResult(); return;
            }
            if (acting)
            {
                if (map.AttackAnimationPlaying) return;
                map.CancelAttackAnimation(); acting = false;
                var complete = afterAttackAnimation;
                afterAttackAnimation = null;
                if (Controller != null && Controller == presentationOwner
                    && Controller.View.PendingOperation?.OperationId == presentationOperationId)
                    Attempt(complete ?? Recover);
                return;
            }
            bool escape = UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame;
            if (escape && !UIFocusUtility.IsGameplayShortcutBlocked && !ModalOpen && !rewardOpen && selected.HasValue) { selected = null; hovered = null; Render(); return; }
            if (ModalOpen && UIFocusUtility.WasEscapePressed(this))
            {
                if (modalContent == confirmWindow.transform && confirmDeckDropdown.IsExpanded)
                    confirmDeckDropdown.Hide();
                else cancelModal?.Invoke();
                // Results require explicit Continue; ESC never acknowledges an operation.
                return;
            }
            if (Controller == null || !CanUseCampaignMap) return;
            if (Controller.Phase != CampaignPhase.AwaitingFactionAction || !Controller.IsAiTurn) return;
            Attempt(() => { Controller.TakeAiTurn(ProgressionContext.Collection.Rules, resolver, resolveImmediately: false); selected = null; defenceDeferred = false; PresentConfirmedAttack(); });
        }
        // Uses the existing UI Update presentation gate; no timers or callbacks in strategic logic.
        // This is the only attack animation launch point, reached only after a new attack is confirmed.
        // Recover/load/resume/result rendering never launch an old operation again.
        private void PresentConfirmedAttack(Action onComplete = null)
        {
            if (acting || continueAfterCapture) return;
            if (Controller.View.PendingOperation == null) { Recover(); return; }
            acting = true;
            afterAttackAnimation = onComplete;
            presentationOwner = Controller; presentationOperationId = Controller.View.PendingOperation.OperationId;
            CloseModal(); selected = null; hovered = null; hoverLabel.text = ""; Render();
            map.PlayAttack(Controller.View.PendingOperation);
        }
        private void CloseRewardPresentation()
        {
            if (rewardPresentation != null) rewardPresentation.ClearPresentation();
            rewardOpen = false;
        }

        private void OnEnable() { if (started && Controller != null) Recover(); }
        private void OnDisable()
        {
            CloseRewardPresentation();
            acting = continueAfterCapture = false; afterAttackAnimation = null;
            presentationOwner = null; presentationOperationId = null;
            if (map != null) { map.CancelAttackAnimation(); map.CancelCapture(); }
            UIFocusUtility.SetOverlay(this, false);
        }
        private void Attempt(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                Debug.LogWarning(ex);
                Failure(ex is InvalidOperationException ? ex.Message : "Could not complete this campaign action. Try again.", Recover);
            }
        }
        private void HoverRegion(int? id)
        {
            if (continueAfterCapture || acting) return;
            hovered = id;
            var r = id.HasValue ? Controller.View.FindRegion(id.Value) : null;
            hoverLabel.text = r == null ? "" : r.Name + "  ·  " + FactionName(r.OwnerFaction);
            RefreshMap();
        }
        private void ClickRegion(int id)
        {
            if (!CanUseCampaignMap) return;
            var state = Controller.View;
            if (selected.HasValue && state.CanAttack(state.HumanFaction, selected.Value, id)) { ConfirmAttack(selected.Value, id); return; }
            selected = id; Render();
        }
        private void Render()
        {
            if (Controller == null) return;
            var s = Controller.View;
            RenderHeader(s);
            for (int i = 0; i < standings.Length; i++)
            {
                var row = standings[i]; bool used = i < s.Factions.Count;
                row.root.SetActive(used);
                if (!used) continue;
                var f = s.Factions[i];
                SetLogo(row.logo, f.Faction);
                row.factionName.text = FactionName(f.Faction) + (f.Eliminated ? " - eliminated" : "");
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
            var region = selected.HasValue ? s.FindRegion(selected.Value) : null;
            sidebarTitle.text = region == null ? "SELECT A REGION" : region.Name.ToUpperInvariant();
            // RegionPanel stays up: without a selection it shows the title and the hint in place of the region details.
            noSelectionHint.SetActive(region == null);
            foreach (var detail in new Component[] { terrainPreview, ownerLogo, ownerName, regionConditionsText }) detail.gameObject.SetActive(region != null);
            if (region != null) RenderRegion(s, region);
            else ClearAttacks(); // the attack list sits outside RegionPanel, so drop the last region's rows
            RenderFooter(s);
            RefreshMap();
        }
        private void RenderHeader(CampaignReadModel s)
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
        private const float RegionPreviewUvPerPixel = .12f / 135f; // the original 135 px preview showed .12 of the map width
        private void ShowTerrain(RawImage preview, CampaignRegionView region, float width = .32f)
        {
            preview.texture = map.SurfaceTexture;
            var center = map.SurfaceUVAt(CampaignRegionGraphic.VisibleCenter(region.PolygonVertices));
            var size = preview.rectTransform.rect.size;
            if (size.x <= 0 || size.y <= 0) return;
            float previewHeight = width * (size.y / size.x) * ((float)map.SurfaceTexture.width / map.SurfaceTexture.height);
            preview.uvRect = new Rect(Mathf.Clamp(center.x - width / 2, 0, 1 - width), Mathf.Clamp(center.y - previewHeight / 2, 0, 1 - previewHeight), width, previewHeight);
        }
        private void RenderRegion(CampaignReadModel s, CampaignRegionView region)
        {
            var size = terrainPreview.rectTransform.rect.size;
            ShowTerrain(terrainPreview, region, size.x * RegionPreviewUvPerPixel); // fixed zoom: resizing the element reveals more map, not a bigger picture
            if (consoleMaterial != null)
            {
                if (regionPreviewMaterial == null)
                {
                    regionPreviewMaterial = new Material(consoleMaterial) { name = "CampaignRegionPreview (runtime)" };
                    regionPreviewMaterial.SetFloat("_CornerRadius", 16);
                    terrainPreview.material = regionPreviewMaterial;
                }
                regionPreviewMaterial.SetVector("_SurfaceSize", new Vector4(size.x, size.y, 0, 0));
                var uv = terrainPreview.uvRect; var uvRect = new Vector4(uv.x, uv.y, uv.width, uv.height);
                regionPreviewMaterial.SetVector("_UVRect", uvRect);
                // The preview's Mask renders a cached stencil copy of this material; update the copy too,
                // otherwise its rounded mask keeps the first region's UV rect and crops the picture.
                if (terrainPreview.materialForRendering != regionPreviewMaterial)
                {
                    terrainPreview.materialForRendering.SetVector("_UVRect", uvRect);
                    terrainPreview.materialForRendering.SetVector("_SurfaceSize", new Vector4(size.x, size.y, 0, 0));
                }
            }
            SetLogo(ownerLogo, region.OwnerFaction); ownerName.text = FactionName(region.OwnerFaction);
            regionConditionsText.text = $"Biome: {BiomeName(region.Biome)}\nSpecialization: {region.Specialization}\nRichness: {region.Richness}\n{MatchWeatherConditions.Describe(region.Weather)}";
            if (attacksContent != null && attackRowPrefab != null)
                RenderAttacks(s, region, region.OwnerFaction == s.HumanFaction ? "No attacks available from this region." : "Attack unavailable this turn.");
        }
        // From an own region: every legal target next to it. From an enemy region: that region, if any own region can reach it.
        private void RoundAttackPreview(RawImage preview)
        {
            if (consoleMaterial == null) return;
            var size = preview.rectTransform.rect.size;
            if (size.x <= 0 || size.y <= 0) return;
            var uv = preview.uvRect;
            var material = new Material(consoleMaterial) { name = "CampaignAttackPreview (runtime)" };
            material.SetFloat("_CornerRadius", 8);
            material.SetVector("_SurfaceSize", new Vector4(size.x, size.y, 0, 0));
            material.SetVector("_UVRect", new Vector4(uv.x, uv.y, uv.width, uv.height));
            preview.material = material;
            attackPreviewMaterials.Add(material);
        }
        private void ClearAttacks()
        {
            if (attacksContent != null) ClearChildren(attacksContent);
            foreach (var m in attackPreviewMaterials) if (m != null) Destroy(m);
            attackPreviewMaterials.Clear();
            if (noAttacksLabel != null) noAttacksLabel.gameObject.SetActive(false);
        }
        private void RenderAttacks(CampaignReadModel s, CampaignRegionView region, string emptyText)
        {
            ClearAttacks();
            var attacks = region.OwnerFaction == s.HumanFaction
                ? region.NeighborIds.Where(id => s.CanAttack(s.HumanFaction, region.RegionId, id)).Select(id => (from: region.RegionId, target: id)).ToList()
                : s.Regions.Where(r => s.CanAttack(s.HumanFaction, r.RegionId, region.RegionId)).Take(1).Select(r => (from: r.RegionId, target: region.RegionId)).ToList();
            foreach (var (from, targetId) in attacks)
            {
                var target = s.FindRegion(targetId);
                var view = Instantiate(attackRowPrefab, attacksContent, false);
                if (view.TerrainPreview != null) { ShowTerrain(view.TerrainPreview, target); RoundAttackPreview(view.TerrainPreview); }
                if (view.OwnerLogo != null) SetLogo(view.OwnerLogo, target.OwnerFaction);
                if (view.RegionName != null) view.RegionName.text = target.Name;
                if (view.OwnerName != null) view.OwnerName.text = FactionName(target.OwnerFaction);
                view.AttackButton.onClick.AddListener(() => { if (CanUseCampaignMap) ConfirmAttack(from, targetId); });
            }
            if (noAttacksLabel != null) { noAttacksLabel.gameObject.SetActive(attacks.Count == 0); noAttacksLabel.text = emptyText; }
        }
        private void RenderFooter(CampaignReadModel s)
        {
            bool finished = s.Phase == CampaignPhase.CampaignFinished;
            // Title/body pairs; without a separate title slot they join into the original one-line status.
            var (title, separator, body) = finished ? (s.Factions.First(f => f.Faction == s.HumanFaction).Eliminated ? ("CAMPAIGN DEFEAT", " - ", "your faction has been eliminated.") : ("PLANET CONQUERED", " - ", "campaign completed."))
                : s.PendingOperation != null ? ("Pending battle", " - ", s.Phase.ToString()) : s.CurrentFaction == s.HumanFaction ? ("Your turn:", " ", "attack an adjacent enemy region or end turn.") : ("", "", "AI faction is choosing an attack.");
            statusLabel.text = title.Length == 0 ? body.ToUpperInvariant()
                : "<color=#EE9E40>" + title.ToUpperInvariant() + "</color>" + separator + body.ToUpperInvariant();
            var rows = historyRows ?? Array.Empty<HistoryRowRefs>();
            var history = s.BattleHistory.AsEnumerable().Reverse().Take(rows.Length).ToList();
            noBattlesLabel.gameObject.SetActive(history.Count == 0);
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i]; row.root.SetActive(i < history.Count);
                if (i >= history.Count) continue;
                var h = history[i];
                if (row.round != null) row.round.text = "Round " + h.Round;
                if (row.attackerLogo != null) SetLogo(row.attackerLogo, h.Attacker);
                if (row.attacker != null) row.attacker.text = FactionName(h.Attacker);
                if (row.region != null) row.region.text = h.RegionName;
                if (row.outcome != null) row.outcome.text = h.Outcome == CampaignOutcome.Draw ? "Draw" : h.Captured ? "Conquered" : "Defended";
            }
            bool humanTurn = s.Phase == CampaignPhase.AwaitingFactionAction && s.CurrentFaction == s.HumanFaction;
            endTurnButton.gameObject.SetActive(humanTurn);
            resumeButton.gameObject.SetActive(!acting && s.PendingOperation?.Manual == true && !s.PendingOperation.ResultRecorded);
            summaryLabel.gameObject.SetActive(finished);
            if (!finished) return;
            var mine = s.BattleHistory.Where(b => b.Attacker == s.HumanFaction || b.Defender == s.HumanFaction).ToList();
            int wins = mine.Count(b => b.Outcome != CampaignOutcome.Draw && (b.Outcome == CampaignOutcome.AttackerVictory ? b.Attacker : b.Defender) == s.HumanFaction);
            summaryLabel.text = $"Rounds: {s.RoundNumber}  ·  Wins: {wins}  ·  Losses: {mine.Count(b => b.Outcome != CampaignOutcome.Draw) - wins}  ·  Captures: {mine.Count(b => b.Attacker == s.HumanFaction && b.Captured)}";
        }
        private void RefreshMap()
        {
            bool available = CanUseCampaignMap;
            SetMapControlsInteractable(available);
            if (Controller == null || map == null) return;
            var s = Controller.View;
            bool enabled = available && (s.Phase == CampaignPhase.AwaitingFactionAction && s.CurrentFaction == s.HumanFaction
                || s.Phase == CampaignPhase.CampaignFinished);
            map.Refresh(s, selected, hovered, enabled);
        }
        private void SetMapControlsInteractable(bool available)
        {
            menuButton.interactable = available;
            endTurnButton.interactable = available;
            resumeButton.interactable = available;
        }
        private void OpenModal(GameObject window, Action onCancel = null, Selectable focus = null)
        {
            CloseModal();
            cancelModal = onCancel;
            modalBackdrop.SetActive(true); window.SetActive(true); modalContent = (RectTransform)window.transform;
            UIFocusUtility.SetModal(this, window, ModalBlock.Full, focus);
            RefreshMap();
        }
        private void CloseModal()
        {
            foreach (var window in new[] { confirmWindow, resultWindow, errorWindow }) window.SetActive(false);
            modalBackdrop.SetActive(false); modalContent = null; cancelModal = null; retryAction = null;
            SetMapControlsInteractable(CanUseCampaignMap);
            UIFocusUtility.SetModal(this, null);
        }
        private void ConfirmAttack(int from, int target)
        {
            var s = Controller.View;
            if (!s.CanAttack(s.HumanFaction, from, target)) return;
            OpenBattleConfirmation(s, from, target);
        }
        private void OpenPendingBattle()
        {
            var s = Controller.View;
            var op = s.PendingOperation;
            if (op == null || !op.Manual || op.ResultRecorded) return;
            OpenBattleConfirmation(s, op.SourceRegionId, op.TargetRegionId);
        }
        private void OpenBattleConfirmation(CampaignReadModel s, int from, int target)
        {
            var op = s.PendingOperation;
            var source = s.FindRegion(from);
            var battlefield = s.FindRegion(target);
            var attacker = op?.AttackerFaction ?? source.OwnerFaction;
            var defender = op?.DefenderFaction ?? battlefield.OwnerFaction;
            var supply = CampaignAttackSupply.Calculate(s, battlefield, attacker);
            bool attack = attacker == s.HumanFaction;
            bool interrupted = s.Phase == CampaignPhase.BattleInProgress;

            confirmFrom = from;
            confirmTarget = target;
            confirmHeading.text = interrupted ? "RESUME CAMPAIGN BATTLE" : attack ? "CONFIRM ATTACK" : "DEFEND REGION";
            confirmStartLabel.text = interrupted ? "Restart Battle" : attack ? "Start Battle" : "Defend Region";
            confirmText.text = source.Name + "\n" + FactionName(attacker);
            confirmTargetText.text = battlefield.Name + "\n" + FactionName(defender);
            confirmConditionsText.text = "Battle conditions" + BattleConditions(battlefield, op?.Weather);
            ShowSupply(confirmSupplyAttacker, supply.Attacker, battlefield.RegionId);
            ShowSupply(confirmSupplyDefender, supply.Defender, battlefield.RegionId);

            confirmAutoResolve.gameObject.SetActive(s.Phase != CampaignPhase.BattleInProgress
                && (enableHumanTestAutoResolve || op?.TestAutoResolve == true));
            PopulateBattleDecks(s.HumanFaction, op?.SelectedHumanDeckId);
            OpenModal(confirmWindow, CancelBattleConfirmation, confirmStart.interactable ? confirmStart : confirmCancel);
            confirmSupplyScroll.StopMovement();
            confirmSupplyScroll.verticalNormalizedPosition = 1;
        }
        private static void ShowSupply(SupplyColumnRefs column, CampaignAttackSupply.Side side, int battlefieldId)
        {
            string Amount(int amount, ResourceType type) => (amount == 0 ? "0 " : "+" + amount + " ") + type;
            column.human.text = Amount(side.Total.human, ResourceType.Human);
            column.energy.text = Amount(side.Total.energy, ResourceType.Energy);
            column.materials.text = Amount(side.Total.materials, ResourceType.Materials);
            column.regions.text = side.Sources.Count == 0 ? "No supplying regions" : string.Join("\n", side.Sources.Select(source =>
                source.Region.Name + (source.Region.RegionId == battlefieldId ? " (Battlefield)" : "") + " · " + CampaignAttackSupply.Describe(source.Bonus)));
        }
        private void PopulateBattleDecks(Faction faction, string savedDeckId)
        {
            var collection = ProgressionContext.Collection;
            var labels = new List<string>();
            battleDecks.Clear();
            foreach (var deck in collection.Decks(faction))
            {
                var validation = collection.Rules.Validate(deck, collection.Owned);
                battleDecks.Add((deck.deckId, validation.IsValid));
                labels.Add($"{deck.name}  ·  {validation.Points} / {DeckRules.MaximumPoints}" + (validation.IsValid ? "" : "  ·  Invalid"));
            }

            int index = battleDecks.FindIndex(d => d.Id == (selectedDeckId ?? savedDeckId) && d.Valid);
            if (index < 0 && savedDeckId != null)
                index = battleDecks.FindIndex(d => d.Id == savedDeckId && d.Valid);
            if (index < 0)
            {
                var defaultDeckId = collection.DefaultDeck(faction)?.deckId;
                index = battleDecks.FindIndex(d => d.Id == defaultDeckId && d.Valid);
            }
            if (index < 0) index = battleDecks.FindIndex(d => d.Valid);
            if (labels.Count == 0) labels.Add("No saved decks");

            confirmDeckDropdown.ClearOptions();
            confirmDeckDropdown.AddOptions(labels);
            confirmDeckDropdown.interactable = battleDecks.Count > 0;
            confirmDeckDropdown.SetValueWithoutNotify(Mathf.Max(0, index));
            SelectBattleDeck(confirmDeckDropdown.value);
        }
        private void SelectBattleDeck(int index)
        {
            bool valid = index >= 0 && index < battleDecks.Count && battleDecks[index].Valid;
            selectedDeckId = valid ? battleDecks[index].Id : null;
            confirmStart.interactable = valid && Controller.View.PendingOperation?.TestAutoResolve != true;
            confirmAutoResolve.interactable = valid;
            confirmDeckStatus.text = battleDecks.Count == 0 ? "Create a deck for this faction in Collection & Decks."
                : !valid ? "This deck needs changes. Open Collection & Decks."
                : Controller.Phase == CampaignPhase.BattleInProgress ? "Restarting this battle generates a fresh tactical map." : "";
            confirmDeckStatus.gameObject.SetActive(confirmDeckStatus.text.Length > 0);
        }
        private void ConfirmBattle(bool autoResolve)
        {
            var button = autoResolve ? confirmAutoResolve : confirmStart;
            if (acting || SceneLoadCurtain.IsLoading || modalContent != confirmWindow.transform || !UIFocusUtility.IsTop(this)
                || !button.isActiveAndEnabled || !button.IsInteractable() || selectedDeckId == null) return;

            string deckId = selectedDeckId;
            Action launch = autoResolve ? () => ResolveTest(deckId) : () => StartBattle(deckId);
            Attempt(() =>
            {
                if (Controller.View.PendingOperation != null)
                {
                    launch();
                    return;
                }
                Controller.BeginAttack(confirmFrom, confirmTarget, manual: true, humanDeckId: deckId);
                PresentConfirmedAttack(launch);
            });
        }
        private static string BiomeName(Biome biome) => biome == Biome.Arid ? "Arid badlands" : "Desert";
        // The player-facing battle conditions: the target region's properties and its weather (a
        // pending operation passes its own weather snapshot). No generator internals here.
        private static string BattleConditions(CampaignRegionView target, MatchWeatherProfile? weather = null) =>
            $"\n{BiomeName(target.Biome)} · {target.Specialization} · {target.Richness}\n{MatchWeatherConditions.Describe(weather ?? target.Weather)}";
        private void StartBattle(string deckId)
        {
            if (SceneLoadCurtain.IsLoading) return;
            if (!CampaignMatchBridge.PrepareMatch(gameConfig, deckId, out string error)) { Failure(error, OpenPendingBattle); return; }
            SceneLoadCurtain.LoadScene(SceneNames.Game, gameConfig.PickLoadingScreenImage());
            CloseModal();
            RefreshMap();
        }
        private void ResolveTest(string deckId)
        {
            var s = Controller.View; var op = s.PendingOperation;
            if (!enableHumanTestAutoResolve && !op.TestAutoResolve) throw new InvalidOperationException("Test Auto Resolve is disabled.");
            var deck = ProgressionContext.Collection.GetDeck(deckId, s.HumanFaction)
                ?? throw new InvalidOperationException("Test deck is no longer available.");
            var human = CampaignDeck.Saved(deck, ProgressionContext.Collection);
            var enemy = CampaignDeck.Ai(op.AttackerFaction == s.HumanFaction ? op.DefenderFaction : op.AttackerFaction, ProgressionContext.Collection.Rules, op.RandomSeed, op.AttackerFaction == s.HumanFaction ? op.DefenderDeckName : op.AttackerDeckName);
            Controller.SelectTestDeck(deck.deckId);
            if (op.TestAutoResolve) Controller.ResolveFast(op.AttackerFaction == s.HumanFaction ? human : enemy, op.AttackerFaction == s.HumanFaction ? enemy : human, resolver);
            else Controller.ResolveHumanTest(op.AttackerFaction == s.HumanFaction ? human : enemy, op.AttackerFaction == s.HumanFaction ? enemy : human, resolver);
            CloseModal(); Recover();
        }
        private void CancelBattleConfirmation()
        {
            var s = Controller.View;
            var op = s.PendingOperation;
            if (op == null) { CloseModal(); RefreshMap(); return; }
            if (op.ResultRecorded) return;
            // Defence and tactical matches already registered with the campaign stay pending.
            if (s.Phase == CampaignPhase.PreparingBattle && op.AttackerFaction == s.HumanFaction)
            {
                try { Controller.CancelHumanAttack(); }
                catch (Exception ex)
                {
                    Debug.LogWarning(ex);
                    Failure("Could not cancel this attack. Try again.", Recover);
                    return;
                }
            }
            else defenceDeferred = true;
            CloseModal(); Render();
        }
        private void ShowResult()
        {
            var s = Controller.View; var op = s.PendingOperation;
            if (s.Phase != CampaignPhase.ShowingResult || op == null) return;
            var owner = Controller;
            resultContinue.onClick.RemoveAllListeners();
            resultContinue.onClick.AddListener(() => ContinueResult(owner, op.OperationId));
            OpenModal(resultWindow);
            string text = "Region: " + s.FindRegion(op.TargetRegionId).Name
                + "\nAttacker: " + FactionName(op.AttackerFaction) + "  ·  " + op.AttackerDeckName
                + "\nDefender: " + FactionName(op.DefenderFaction) + "  ·  " + op.DefenderDeckName;
            if (!op.Manual || op.TestAutoResolve) text += $"\nPredicted chances: {op.WinChance:P0} / {1 - op.WinChance:P0}\nDeck scores: {op.AttackerScore:F1} / {op.DefenderScore:F1}";
            resultText.text = text + "\n\n" + s.PendingNotification;
        }
        private void ContinueResult(CampaignController owner, string operationId)
        {
            if (!isActiveAndEnabled || acting || continueAfterCapture || modalContent != resultWindow.transform
                || Controller != owner || owner.View.PendingOperation?.OperationId != operationId) return;
            presentationOwner = owner;
            presentationOperationId = operationId;
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
            var controller = Controller;
            if (!isActiveAndEnabled || controller == null || controller != presentationOwner
                || controller.View.PendingOperation?.OperationId != presentationOperationId) return;
            var result = MatchResultOperation.Open(gameConfig, MatchResultOrigin.Campaign);
            if (!result.ContinueCampaign(presentationOperationId, out string error)) { Failure(error, CompleteResult); return; }
            CloseModal(); selected = null; Recover();
        }
        private void Failure(string error, Action retry)
        {
            acting = continueAfterCapture = false; afterAttackAnimation = null;
            map?.CancelAttackAnimation(); map?.CancelCapture();
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
            foreach (var m in attackPreviewMaterials) if (m != null) Destroy(m);
            if (cloudMaterial != null) Destroy(cloudMaterial);
            if (consoleMaterial != null) Destroy(consoleMaterial);
            if (surfacePanelMaterial != null) Destroy(surfacePanelMaterial);
            if (regionPreviewMaterial != null) Destroy(regionPreviewMaterial);
        }
    }
}

