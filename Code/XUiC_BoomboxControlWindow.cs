using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Scripting;

namespace Boombox
{
    [Preserve]
    public class XUiC_BoomboxControlWindow : XUiController
    {
        private XUiC_TextInput queryInput;
        private XUiC_TextInput numberInput;
        private XUiC_TextInput volumeInput;
        private XUiC_TextInput preDelayInput;
        private bool handlersRegistered;

        public static Vector3i BlockPosition { get; set; }
        public static int ClrIdx { get; set; }
        public static EntityPlayerLocal Player { get; set; }

        public override void Init()
        {
            base.Init();
            BindInputs();
            RegisterHandlers();
        }

        public override void OnOpen()
        {
            base.OnOpen();
            BindInputs();
            RegisterHandlers();

            if (queryInput != null)
            {
                queryInput.SetSelected(true, true);
            }
        }

        public override void OnClose()
        {
            base.OnClose();
            SetSelected(queryInput, false);
            SetSelected(numberInput, false);
            SetSelected(volumeInput, false);
            SetSelected(preDelayInput, false);
        }

        private void BindInputs()
        {
            queryInput = GetInput("txtBoomboxQuery");
            numberInput = GetInput("txtBoomboxNumber");
            volumeInput = GetInput("txtBoomboxVolume");
            preDelayInput = GetInput("txtBoomboxPreDelay");

            if (queryInput != null)
            {
                queryInput.UIInput.validation = 0;
                queryInput.characterLimit = 512;
            }

            if (numberInput != null)
            {
                numberInput.UIInput.validation = 0;
                numberInput.characterLimit = 4;
            }

            if (volumeInput != null)
            {
                volumeInput.UIInput.validation = 0;
                volumeInput.characterLimit = 8;
                if (string.IsNullOrWhiteSpace(volumeInput.Text))
                {
                    volumeInput.Text = "1";
                }
            }

            if (preDelayInput != null)
            {
                preDelayInput.UIInput.validation = 0;
                preDelayInput.characterLimit = 8;
                if (string.IsNullOrWhiteSpace(preDelayInput.Text))
                {
                    preDelayInput.Text = "2";
                }
            }
        }

        private void RegisterHandlers()
        {
            if (handlersRegistered)
            {
                return;
            }

            RegisterButton("btnBoomboxPlayLocal", BoomboxCommandType.PlayLocal);
            RegisterButton("btnBoomboxPlayOnline", BoomboxCommandType.PlayOnline);
            RegisterButton("btnBoomboxSearch", BoomboxCommandType.SearchOnline);
            RegisterButton("btnBoomboxPlayNumber", BoomboxCommandType.PlaySearchResult);
            RegisterButton("btnBoomboxQueue", BoomboxCommandType.QueueOnline);
            RegisterButton("btnBoomboxQueueNumber", BoomboxCommandType.QueueSearchResult);
            RegisterButton("btnBoomboxVolume", BoomboxCommandType.SetVolume);
            RegisterButton("btnBoomboxPreDelay", BoomboxCommandType.SetPreDelay);
            RegisterButton("btnBoomboxToggle", BoomboxCommandType.ToggleBlock);
            RegisterButton("btnBoomboxPickup", BoomboxCommandType.PickupBlock);
            RegisterButton("btnBoomboxClearQueue", BoomboxCommandType.ClearQueue);

            var closeButton = GetChildById("btnBoomboxClose");
            if (closeButton != null)
            {
                closeButton.OnPress += CloseButton_OnPress;
            }

            if (queryInput != null)
            {
                queryInput.OnSubmitHandler += QueryInput_OnSubmitHandler;
                queryInput.OnInputAbortedHandler += Input_OnInputAbortedHandler;
            }

            handlersRegistered = true;
        }

        private void RegisterButton(string id, BoomboxCommandType type)
        {
            var button = GetChildById(id);
            if (button == null)
            {
                Debug.LogWarning($"[Boombox] UI button missing: {id}");
                return;
            }

            button.OnPress += (sender, mouseButton) =>
            {
                if (mouseButton == 0)
                {
                    Submit(type);
                }
            };
        }

        private void QueryInput_OnSubmitHandler(XUiController sender, string text)
        {
            Submit(BoomboxCommandType.PlayOnline);
        }

        private void Input_OnInputAbortedHandler(XUiController sender)
        {
            Close();
        }

        private void CloseButton_OnPress(XUiController sender, int mouseButton)
        {
            if (mouseButton == 0)
            {
                Close();
            }
        }

        private void Submit(BoomboxCommandType type)
        {
            var request = new BoomboxCommandRequest
            {
                Type = type,
                Source = BoomboxCommandSource.Ui,
                Text = queryInput?.Text?.Trim() ?? string.Empty,
                Number = ParseInt(numberInput?.Text),
                Value = ParseFloat(type == BoomboxCommandType.SetPreDelay ? preDelayInput?.Text : volumeInput?.Text),
                BlockPosition = BlockPosition,
                ClrIdx = ClrIdx
            };

            if (RequiresText(type) && string.IsNullOrWhiteSpace(request.Text))
            {
                ShowTooltip("Enter a song name or search query");
                return;
            }

            if (RequiresNumber(type) && request.Number < 1)
            {
                ShowTooltip("Enter a result number");
                return;
            }

            BoomboxCommandService.SendToServerOrExecuteLocal(request, Player);
        }

        private static bool RequiresText(BoomboxCommandType type)
        {
            return type == BoomboxCommandType.PlayLocal ||
                   type == BoomboxCommandType.PlayOnline ||
                   type == BoomboxCommandType.SearchOnline ||
                   type == BoomboxCommandType.QueueOnline;
        }

        private static bool RequiresNumber(BoomboxCommandType type)
        {
            return type == BoomboxCommandType.PlaySearchResult ||
                   type == BoomboxCommandType.QueueSearchResult;
        }

        private static int ParseInt(string value)
        {
            return int.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : 0;
        }

        private static float ParseFloat(string value)
        {
            return float.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : 0f;
        }

        private XUiC_TextInput GetInput(string id)
        {
            return GetChildById(id) as XUiC_TextInput;
        }

        private static void SetSelected(XUiC_TextInput input, bool selected)
        {
            input?.SetSelected(selected, false);
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
    }
}
