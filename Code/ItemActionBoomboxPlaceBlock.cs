namespace Boombox
{
    public class ItemActionBoomboxPlaceBlock : ItemActionPlaceAsBlock
    {
        public override void ExecuteAction(ItemActionData actionData, bool isReleased)
        {
            if (actionData == null)
            {
                return;
            }

            if (actionData.invData == null || actionData.invData.holdingEntity is not EntityPlayerLocal player)
            {
                base.ExecuteAction(actionData, isReleased);
                return;
            }

            if (player.Crouching)
            {
                BoomboxAudioManager.StopPlayer(player);
                actionData.HasExecuted = true;
                return;
            }

            BoomboxAudioManager.BeginPlacement(player);
            base.ExecuteAction(actionData, isReleased);
    }
}
}
