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

            base.ExecuteAction(actionData, isReleased);
        }
    }
}
