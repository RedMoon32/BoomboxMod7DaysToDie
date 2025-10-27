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
            RegisterPackageWithLog("ToggleRequest", BoomboxNetPackageIds.ToggleRequest, typeof(NetPackageBoomboxToggleRequest));
            RegisterPackageWithLog("Play", BoomboxNetPackageIds.Play, typeof(NetPackageBoomboxPlay));
            RegisterPackageWithLog("Stop", BoomboxNetPackageIds.Stop, typeof(NetPackageBoomboxStop));
            RegisterPackageWithLog("Sync", BoomboxNetPackageIds.Sync, typeof(NetPackageBoomboxSync));
        }

        private static void RegisterPackageWithLog(string name, int id, Type packageType)
        {
            TryRegisterPackage(id, packageType);
            try
            {
                var resolvedId = NetPackageManager.GetPackageId(packageType);
                Debug.Log($"[Boombox] NetPackage '{name}' resolved ID {resolvedId} (expected {id})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Boombox] Failed to resolve NetPackage '{name}' (expected ID {id}): {ex}");
            }
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
