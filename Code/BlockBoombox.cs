using System.Collections.Generic;
using UnityEngine;

namespace Boombox
{
    public class BlockBoombox : Block
    {
        private static readonly BlockActivationCommand PlayCommand = new("boombox_play_toggle", "hand", true, false, "toggle");
        private static readonly BlockActivationCommand PickupCommand = new("boombox_pickup", "hand", true, false, "pickup");

        private static bool IsClient => !GameManager.IsDedicatedServer;

        private static Vector3 Center(Vector3i pos) => new(pos.x + 0.5f, pos.y + 0.5f, pos.z + 0.5f);

        public override BlockValue OnBlockPlaced(WorldBase world, int clrIdx, Vector3i blockPos, BlockValue blockValue, GameRandom random)
        {
            return base.OnBlockPlaced(world, clrIdx, blockPos, blockValue, random);
        }

        public override void OnBlockUnloaded(WorldBase world, int clrIdx, Vector3i blockPos, BlockValue blockValue)
        {
            if (IsClient)
            {
                BoomboxAudioManager.StopAt(blockPos);
            }

            base.OnBlockUnloaded(world, clrIdx, blockPos, blockValue);
        }

        public override DestroyedResult OnBlockDestroyedBy(WorldBase world, int clrIdx, Vector3i blockPos, BlockValue blockValue, int entityId, bool isDrop)
        {
            if (IsClient)
            {
                BoomboxAudioManager.StopAt(blockPos);
            }

            return base.OnBlockDestroyedBy(world, clrIdx, blockPos, blockValue, entityId, isDrop);
        }

        public override void OnBlockRemoved(WorldBase world, Chunk chunk, Vector3i blockPos, BlockValue blockValue)
        {
            if (IsClient)
            {
                BoomboxAudioManager.StopAt(blockPos);
            }

            base.OnBlockRemoved(world, chunk, blockPos, blockValue);
        }


        public override bool OnBlockActivated(WorldBase world, int clrIdx, Vector3i blockPos, BlockValue blockValue, EntityPlayerLocal player)
        {
            if (!IsClient)
            {
                return true;
            }

            if (player != null && player.Crouching)
            {
                HandlePickup(world, clrIdx, blockPos, player);
                return true;
            }

            if (BoomboxAudioManager.IsWorldPlaying(blockPos))
            {
                BoomboxAudioManager.StopAt(blockPos);
                GameManager.ShowTooltip(player, "Boombox stopped.");
            }
            else
            {
                BoomboxAudioManager.PlayAt(blockPos);
                GameManager.ShowTooltip(player, "Boombox playing.");
            }

            return true;
        }

        public override string GetActivationText(WorldBase world, BlockValue blockValue, int clrIdx, Vector3i blockPos, EntityAlive entityFocusing)
        {
            if (entityFocusing is EntityPlayerLocal player && player.Crouching)
            {
                return "Hold [E] to pick up boombox";
            }

            return BoomboxAudioManager.IsWorldPlaying(blockPos)
                ? "Press [E] to stop boombox"
                : "Press [E] to play boombox";
        }

        public override bool HasBlockActivationCommands(WorldBase world, BlockValue blockValue, int clrIdx, Vector3i blockPos, EntityAlive entityFocusing) => true;

        public override BlockActivationCommand[] GetBlockActivationCommands(WorldBase world, BlockValue blockValue, int clrIdx, Vector3i blockPos, EntityAlive entityFocusing)
        {
            if (entityFocusing is EntityPlayerLocal player && player.Crouching)
            {
                return new[] { PickupCommand };
            }

            return new[] { PlayCommand };
        }

        public override bool OnBlockActivated(string command, WorldBase world, int clrIdx, Vector3i blockPos, BlockValue blockValue, EntityPlayerLocal player)
        {
            return OnBlockActivated(world, clrIdx, blockPos, blockValue, player);
        }

        private static void HandlePickup(WorldBase world, int clrIdx, Vector3i blockPos, EntityPlayerLocal player)
        {
            BoomboxAudioManager.StopAt(blockPos);

            world.SetBlockRPC(clrIdx, blockPos, BlockValue.Air);

            var itemValue = ItemClass.GetItem("boombox", false);
            var stack = new ItemStack(itemValue, 1);

            if (!player.inventory.AddItem(stack))
            {
                var dropPos = Center(blockPos) + Vector3.up * 0.5f;
                GameManager.Instance.ItemDropServer(stack, dropPos, Vector3.zero, -1, 60f, false);
            }

            GameManager.ShowTooltip(player, "Picked up the boombox.");
        }
    }
}
