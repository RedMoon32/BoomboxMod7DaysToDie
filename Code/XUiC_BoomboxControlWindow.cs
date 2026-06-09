using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Scripting;

namespace Boombox
{
    [Preserve]
    public class XUiC_BoomboxControlWindow : XUiController
    {
        private const int MaxSearchResults = 10;

        private static readonly object PendingSearchResultsSyncRoot = new object();
        private static PendingSearchResults pendingSearchResults;

        private readonly List<MusicSearchItem> searchResults = new List<MusicSearchItem>();
        private XUiC_TextInput searchInput;
        private XUiC_TextInput volumeInput;
        private XUiController panelMain;
        private XUiController panelSearch;
        private XUiController panelVolume;
        private XUiController searchStatusLabel;
        private readonly XUiController[] resultRows = new XUiController[MaxSearchResults];
        private readonly XUiController[] resultTitleLabels = new XUiController[MaxSearchResults];
        private readonly XUiController[] resultMetaLabels = new XUiController[MaxSearchResults];
        private readonly HashSet<string> registeredButtonIds = new HashSet<string>();
        private string searchStatus = string.Empty;
        private bool searchInputHandlersRegistered;
        private bool volumeInputHandlersRegistered;

        public static Vector3i BlockPosition { get; set; }
        public static int ClrIdx { get; set; }
        public static EntityPlayerLocal Player { get; set; }
        public static XUiC_BoomboxControlWindow ActiveWindow { get; private set; }

        public override void Init()
        {
            base.Init();
        }

        public override void OnOpen()
        {
            base.OnOpen();
            ActiveWindow = this;
            BindControls();
            RegisterHandlers();
            ShowMainPanel();
            ApplyPendingSearchResults();
        }

        public override void OnClose()
        {
            base.OnClose();
            if (ActiveWindow == this)
            {
                ActiveWindow = null;
            }

            SetSelected(searchInput, false);
            SetSelected(volumeInput, false);
        }

        public static void ClientReceiveSearchResults(string query, List<MusicSearchItem> items, string error)
        {
            var window = ActiveWindow;
            if (window != null)
            {
                window.RenderSearchResults(query, items, error);
                return;
            }

            lock (PendingSearchResultsSyncRoot)
            {
                pendingSearchResults = new PendingSearchResults(query, items, error);
            }
        }

        private void BindControls()
        {
            panelMain = GetChildById("panelMain");
            panelSearch = GetChildById("panelSearch");
            panelVolume = GetChildById("panelVolume");
            searchStatusLabel = GetChildById("lblSearchStatus");
            searchInput = GetInput("txtSearchQuery");
            volumeInput = GetInput("txtVolume");
            for (var i = 0; i < MaxSearchResults; i++)
            {
                resultRows[i] = GetChildById("searchResultRow" + i);
                resultTitleLabels[i] = GetChildById("lblResultTitle" + i);
                resultMetaLabels[i] = GetChildById("lblResultMeta" + i);
            }

            ConfigureInput(searchInput);
            ConfigureInput(volumeInput);
            RenderRows();
        }

        private void ConfigureInput(XUiC_TextInput input)
        {
            if (input == null)
            {
                return;
            }

            input.UIInput.validation = 0;
            input.characterLimit = 512;
        }

        private void RegisterHandlers()
        {
            var registered = 0;
            var missing = 0;
            var skipped = 0;

            RegisterButton("btnMainSearch", (sender, mouseButton) =>
            {
                ShowSearchPanel();
            }, ref registered, ref missing, ref skipped);
            RegisterButton("btnMainStop", (sender, mouseButton) =>
            {
                Submit(BoomboxCommandType.Stop, string.Empty, 0);
            }, ref registered, ref missing, ref skipped);
            RegisterButton("btnMainLocal", (sender, mouseButton) =>
            {
                Submit(BoomboxCommandType.ToggleBlock, string.Empty, 0);
            }, ref registered, ref missing, ref skipped);
            RegisterButton("btnMainVolume", (sender, mouseButton) =>
            {
                ShowVolumePanel();
            }, ref registered, ref missing, ref skipped);
            RegisterButton("btnMainClose", CloseButton_OnPress, ref registered, ref missing, ref skipped);

            RegisterButton("btnSearchBack", (sender, mouseButton) =>
            {
                ShowMainPanel();
            }, ref registered, ref missing, ref skipped);
            RegisterButton("btnSearchSubmit", (sender, mouseButton) =>
            {
                SubmitSearch();
            }, ref registered, ref missing, ref skipped);
            RegisterButton("btnVolumeBack", (sender, mouseButton) =>
            {
                ShowMainPanel();
            }, ref registered, ref missing, ref skipped);
            RegisterButton("btnVolumeSet", (sender, mouseButton) =>
            {
                SubmitVolume();
            }, ref registered, ref missing, ref skipped);

            for (var i = 0; i < MaxSearchResults; i++)
            {
                var number = i + 1;
                RegisterButton("btnResultPlay" + i, (sender, mouseButton) =>
                {
                    Submit(BoomboxCommandType.PlaySearchResult, string.Empty, number);
                }, ref registered, ref missing, ref skipped);
                RegisterButton("btnResultNext" + i, (sender, mouseButton) =>
                {
                    Submit(BoomboxCommandType.QueueSearchResult, string.Empty, number);
                }, ref registered, ref missing, ref skipped);
            }

            if (!searchInputHandlersRegistered && searchInput != null)
            {
                searchInput.OnSubmitHandler += SearchInput_OnSubmitHandler;
                searchInput.OnInputAbortedHandler += Input_OnInputAbortedHandler;
                searchInputHandlersRegistered = true;
            }

            if (!volumeInputHandlersRegistered && volumeInput != null)
            {
                volumeInput.OnSubmitHandler += VolumeInput_OnSubmitHandler;
                volumeInput.OnInputAbortedHandler += Input_OnInputAbortedHandler;
                volumeInputHandlersRegistered = true;
            }

            Debug.Log($"[Boombox] UI handler registration pass registered={registered} skipped={skipped} missing={missing} searchInputReady={searchInputHandlersRegistered} volumeInputReady={volumeInputHandlersRegistered}");
        }

        private void RegisterButton(string id, XUiEvent_OnPressEventHandler handler, ref int registered, ref int missing, ref int skipped)
        {
            if (registeredButtonIds.Contains(id))
            {
                skipped++;
                return;
            }

            var button = GetChildById(id);
            if (button == null)
            {
                Debug.LogWarning($"[Boombox] UI button missing: {id}");
                missing++;
                return;
            }

            var clickable = button.GetChildById("clickable");
            var lastPressFrame = -1;
            XUiEvent_OnPressEventHandler wrappedHandler = (sender, mouseButton) =>
            {
                if (lastPressFrame == Time.frameCount)
                {
                    return;
                }

                lastPressFrame = Time.frameCount;
                Debug.Log($"[Boombox] UI button clicked: {id} mouse={mouseButton}");
                handler(sender, mouseButton);
            };

            button.OnPress += wrappedHandler;

            if (clickable != null && clickable != button)
            {
                clickable.OnPress += wrappedHandler;
            }

            registeredButtonIds.Add(id);
            registered++;
            Debug.Log($"[Boombox] UI button registered: {id} target={button.GetType().Name} clickable={clickable?.GetType().Name ?? "none"}");
        }

        private void SearchInput_OnSubmitHandler(XUiController sender, string text)
        {
            SubmitSearch();
        }

        private void VolumeInput_OnSubmitHandler(XUiController sender, string text)
        {
            SubmitVolume();
        }

        private void Input_OnInputAbortedHandler(XUiController sender)
        {
            ShowMainPanel();
        }

        private void CloseButton_OnPress(XUiController sender, int mouseButton)
        {
            Close();
        }

        private void SubmitSearch()
        {
            var query = searchInput?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                ShowTooltip("Enter a search query");
                return;
            }

            searchResults.Clear();
            searchStatus = "Searching...";
            RenderRows();
            Submit(BoomboxCommandType.SearchOnline, query, 0);
        }

        private void SubmitVolume()
        {
            var valueText = volumeInput?.Text?.Trim() ?? string.Empty;
            if (!TryParseVolume(valueText, out var volume))
            {
                ShowTooltip("Enter volume 0..5 or 0..500");
                return;
            }

            Submit(BoomboxCommandType.SetVolume, string.Empty, 0, volume);
        }

        private void Submit(BoomboxCommandType type, string text, int number)
        {
            Submit(type, text, number, 0f);
        }

        private void Submit(BoomboxCommandType type, string text, int number, float value)
        {
            var request = new BoomboxCommandRequest
            {
                Type = type,
                Source = BoomboxCommandSource.Ui,
                Text = text ?? string.Empty,
                Number = number,
                Value = value,
                BlockPosition = BlockPosition,
                ClrIdx = ClrIdx
            };

            BoomboxCommandService.SendToServerOrExecuteLocal(request, Player);
        }

        private void RenderSearchResults(string query, List<MusicSearchItem> items, string error)
        {
            searchResults.Clear();
            if (items != null)
            {
                for (var i = 0; i < items.Count && i < MaxSearchResults; i++)
                {
                    searchResults.Add(items[i]);
                }
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                searchStatus = "Search failed: " + error;
            }
            else if (searchResults.Count == 0)
            {
                searchStatus = "No results for: " + (query ?? string.Empty);
            }
            else
            {
                searchStatus = $"Results for '{query}'";
            }

            ShowSearchPanel(false);
            RenderRows();
        }

        private void ApplyPendingSearchResults()
        {
            PendingSearchResults pending;
            lock (PendingSearchResultsSyncRoot)
            {
                pending = pendingSearchResults;
                pendingSearchResults = null;
            }

            if (pending != null)
            {
                RenderSearchResults(pending.Query, pending.Items, pending.Error);
            }
        }

        private void ShowMainPanel()
        {
            SetVisible(panelMain, true);
            SetVisible(panelSearch, false);
            SetVisible(panelVolume, false);
            SetSelected(searchInput, false);
            SetSelected(volumeInput, false);
        }

        private void ShowSearchPanel(bool selectInput = true)
        {
            SetVisible(panelMain, false);
            SetVisible(panelSearch, true);
            SetVisible(panelVolume, false);
            SetSelected(volumeInput, false);
            if (selectInput)
            {
                SetSelected(searchInput, true);
            }
        }

        private void ShowVolumePanel()
        {
            SetVisible(panelMain, false);
            SetVisible(panelSearch, false);
            SetVisible(panelVolume, true);
            SetSelected(searchInput, false);
            SetSelected(volumeInput, true);
        }

        private XUiC_TextInput GetInput(string id)
        {
            return GetChildById(id) as XUiC_TextInput;
        }

        private static void SetSelected(XUiC_TextInput input, bool selected)
        {
            input?.SetSelected(selected, false);
        }

        private static void SetVisible(XUiController controller, bool visible)
        {
            if (controller == null)
            {
                return;
            }

            if (TrySetBoolProperty(controller, "Visible", visible) ||
                TrySetBoolProperty(controller, "IsVisible", visible) ||
                TryInvokeSetVisible(controller, visible))
            {
                return;
            }

            var viewComponent = GetPropertyValue(controller, "ViewComponent");
            if (viewComponent == null)
            {
                return;
            }

            TrySetBoolProperty(viewComponent, "Visible", visible);
            TrySetBoolProperty(viewComponent, "IsVisible", visible);
            TryInvokeSetVisible(viewComponent, visible);
        }

        private static bool TryParseVolume(string value, out float volume)
        {
            volume = 1f;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim().Replace(',', '.');
            if (!float.TryParse(normalized, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            if (parsed > 5f && parsed <= 500f)
            {
                parsed /= 100f;
            }

            if (parsed < 0f || parsed > 5f)
            {
                return false;
            }

            volume = parsed;
            return true;
        }

        private void RenderRows()
        {
            SetControllerText(searchStatusLabel, searchStatus ?? string.Empty);

            for (var i = 0; i < MaxSearchResults; i++)
            {
                var item = i < searchResults.Count ? searchResults[i] : null;
                SetVisible(resultRows[i], item != null);
                SetControllerText(resultTitleLabels[i], item != null ? $"{i + 1}. {item.DisplayName}" : string.Empty);
                SetControllerText(resultMetaLabels[i], item?.Duration ?? string.Empty);
            }
        }

        private static void SetControllerText(XUiController controller, string text)
        {
            if (controller == null)
            {
                return;
            }

            var normalizedText = text ?? string.Empty;
            if (TrySetStringProperty(controller, "Text", normalizedText))
            {
                MarkDirty(controller);
                return;
            }

            var viewComponent = GetPropertyValue(controller, "ViewComponent");
            if (viewComponent != null)
            {
                TrySetStringProperty(viewComponent, "Text", normalizedText);
                TrySetStringProperty(viewComponent, "Value", normalizedText);
                MarkDirty(viewComponent);
            }
        }

        private static object GetPropertyValue(object target, string propertyName)
        {
            try
            {
                var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return property?.GetValue(target, null);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Boombox] Failed to read property {propertyName}: {ex.Message}");
                return null;
            }
        }

        private static bool TrySetStringProperty(object target, string propertyName, string value)
        {
            try
            {
                var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null || !property.CanWrite || property.PropertyType != typeof(string))
                {
                    return false;
                }

                property.SetValue(target, value ?? string.Empty, null);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Boombox] Failed to set property {propertyName}: {ex.Message}");
                return false;
            }
        }

        private static bool TrySetBoolProperty(object target, string propertyName, bool value)
        {
            try
            {
                var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null || !property.CanWrite || property.PropertyType != typeof(bool))
                {
                    return false;
                }

                property.SetValue(target, value, null);
                MarkDirty(target);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Boombox] Failed to set property {propertyName}: {ex.Message}");
                return false;
            }
        }

        private static bool TryInvokeSetVisible(object target, bool visible)
        {
            try
            {
                var method = target.GetType().GetMethod("SetVisible", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);
                if (method == null)
                {
                    return false;
                }

                method.Invoke(target, new object[] { visible });
                MarkDirty(target);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Boombox] Failed to call SetVisible: {ex.Message}");
                return false;
            }
        }

        private static void MarkDirty(object target)
        {
            if (target == null)
            {
                return;
            }

            TrySetBoolPropertyNoDirty(target, "IsDirty", true);
            TrySetBoolPropertyNoDirty(target, "mIsDirty", true);
            TryInvokeParameterless(target, "RefreshBindings");
            TryInvokeParameterless(target, "RefreshBindingsSelfAndChildren");
        }

        private static void TrySetBoolPropertyNoDirty(object target, string propertyName, bool value)
        {
            try
            {
                var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null || !property.CanWrite || property.PropertyType != typeof(bool))
                {
                    return;
                }

                property.SetValue(target, value, null);
            }
            catch
            {
                // Best-effort UI refresh marker.
            }
        }

        private static void TryInvokeParameterless(object target, string methodName)
        {
            try
            {
                var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                method?.Invoke(target, null);
            }
            catch
            {
                // Best-effort UI refresh hook.
            }
        }

        private void Close()
        {
            xui?.playerUI?.windowManager?.Close(WindowGroup.ID);
        }

        private static void ShowTooltip(string message)
        {
            if (Player != null)
            {
                GameManager.ShowTooltip(Player, message, string.Empty, "ui_denied", null, false, false, 0f);
            }
        }

        private sealed class PendingSearchResults
        {
            public PendingSearchResults(string query, List<MusicSearchItem> items, string error)
            {
                Query = query ?? string.Empty;
                Items = items != null ? new List<MusicSearchItem>(items) : new List<MusicSearchItem>();
                Error = error ?? string.Empty;
            }

            public string Query { get; }
            public List<MusicSearchItem> Items { get; }
            public string Error { get; }
        }
    }
}
