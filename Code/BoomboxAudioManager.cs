using System;
using System.Collections.Generic;
using Audio;
using UnityEngine;

namespace Boombox
{
    public static class BoomboxAudioManager
    {
        private const string SoundName = "boombox_music";

        // Keep clip list in sync with Config/sounds.xml.
        private static readonly string[] ClipNames =
        {
            "#@modfolder:Resources/Sounds.unity3d?1",
            "#@modfolder:Resources/Sounds.unity3d?2",
            "#@modfolder:Resources/Sounds.unity3d?3"
        };

        private static readonly Dictionary<Vector3i, Handle> ActiveHandles = new Dictionary<Vector3i, Handle>();
        private static readonly Dictionary<Vector3i, string> ClientStates = new Dictionary<Vector3i, string>();
        private static readonly object ClientSyncRoot = new object();

        private static readonly Dictionary<Vector3i, BoomboxServerState> ServerStates = new Dictionary<Vector3i, BoomboxServerState>();
        private static readonly object ServerSyncRoot = new object();

        private static bool IsClient => !GameManager.IsDedicatedServer;

        private static Vector3 ToWorld(Vector3i pos) => new Vector3(pos.x + 0.5f, pos.y + 0.5f, pos.z + 0.5f);

        private static bool IsServer()
        {
            var connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
            return GameManager.IsDedicatedServer || connection != null && connection.IsServer;
        }

        public static IReadOnlyList<string> AvailableClips => ClipNames;

        public static void ServerInitialize()
        {
            if (!IsServer())
            {
                return;
            }

            lock (ServerSyncRoot)
            {
                ServerStates.Clear();
            }
        }

        public static void ServerShutdown()
        {
            if (!IsServer())
            {
                return;
            }

            lock (ServerSyncRoot)
            {
                ServerStates.Clear();
            }
        }

        public static void ServerHandleToggle(World world, int clrIdx, Vector3i position, ClientInfo clientInfo, EntityPlayer player, bool pickup)
        {
            if (world == null || !IsServer())
            {
                return;
            }

            if (pickup)
            {
                ServerHandlePickup(world, clrIdx, position, clientInfo, player);
                return;
            }

            BoomboxServerState state;
            var shouldPlay = false;
            var shouldStop = false;

            lock (ServerSyncRoot)
            {
                if (!ServerStates.TryGetValue(position, out state))
                {
                    state = new BoomboxServerState();
                    ServerStates[position] = state;
                }

                if (!state.IsPlaying)
                {
                    state.ToggleCount++;
                    state.ClipName = SelectClip(world, position, state.ToggleCount);
                    state.IsPlaying = true;
                    shouldPlay = true;
                }
                else
                {
                    state.IsPlaying = false;
                    shouldStop = true;
                    ServerStates.Remove(position);
                }
            }

            if (shouldPlay)
            {
                BroadcastPlay(position, state);
                if (!GameManager.IsDedicatedServer)
                {
                    ClientPlay(position, state.ClipName);
                }
            }

            if (shouldStop)
            {
                BroadcastStop(position);
                if (!GameManager.IsDedicatedServer)
                {
                    ClientStop(position);
                }
            }
        }

        private static void ServerHandlePickup(World world, int clrIdx, Vector3i position, ClientInfo clientInfo, EntityPlayer player)
        {
            if (world == null)
            {
                return;
            }

            BoomboxServerState previousState = null;
            lock (ServerSyncRoot)
            {
                if (ServerStates.TryGetValue(position, out previousState))
                {
                    ServerStates.Remove(position);
                }
            }

            if (previousState != null && previousState.IsPlaying)
            {
                BroadcastStop(position);
                if (!GameManager.IsDedicatedServer)
                {
                    ClientStop(position);
                }
            }

            var blockValue = world.GetBlock(position);
            var gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return;
            }

            var playerId = player?.entityId ?? clientInfo?.entityId ?? -1;
            gameManager.PickupBlockServer(clrIdx, position, blockValue, playerId, clientInfo?.PlatformId);
        }

        public static void ServerHandleBlockRemoved(World world, Vector3i position)
        {
            if (world == null || !IsServer())
            {
                return;
            }

            var shouldStop = false;
            lock (ServerSyncRoot)
            {
                if (ServerStates.TryGetValue(position, out var state) && state.IsPlaying)
                {
                    shouldStop = true;
                }

                ServerStates.Remove(position);
            }

            if (shouldStop)
            {
                BroadcastStop(position);
                if (!GameManager.IsDedicatedServer)
                {
                    ClientStop(position);
                }
            }
        }

        public static void ServerSyncClient(ClientInfo client)
        {
            if (client == null || !IsServer())
            {
                return;
            }

            List<BoomboxStateSnapshot> snapshots;
            lock (ServerSyncRoot)
            {
                snapshots = new List<BoomboxStateSnapshot>(ServerStates.Count);
                foreach (var entry in ServerStates)
                {
                    var state = entry.Value;
                    if (!state.IsPlaying)
                    {
                        continue;
                    }

                    snapshots.Add(new BoomboxStateSnapshot(entry.Key, state.ClipName));
                }
            }

            var package = NetPackageManager.GetPackage<NetPackageBoomboxSync>().Setup(snapshots);
            client.SendPackage(package);
        }

        private static void BroadcastPlay(Vector3i position, BoomboxServerState state)
        {
            var package = NetPackageManager
                .GetPackage<NetPackageBoomboxPlay>()
                .Setup(position, state.ClipName);

            SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(package, false, -1, -1, -1, null, -1, false);
        }

        private static void BroadcastStop(Vector3i position)
        {
            var package = NetPackageManager
                .GetPackage<NetPackageBoomboxStop>()
                .Setup(position);

            SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(package, false, -1, -1, -1, null, -1, false);
        }

        private static string SelectClip(World world, Vector3i position, int toggleIndex)
        {
            if (ClipNames.Length == 0)
            {
                return string.Empty;
            }

            unchecked
            {
                var hash = 17;
                hash = hash * 31 + world.Seed;
                hash = hash * 31 + position.x;
                hash = hash * 31 + position.y;
                hash = hash * 31 + position.z;
                hash = hash * 31 + toggleIndex;
                var index = Mathf.Abs(hash) % ClipNames.Length;
                return ClipNames[index];
            }
        }

        public static bool IsWorldPlaying(Vector3i position)
        {
            if (!IsClient)
            {
                return false;
            }

            lock (ClientSyncRoot)
            {
                return ClientStates.ContainsKey(position);
            }
        }

        public static void ClientPlay(Vector3i position, string clipName)
        {
            if (!IsClient)
            {
                return;
            }

            lock (ClientSyncRoot)
            {
                ClientPlayInternal(position, clipName);
            }
        }

        public static void ClientSync(IEnumerable<BoomboxStateSnapshot> states)
        {
            if (!IsClient)
            {
                return;
            }

            lock (ClientSyncRoot)
            {
                StopAllInternal();
                if (states == null)
                {
                    return;
                }

                foreach (var entry in states)
                {
                    ClientPlayInternal(entry.Position, entry.ClipName);
                }
            }
        }

        public static void ClientStop(Vector3i position)
        {
            if (!IsClient)
            {
                return;
            }

            lock (ClientSyncRoot)
            {
                StopInternal(position);
            }
        }

        public static void StopAll()
        {
            if (!IsClient)
            {
                return;
            }

            lock (ClientSyncRoot)
            {
                StopAllInternal();
            }
        }

        private static void ClientPlayInternal(Vector3i position, string clipName)
        {
            StopInternal(position);
            ClientStates[position] = clipName ?? string.Empty;

            var handle = Manager.Play(ToWorld(position), SoundName, -1, true);
            ActiveHandles[position] = handle;
        }

        private static void StopAllInternal()
        {
            foreach (var kvp in ActiveHandles)
            {
                StopHandle(kvp.Value);
                Manager.Stop(ToWorld(kvp.Key), SoundName);
            }

            ActiveHandles.Clear();
            ClientStates.Clear();
        }

        private static void StopInternal(Vector3i position)
        {
            if (ActiveHandles.TryGetValue(position, out var handle))
            {
                ActiveHandles.Remove(position);
                StopHandle(handle);
                Manager.Stop(ToWorld(position), SoundName);
            }

            ClientStates.Remove(position);
        }

        private static void StopHandle(Handle handle)
        {
            if (handle == null)
            {
                return;
            }

            try
            {
                StopAudioSource(handle.nearSource);
                StopAudioSource(handle.farSource);
            }
            catch (MissingReferenceException)
            {
                // ignored
            }
            catch (NullReferenceException)
            {
                // ignored
            }
        }

        private static void StopAudioSource(AudioSource source)
        {
            if (source == null)
            {
                return;
            }

            source.Stop();
            var gameObject = source.gameObject;
            if (gameObject != null)
            {
                UnityEngine.Object.Destroy(gameObject);
            }
        }

        public readonly struct BoomboxStateSnapshot
        {
            public BoomboxStateSnapshot(Vector3i position, string clipName)
            {
                Position = position;
                ClipName = clipName ?? string.Empty;
            }

            public Vector3i Position { get; }
            public string ClipName { get; }
        }

        private sealed class BoomboxServerState
        {
            public bool IsPlaying;
            public string ClipName = string.Empty;
            public int ToggleCount;
        }
    }
}
