using System;
using UnityEngine;

namespace Boombox
{
    public class BoomboxModApi : IModApi
    {
        public void InitMod(Mod modInstance)
        {
            Debug.Log("[Boombox] Mod API initialized");
            RegisterNetPackages();

            ModEvents.GameStartDone.RegisterHandler(OnGameStartDone);
            ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDown);
            ModEvents.PlayerSpawnedInWorld.RegisterHandler(OnPlayerSpawnedInWorld);
        }

        private static void RegisterNetPackages()
        {
            TryRegisterPackage(BoomboxNetPackageIds.ToggleRequest, typeof(NetPackageBoomboxToggleRequest));
            TryRegisterPackage(BoomboxNetPackageIds.Play, typeof(NetPackageBoomboxPlay));
            TryRegisterPackage(BoomboxNetPackageIds.Stop, typeof(NetPackageBoomboxStop));
            TryRegisterPackage(BoomboxNetPackageIds.Sync, typeof(NetPackageBoomboxSync));
        }

        private static void TryRegisterPackage(int id, Type packageType)
        {
            try
            {
                var name = NetPackageManager.GetPackageName(id);
                if (!string.IsNullOrEmpty(name))
                {
                    return;
                }
            }
            catch (Exception)
            {
                // ignored, we will register mapping next.
            }

            NetPackageManager.AddPackageMapping(id, packageType);
        }

        private static void OnGameStartDone(ref ModEvents.SGameStartDoneData data)
        {
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
            if (!IsServer)
            {
                return;
            }

            BoomboxAudioManager.ServerSyncClient(data.ClientInfo);
        }

        private static bool IsServer
        {
            get
            {
                var connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
                return GameManager.IsDedicatedServer || connection != null && connection.IsServer;
            }
        }
    }
}
