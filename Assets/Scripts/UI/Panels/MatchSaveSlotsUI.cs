using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Core;
using Game.Saves;
using Game.Turns;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    // Authored once in MatchSaveSlots.prefab. Runtime code changes data and state, never layout/skin.
    public sealed class MatchSaveSlotsUI : MonoBehaviour
    {
        private const int PageSize = 6;
        [SerializeField] private TMP_Text title, description, status, actionText;
        [SerializeField] private Button[] rowButtons = new Button[0];
        [SerializeField] private TMP_Text[] rowLabels = new TMP_Text[0];
        [SerializeField] private Button action, previous, next, add, cancel, deleteButton;
        [SerializeField] private CanvasGroup contentGroup;
        [SerializeField] private GameObject deleteConfirmation;
        [SerializeField] private TMP_Text deleteMessage;
        [SerializeField] private Button confirmDeleteButton, cancelDeleteButton;
        private IMatchSaveApplication application;
        private readonly Dictionary<int,MatchSaveSlot> occupiedSlots=new Dictionary<int,MatchSaveSlot>();
        private bool saving, overwriteArmed;
        private int page, selected = -1, deleting = -1;
        private List<int> slots = new List<int>();
        private UnityAction[] rowHandlers;
        private GameObject previousSelection;
        public bool IsShowing { get; private set; }
        public event Action Closed;
        public event Action SlotsChanged;

        internal bool Show(GameConfig configuration, GameTurnController turnController, bool save)
        {
            if (IsShowing || configuration == null || UIFocusUtility.HasOverlay) return false;
            if (save && (turnController == null || !turnController.CanSaveMatch(out _))) return false;
            if (!HasBindings()) { Debug.LogError("Save/Load window has missing prefab bindings.", this); return false; }
            application=MatchSaveApplication.ForScene(configuration,turnController);saving=save;
            previousSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            page = 0; selected = deleting = -1; overwriteArmed = false; slots.Clear();
            deleteConfirmation.SetActive(false);
            contentGroup.interactable = contentGroup.blocksRaycasts = true;
            status.text = "";
            title.text = saving ? "SAVE MATCH" : "LOAD MATCH";
            description.text = saving ? "Choose a slot. Existing saves require confirmation."
                : "Choose a match to load. Current unsaved progress will be replaced.";
            IsShowing = true;
            gameObject.SetActive(true);
            UIFocusUtility.SetOverlay(this, true);
            try { ReloadSlots(); Draw(); }
            catch (Exception ex) { slots.Clear(); Draw(); status.text = "Could not read save slots: " + ex.Message; }
            cancel.Select();
            return true;
        }
        private bool HasBindings() => title != null && description != null && status != null && actionText != null
            && action != null && previous != null && next != null && add != null && cancel != null && deleteButton != null
            && contentGroup != null && deleteConfirmation != null && deleteMessage != null
            && confirmDeleteButton != null && cancelDeleteButton != null && rowButtons != null && rowLabels != null
            && rowButtons.Length == PageSize && rowLabels.Length == PageSize
            && rowButtons.All(b => b != null) && rowLabels.All(t => t != null);

        private void OnEnable()
        {
            if (!HasBindings()) return;
            previous.onClick.AddListener(PreviousPage); next.onClick.AddListener(NextPage);
            add.onClick.AddListener(AddSlot); action.onClick.AddListener(Execute); cancel.onClick.AddListener(Close);
            deleteButton.onClick.AddListener(RequestDelete); confirmDeleteButton.onClick.AddListener(ConfirmDelete);
            cancelDeleteButton.onClick.AddListener(CancelDelete);
            if (rowHandlers == null)
            {
                rowHandlers = new UnityAction[PageSize];
                for (int i = 0; i < PageSize; i++) { int row = i; rowHandlers[i] = () => SelectRow(row); }
            }
            for (int i = 0; i < PageSize; i++) rowButtons[i].onClick.AddListener(rowHandlers[i]);
        }
        private void ReloadSlots()
        {
            var snapshots=application.ListSlots();
            occupiedSlots.Clear();foreach(var slot in snapshots)occupiedSlots.Add(slot.Id,slot);
            var occupied=snapshots.Select(slot=>slot.Id);
            // Positions created in this open save window remain available across saves and
            // deletions. Re-reading disk must not discard unrelated unsaved positions.
            slots = (saving ? occupied.Union(slots).Union(Enumerable.Range(0, PageSize)) : occupied).OrderBy(x => x).ToList();
        }
        private void PreviousPage() => ChangePage(-1);
        private void NextPage() => ChangePage(1);
        private void ChangePage(int delta)
        {
            if (deleting >= 0) return;
            page += delta; selected = -1; overwriteArmed = false; status.text = ""; Draw();
        }
        private void AddSlot()
        {
            if (!saving || deleting >= 0) return;
            int id = slots.Count == 0 ? -1 : slots.Last();
            if (id == int.MaxValue) { status.text = "No additional numeric slot is available."; return; }
            id++; slots.Add(id); page = (slots.Count - 1) / PageSize; Select(id);
        }
        private void SelectRow(int row)
        {
            int index = page * PageSize + row;
            if (index < slots.Count) Select(slots[index]);
        }
        private void Select(int id)
        {
            if (deleting >= 0 || !slots.Contains(id)) return;
            selected = id; overwriteArmed = false;
            status.text = "Slot " + Number(id) + " selected."; Draw();
        }
        private static string Number(int slot) => (slot + 1L).ToString(CultureInfo.InvariantCulture);
        private void Draw()
        {
            page = Mathf.Clamp(page, 0, Math.Max(0, (slots.Count - 1) / PageSize));
            for (int i = 0; i < PageSize; i++)
            {
                int index = page * PageSize + i;
                bool visible = index < slots.Count;
                rowButtons[i].gameObject.SetActive(visible);
                if (!visible) continue;
                int slot = slots[index];
                string label = "Slot " + Number(slot) + " — Empty";
                bool occupied=occupiedSlots.TryGetValue(slot,out var snapshot) && snapshot.Occupied;
                if(occupied)
                {
                    label="Slot "+Number(slot)+" — "+(snapshot.Error??snapshot.Summary??"Empty");
                    if(snapshot.SavedUtc.HasValue)label+=" · "+snapshot.SavedUtc.Value.ToLocalTime().ToString("g",CultureInfo.CurrentCulture);
                    if(snapshot.Notice!=null)label+=" [backup]";
                }
                rowLabels[i].text = (slot == selected ? "> " : "") + label;
                rowButtons[i].interactable = saving || occupied;
            }
            previous.interactable = page > 0; next.interactable = (page + 1) * PageSize < slots.Count;
            add.gameObject.SetActive(saving);
            action.interactable = selected >= 0 && (saving || occupiedSlots.ContainsKey(selected));
            deleteButton.interactable = selected >= 0 && (occupiedSlots.ContainsKey(selected) || (saving && selected >= PageSize));
            actionText.text = saving ? (overwriteArmed ? "Overwrite" : "Save") : "Load";
            if (slots.Count == 0 && string.IsNullOrEmpty(status.text)) status.text = "No saved matches.";
        }
        private void RequestDelete()
        {
            if (!IsShowing || deleting >= 0 || selected < 0 || !deleteButton.interactable) return;
            deleting = selected; overwriteArmed = false; Draw();
            deleteMessage.text = "Delete slot " + Number(deleting) + " and its saved match? This cannot be undone.";
            contentGroup.interactable = contentGroup.blocksRaycasts = false;
            deleteConfirmation.SetActive(true); cancelDeleteButton.Select();
        }
        private void CancelDelete()
        {
            if (deleting < 0) return;
            deleting = -1; deleteConfirmation.SetActive(false);
            contentGroup.interactable = contentGroup.blocksRaycasts = true;
            deleteButton.Select();
        }
        private void ConfirmDelete()
        {
            if (!IsShowing || deleting < 0) return;
            int slot = deleting;
            CancelDelete(); selected = -1;
            try
            {
                application.Delete(slot); slots.Remove(slot); ReloadSlots(); Draw();
                status.text = "Slot " + Number(slot) + " deleted.";
                SlotsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                // A partial file-system failure is reflected from disk, never claimed as success.
                // Keep the current positions if disk cannot be inspected at all. In
                // particular, an access error must not discard unrelated new positions.
                try { ReloadSlots(); } catch { }
                Draw(); status.text = "Delete failed: " + ex.Message;
            }
            cancel.Select();
        }
        private void Execute()
        {
            if (!IsShowing || deleting >= 0 || selected < 0) return;
            if (saving && application.Exists(selected) && !overwriteArmed)
            {
                overwriteArmed = true;
                status.text = "Replace the existing save in slot " + Number(selected) + "? Press Overwrite to confirm.";
                Draw(); return;
            }
            try
            {
                if (saving)
                {
                    application.Save(selected);
                    overwriteArmed = false; ReloadSlots(); Draw();
                    status.text = "Match saved to slot " + Number(selected) + ".";
                    SlotsChanged?.Invoke();
                }
                else
                {
                    application.Load(selected);
                }
            }
            catch (Exception ex)
            {
                overwriteArmed = false; Draw(); status.text = (saving ? "Save failed: " : "Load failed: ") + ex.Message;
            }
        }
        private void Update()
        {
            if (!IsShowing || Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame) return;
            if (deleting >= 0) CancelDelete(); else Close();
        }
        public void Close() { if (IsShowing) gameObject.SetActive(false); }
        private void OnDisable()
        {
            if (HasBindings())
            {
                previous.onClick.RemoveListener(PreviousPage); next.onClick.RemoveListener(NextPage);
                add.onClick.RemoveListener(AddSlot); action.onClick.RemoveListener(Execute); cancel.onClick.RemoveListener(Close);
                deleteButton.onClick.RemoveListener(RequestDelete); confirmDeleteButton.onClick.RemoveListener(ConfirmDelete);
                cancelDeleteButton.onClick.RemoveListener(CancelDelete);
                if (rowHandlers != null) for (int i = 0; i < PageSize; i++) rowButtons[i].onClick.RemoveListener(rowHandlers[i]);
            }
            bool wasShowing = IsShowing;
            IsShowing = false; selected = deleting = -1; overwriteArmed = false;
            if (!wasShowing) return;
            UIFocusUtility.SetOverlay(this, false);
            var selection = previousSelection != null ? previousSelection.GetComponent<Selectable>() : null;
            if (selection != null && selection.isActiveAndEnabled && selection.IsInteractable()) selection.Select();
            previousSelection = null;
            Closed?.Invoke();
        }
    }
}
