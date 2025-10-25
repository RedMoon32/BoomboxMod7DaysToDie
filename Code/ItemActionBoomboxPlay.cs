using UnityEngine;

namespace Boombox
{
    public class ItemActionBoomboxPlay : ItemAction
    {
        private const float CooldownSeconds = 0.2f;
        private float _lastPlayTime = -1f;

        public override void StartAction(ItemActionData actionData, bool isReleased)
        {
            base.StartAction(actionData, isReleased);

            if (actionData?.invData?.holdingEntity is EntityPlayerLocal player)
            {
                Debug.Log($"[Boombox] StartAction idx={actionData.indexInEntityOfAction} isReleased={isReleased}");
            }
        }

        public override void ExecuteAction(ItemActionData actionData, bool isReleased)
        {
            if (actionData == null || isReleased)
            {
                return;
            }

            if (actionData.invData == null || actionData.invData.holdingEntity is not EntityPlayerLocal player)
            {
                return;
            }

            Debug.Log($"[Boombox] ExecuteAction idx={actionData.indexInEntityOfAction} isReleased={isReleased}");
            var actionIndex = actionData.indexInEntityOfAction;
            if (actionIndex == 1)
            {
                BoomboxAudioManager.StopPlayback(player);
                actionData.HasExecuted = true;
                return;
            }

            var now = Time.time;
            if (_lastPlayTime > 0f && now - _lastPlayTime < CooldownSeconds)
            {
                return;
            }

            _lastPlayTime = now;

            BoomboxAudioManager.PlayRandomTrack(player);
            actionData.HasExecuted = true;
        }

        public override void CancelAction(ItemActionData actionData) => base.CancelAction(actionData);
    }
}
