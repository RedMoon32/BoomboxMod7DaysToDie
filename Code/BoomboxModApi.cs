using System;
using UnityEngine;

namespace Boombox
{
    public class BoomboxModApi : IModApi
    {
        private static bool _blockItemConfigured;

        public void InitMod(Mod modInstance)
        {
            Debug.Log("[Boombox] Mod API initialized");
            ConfigureBoomboxBlockItem();
            try
            {
                Debug.Log("[Boombox] Enumerating NetPackage implementations");
                ReflectionHelpers.FindTypesImplementingBase(typeof(NetPackage), (Type packageType) =>
                {
                    Debug.Log($"[Boombox] NetPackage discovered: {packageType.FullName}");
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Boombox] Failed to enumerate NetPackage implementations: {ex}");
            }

            ModEvents.GameStartDone.RegisterHandler(OnGameStartDone);
            ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDown);
            ModEvents.PlayerSpawnedInWorld.RegisterHandler(OnPlayerSpawnedInWorld);
        }

        private static void OnGameStartDone(ref ModEvents.SGameStartDoneData data)
        {
            ConfigureBoomboxBlockItem();
            ConvertLocalPlayerInventories();

            if (IsServer)
            {
                BoomboxAudioManager.ServerInitialize();
            }

            if (!GameManager.IsDedicatedServer)
            {
                BoomboxAudioManager.StopAll();
            }
        }

        private static void OnWorldShuttingDown(ref ModEvents.SWorldShuttingDownData data)
        {
            if (IsServer)
            {
                BoomboxAudioManager.ServerShutdown();
            }

            BoomboxAudioManager.StopAll();
        }

        private static void OnPlayerSpawnedInWorld(ref ModEvents.SPlayerSpawnedInWorldData data)
        {
            ConfigureBoomboxBlockItem();

            var world = GameManager.Instance?.World;
            if (world == null)
            {
                return;
            }

            if (world.GetEntity(data.EntityId) is EntityPlayer player)
            {
                ConvertPlayerInventory(player);
            }

            if (IsServer && data.ClientInfo != null)
            {
                //BoomboxAudioManager.ServerSyncClient(data.ClientInfo);
            }
        }

        private static bool IsServer
        {
            get
            {
                var connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
                return GameManager.IsDedicatedServer || connection != null && connection.IsServer;
            }
        }

        private static void ConfigureBoomboxBlockItem()
        {
            if (_blockItemConfigured)
            {
                return;
            }

            try
            {
                var blockItemClass = ItemClass.GetItemClass("boomboxBlock", false);
                if (blockItemClass == null)
                {
                    Debug.LogWarning("[Boombox] Failed to locate boomboxBlock item class for configuration");
                    return;
                }

                blockItemClass.Stacknumber.Value = 1;
                blockItemClass.CreativeMode = EnumCreativeMode.Player;
                blockItemClass.Groups = new[] { "Building" };
                if (string.IsNullOrEmpty(blockItemClass.DescriptionKey))
                {
                    blockItemClass.DescriptionKey = "boomboxDesc";
                }
                if (blockItemClass.CustomIcon == null || string.IsNullOrEmpty(blockItemClass.CustomIcon.Value))
                {
                    blockItemClass.CustomIcon = new DataItem<string>("speaker");
                }

                _blockItemConfigured = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Boombox] Failed to configure boombox block item: {ex}");
            }
        }

        private static void ConvertLocalPlayerInventories()
        {
            try
            {
                var world = GameManager.Instance?.World;
                if (world == null)
                {
                    return;
                }

                var localPlayers = world.GetLocalPlayers();
                if (localPlayers == null)
                {
                    return;
                }

                foreach (var player in localPlayers)
                {
                    ConvertPlayerInventory(player);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Boombox] Failed to convert local player inventories: {ex}");
            }
        }

        private static void ConvertPlayerInventory(EntityPlayer player)
        {
            if (player == null)
            {
                return;
            }

            try
            {
                var blockItemClass = ItemClass.GetItemClass("boomboxBlock", false);
                if (blockItemClass == null)
                {
                    return;
                }

                var replacementValue = ItemClass.GetItem("boomboxBlock", false);
                ConvertInventoryContainer(player.inventory, replacementValue);
                if (player.bag != null)
                {
                    ConvertInventoryContainer(player.bag, replacementValue);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Boombox] Failed to convert inventory for player {player.entityId}: {ex}");
            }
        }

        private static void ConvertInventoryContainer(Inventory inventory, ItemValue replacementTemplate)
        {
            if (inventory == null || replacementTemplate == null || replacementTemplate.IsEmpty())
            {
                return;
            }

            for (int i = 0; i < inventory.GetSlotCount(); i++)
            {
                var stack = inventory.GetItem(i);
                if (stack == null || stack.IsEmpty())
                {
                    continue;
                }

                var itemClass = stack.itemValue.ItemClass;
                if (itemClass == null || itemClass.Name != "boombox")
                {
                    continue;
                }

                var newValue = replacementTemplate.Clone();
                newValue.Quality = stack.itemValue.Quality;
                newValue.Meta = stack.itemValue.Meta;
                newValue.UseTimes = stack.itemValue.UseTimes;
                newValue.Seed = stack.itemValue.Seed;
                inventory.SetItem(i, new ItemStack(newValue, stack.count));
            }
        }

        private static void ConvertInventoryContainer(Bag bag, ItemValue replacementTemplate)
        {
            if (bag == null || replacementTemplate == null || replacementTemplate.IsEmpty())
            {
                return;
            }

            var slots = bag.GetSlots();
            for (int i = 0; i < slots.Length; i++)
            {
                var stack = slots[i];
                if (stack == null || stack.IsEmpty())
                {
                    continue;
                }

                var itemClass = stack.itemValue.ItemClass;
                if (itemClass == null || itemClass.Name != "boombox")
                {
                    continue;
                }

                var newValue = replacementTemplate.Clone();
                newValue.Quality = stack.itemValue.Quality;
                newValue.Meta = stack.itemValue.Meta;
                newValue.UseTimes = stack.itemValue.UseTimes;
                newValue.Seed = stack.itemValue.Seed;
                bag.SetSlot(i, new ItemStack(newValue, stack.count));
            }
        }
    }
}
