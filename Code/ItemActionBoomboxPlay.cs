using UnityEngine;

namespace Boombox
{
    public class ItemActionBoomboxPlay : ItemAction
    {
        private const float CooldownSeconds = 0.2f;
        private float _lastPlayTime = -1f;

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

            var now = Time.time;
            if (_lastPlayTime > 0f && now - _lastPlayTime < CooldownSeconds)
            {
                return;
            }

            _lastPlayTime = now;

            BoomboxAudioManager.PlayRandomTrack(player);
            actionData.HasExecuted = true;
        }
    }
}
