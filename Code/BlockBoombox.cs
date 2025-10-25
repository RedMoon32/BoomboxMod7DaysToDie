using System;
using UnityEngine;

namespace Boombox
{
    public class BlockBoombox : Block
    {
        private static readonly BlockActivationCommand PlayCommand = new BlockActivationCommand("boombox_play_toggle", "hand", true, false, "toggle");
        private static readonly BlockActivationCommand PickupCommand = new BlockActivationCommand("boombox_pickup", "hand", true, false, "pickup");

        public override BlockValue OnBlockPlaced(WorldBase world, int clrIdx, Vector3i blockPos, BlockValue blockValue, GameRandom random)
        {
            var result = base.OnBlockPlaced(world, clrIdx, blockPos, blockValue, random);
            var placingPlayer = BoomboxAudioManager.ConsumePendingPlacementPlayer();
            Debug.Log($"[Boombox] OnBlockPlaced at {blockPos} by {(placingPlayer != null ? placingPlayer.EntityName : "unknown")}");
            if (placingPlayer != null)
            {
                BoomboxAudioManager.OnBlockPlaced(placingPlayer, blockPos);
            }

            return result;
        }

        public override void OnBlockUnloaded(WorldBase world, int clrIdx, Vector3i blockPos, BlockValue blockValue)
        {
            Debug.Log($"[Boombox] OnBlockUnloaded at {blockPos}");
            BoomboxAudioManager.OnBlockUnloaded(blockPos);
            base.OnBlockUnloaded(world, clrIdx, blockPos, blockValue);
        }

        public override DestroyedResult OnBlockDestroyedBy(WorldBase world, int clrIdx, Vector3i blockPos, BlockValue blockValue, int entityId, bool isDrop)
        {
            Debug.Log($"[Boombox] OnBlockDestroyedBy at {blockPos} by entity {entityId}");
            BoomboxAudioManager.OnBlockRemoved(blockPos);
            return base.OnBlockDestroyedBy(world, clrIdx, blockPos, blockValue, entityId, isDrop);
        }

        public override bool OnBlockActivated(WorldBase world, int clrIdx, Vector3i blockPos, BlockValue blockValue, EntityPlayerLocal player)
        {
            Debug.Log($"[Boombox] OnBlockActivated at {blockPos} by {(player != null ? player.EntityName : "unknown")}, crouching={player?.Crouching}");
            if (player.Crouching)
            {
                HandlePickup(world, clrIdx, blockPos, player);
                return true;
            }

            if (BoomboxAudioManager.IsWorldPlaying(blockPos))
            {
                BoomboxAudioManager.StopAt(blockPos, player);
            }
            else
            {
                BoomboxAudioManager.PlayNextAt(blockPos, player);
            }

            return true;
        }

        public override int OnBlockDamaged(WorldBase world, int clrIdx, Vector3i blockPos, BlockValue blockValue, int damagePoints, int entityIdThatDamaged, ItemActionAttack.AttackHitInfo attackHitInfo, bool useHarvestTool, bool bypassMaxDamage, int recDepth)
        {
            if (world != null && entityIdThatDamaged >= 0)
            {
                var entity = world.GetEntity(entityIdThatDamaged) as EntityPlayerLocal;
                if (entity != null)
                {
                    var heldClass = entity.inventory?.holdingItem;
                    if (heldClass != null && string.Equals(heldClass.Name, "boombox", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Log($"[Boombox] OnBlockDamaged intercepted from player {entity.EntityName}, crouching={entity.Crouching}");
                        if (entity.Crouching)
                        {
                            BoomboxAudioManager.StopAt(blockPos, entity);
                        }
                        else
                        {
                            BoomboxAudioManager.PlayNextAt(blockPos, entity);
                        }

                        return 0;
                    }
                }
            }

            return base.OnBlockDamaged(world, clrIdx, blockPos, blockValue, damagePoints, entityIdThatDamaged, attackHitInfo, useHarvestTool, bypassMaxDamage, recDepth);
        }

        public override string GetActivationText(WorldBase world, BlockValue blockValue, int clrIdx, Vector3i blockPos, EntityAlive entityFocusing)
        {
            if (entityFocusing is EntityPlayerLocal player && player.Crouching)
            {
                return "Hold [E] to pick up boombox";
            }

            return "Press [E] to play/stop boombox";
        }

        public override bool HasBlockActivationCommands(WorldBase world, BlockValue blockValue, int clrIdx, Vector3i blockPos, EntityAlive entityFocusing)
        {
            return true;
        }

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
            Debug.Log($"[Boombox] OnBlockActivated(command='{command}') at {blockPos}");
            return OnBlockActivated(world, clrIdx, blockPos, blockValue, player);
        }

        private static void HandlePickup(WorldBase world, int clrIdx, Vector3i blockPos, EntityPlayerLocal player)
        {
            Debug.Log($"[Boombox] HandlePickup at {blockPos}");
            BoomboxAudioManager.OnBlockPickedUp(player, blockPos);

            world.SetBlockRPC(clrIdx, blockPos, BlockValue.Air);

            var itemValue = ItemClass.GetItem("boombox", false);
            var stack = new ItemStack(itemValue, 1);

            if (!player.inventory.AddItem(stack))
            {
                var dropPos = new Vector3(blockPos.x + 0.5f, blockPos.y + 1f, blockPos.z + 0.5f);
                GameManager.Instance.ItemDropServer(stack, dropPos, Vector3.zero, -1, 60f, false);
            }

            GameManager.ShowTooltip(player, "Picked up the boombox.");
        }
    }
}
