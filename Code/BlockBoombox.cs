using System.Collections.Generic;
using UnityEngine;

namespace Boombox
{
    public class BlockBoombox : Block
    {
        private static readonly BlockActivationCommand PlayCommand = new BlockActivationCommand("boombox_play_toggle", "hand", true, false, "toggle");
        private static readonly BlockActivationCommand PickupCommand = new BlockActivationCommand("boombox_pickup", "hand", true, false, "pickup");

        private static bool IsClient => !GameManager.IsDedicatedServer;
        private static bool IsServer => GameManager.IsDedicatedServer || SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;

        public override BlockValue OnBlockPlaced(WorldBase world, int clrIdx, Vector3i blockPos, BlockValue blockValue, GameRandom random)
        {
            return base.OnBlockPlaced(world, clrIdx, blockPos, blockValue, random);
        }

        // public override void OnBlockUnloaded(WorldBase world, int clrIdx, Vector3i blockPos, BlockValue blockValue)
        // {
        //     HandleBlockRemoved(world, blockPos);

        //     base.OnBlockUnloaded(world, clrIdx, blockPos, blockValue);
        // }

        public override DestroyedResult OnBlockDestroyedBy(WorldBase world, int clrIdx, Vector3i blockPos, BlockValue blockValue, int entityId, bool isDrop)
        {
            HandleBlockRemoved(world, blockPos);

            return base.OnBlockDestroyedBy(world, clrIdx, blockPos, blockValue, entityId, isDrop);
        }

        public override void OnBlockRemoved(WorldBase world, Chunk chunk, Vector3i blockPos, BlockValue blockValue)
        {
            HandleBlockRemoved(world, blockPos);

            base.OnBlockRemoved(world, chunk, blockPos, blockValue);
        }


        public override bool OnBlockActivated(WorldBase world, int clrIdx, Vector3i blockPos, BlockValue blockValue, EntityPlayerLocal player)
        {
            HandleActivation(world, clrIdx, blockPos, player);
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
            HandleActivation(world, clrIdx, blockPos, player);
            return true;
        }

        private static void HandleActivation(WorldBase worldBase, int clrIdx, Vector3i blockPos, EntityPlayerLocal player)
        {
            if (worldBase == null)
            {
                return;
            }

            var wantsPickup = player != null && player.Crouching;
            var world = worldBase as World;
            var connection = SingletonMonoBehaviour<ConnectionManager>.Instance;

            if (IsServer)
            {
                BoomboxAudioManager.ServerHandleToggle(world, clrIdx, blockPos, null, player, wantsPickup);
            }
            else
            {
                var request = NetPackageManager
                    .GetPackage<NetPackageBoomboxToggleRequest>()
                    .Setup(blockPos, clrIdx, wantsPickup);

                connection?.SendToServer(request, false);
            }
        }

        private static void HandleBlockRemoved(WorldBase worldBase, Vector3i blockPos)
        {
            if (worldBase == null)
            {
                return;
            }

            if (IsClient)
            {
                BoomboxAudioManager.ClientStop(blockPos);
            }

            if (IsServer && worldBase is World world)
            {
                BoomboxAudioManager.ServerHandleBlockRemoved(world, blockPos);
            }
        }
    }
}
