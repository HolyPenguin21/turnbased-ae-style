using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Game.Core;
using Game.Map;
using Game.Saves;
using Game.Turns;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI
{
    // Uses the existing transient dialog skin. Slot count belongs to the UI, never the file schema.
    internal sealed class MatchSaveSlotsUI : MonoBehaviour
    {
        private const int PageSize = 6;
        private GameConfig config;
        private GameTurnController turns;
        private MatchSaveStore store;
        private bool saving, overwriteArmed;
        private int page, selected = -1;
        private List<int> slots;
        private RectTransform rows;
        private TMP_Text status, title, actionText;
        private Button action, previous, next, add;
        private GameObject previousSelection;

        internal static void Show(GameConfig config, GameTurnController turns, bool saving)
        {
            if (config == null || UIFocusUtility.HasOverlay) return;
            if (saving && (turns == null || !turns.CanSaveMatch(out _))) return;
            var canvas = CollectionUIElements.Canvas("MatchSaveSlots");
            var owner = canvas.gameObject.AddComponent<MatchSaveSlotsUI>();
            owner.config = config; owner.turns = turns; owner.saving = saving;
            owner.store = new MatchSaveStore(Path.Combine(Application.persistentDataPath, "Matches"));
            owner.previousSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            UIFocusUtility.SetOverlay(owner, true);
            var panel = CollectionUIElements.Panel(canvas, "Slots"); CollectionUIElements.Stretch(panel);
            owner.title = CollectionUIElements.Label(panel, saving ? "SAVE MATCH" : "LOAD MATCH", 140, 70, 744, 44, 30);
            CollectionUIElements.Label(panel, saving ? "Choose a slot. Existing saves require confirmation."
                : "Choose a match to load. Current unsaved progress will be replaced.", 140, 124, 744, 44, 18);
            owner.rows = CollectionUIElements.Rect(panel, "Rows"); CollectionUIElements.Place(owner.rows, 140, 180, 744, 330);
            owner.status = CollectionUIElements.Label(panel, "", 140, 530, 744, 86, 17);
            owner.status.richText = false;
            owner.previous = CollectionUIElements.Button(panel, "Previous", 140, 625, 128, 40, () => owner.ChangePage(-1));
            owner.next = CollectionUIElements.Button(panel, "Next", 280, 625, 128, 40, () => owner.ChangePage(1));
            owner.add = CollectionUIElements.Button(panel, "New slot", 420, 625, 128, 40, owner.AddSlot);
            owner.action = CollectionUIElements.Button(panel, saving ? "Save" : "Load", 560, 625, 154, 40, owner.Execute);
            owner.actionText = owner.action.GetComponentInChildren<TMP_Text>();
            var cancel = CollectionUIElements.Button(panel, "Cancel", 726, 625, 158, 40, owner.Close);
            owner.ReloadSlots(); owner.Draw(); cancel.Select();
        }
        private void ReloadSlots()
        {
            slots = store.Slots().Union(Enumerable.Range(0, PageSize)).OrderBy(x => x).ToList();
        }
        private void ChangePage(int delta) { page += delta; selected = -1; overwriteArmed = false; Draw(); }
        private void AddSlot()
        {
            int id = slots.Count == 0 ? 0 : slots.Last();
            if (id == int.MaxValue) { status.text = "No additional numeric slot is available."; return; }
            id++; slots.Add(id); page = (slots.Count - 1) / PageSize; Select(id);
        }
        private void Select(int id)
        {
            selected = id; overwriteArmed = false; status.text = "Slot " + (id + 1L).ToString(CultureInfo.InvariantCulture) + " selected."; Draw();
        }
        private void Draw()
        {
            CollectionUIElements.Clear(rows);
            page = Mathf.Clamp(page, 0, Math.Max(0, (slots.Count - 1) / PageSize));
            for (int i = 0; i < PageSize && page * PageSize + i < slots.Count; i++)
            {
                int slot = slots[page * PageSize + i];
                string label = "Slot " + (slot + 1L).ToString(CultureInfo.InvariantCulture) + " — Empty";
                if (store.Exists(slot))
                {
                    try
                    {
                        var header = store.Load(slot, MatchSaveCodec.ReadEnvelope, out string notice);
                        label = "Slot " + (slot + 1L).ToString(CultureInfo.InvariantCulture) + " — " + header.summary;
                        if (DateTimeOffset.TryParse(header.savedUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                            label += " · " + date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                        if (notice != null) label += " [backup]";
                    }
                    catch (Exception ex) { label = "Slot " + (slot + 1L).ToString(CultureInfo.InvariantCulture) + " — " + ex.Message; }
                }
                if (slot == selected) label = "> " + label;
                var button = CollectionUIElements.Button(rows, label, 0, i * 55, 744, 48, () => Select(slot), 17);
                var text = button.GetComponentInChildren<TMP_Text>(); text.richText = false;
                text.alignment = TextAlignmentOptions.Left; text.overflowMode = TextOverflowModes.Ellipsis;
                button.interactable = saving || store.Exists(slot);
            }
            previous.interactable = page > 0; next.interactable = (page + 1) * PageSize < slots.Count;
            add.gameObject.SetActive(saving);
            action.interactable = selected >= 0 && (saving || store.Exists(selected));
            actionText.text = saving ? (overwriteArmed ? "Overwrite" : "Save") : "Load";
        }
        private void Execute()
        {
            if (selected < 0) return;
            if (saving && store.Exists(selected) && !overwriteArmed)
            {
                overwriteArmed = true;
                status.text = "Replace the existing save in slot " + (selected + 1L) + "? Press Overwrite to confirm.";
                Draw(); return;
            }
            try
            {
                if (saving)
                {
                    // Only this dialog's own input block is removed during synchronous capture.
                    // Other decisions/overlays and every gameplay condition remain checked.
                    UIFocusUtility.SetOverlay(this, false);
                    byte[] bytes;
                    try
                    {
                        bytes = MatchSaveCodec.Capture(config, UnityEngine.Object.FindAnyObjectByType<HexMap>(), turns,
                            UnityEngine.Object.FindAnyObjectByType<HexSelectionController>());
                        store.Save(selected, bytes);
                    }
                    finally { UIFocusUtility.SetOverlay(this, true); }
                    overwriteArmed = false; ReloadSlots(); Draw();
                    status.text = "Match saved to slot " + (selected + 1L) + ".";
                }
                else
                {
                    // Decode and validate everything while the current match still exists.
                    var source = store.Load(selected, bytes => MatchSaveCodec.Decode(bytes, config), out var notice);
                    if (source == null) throw new InvalidOperationException("This save slot is empty.");
                    if (!GameSession.TryPrepareRestore(source, config, out var error)) throw new InvalidOperationException(error);
                    GameSession.RestoreNotice = notice;
                    turns?.PrepareForExit();
                    SceneManager.LoadScene(SceneNames.Game);
                }
            }
            catch (Exception ex)
            {
                overwriteArmed = false; Draw(); status.text = (saving ? "Save failed: " : "Load failed: ") + ex.Message;
            }
        }
        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) Close();
        }
        private void Close() { gameObject.SetActive(false); Destroy(gameObject); }
        private void OnDisable()
        {
            UIFocusUtility.SetOverlay(this, false);
            if (EventSystem.current != null && previousSelection != null && previousSelection.activeInHierarchy)
                EventSystem.current.SetSelectedGameObject(previousSelection);
        }
    }
}
