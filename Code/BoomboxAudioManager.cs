using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Audio;
using UnityEngine;
using UnityEngine.Networking;

namespace Boombox
{
    public static class BoomboxAudioManager
    {
        private const string NoiseSoundName = "boombox_music";
        private const string SoundNodePrefix = "boombox_music_track";
        private const string LocalMusicSoundPrefix = "boombox_music_file_";
        private const float MaxVolumeMultiplier = 5f;
        private static readonly string[] SupportedLocalMusicExtensions = { ".mp3", ".wav" };

        // Clip list is discovered from Resources/MusicToPlay next to the DLL.
        private static readonly object ClipCacheSyncRoot = new object();
        private static string[] cachedClipNames;
        private static Dictionary<string, LocalMusicTrack> cachedLocalMusicTracks;
        private static readonly HashSet<string> RegisteredLocalMusicGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly object RandomSyncRoot = new object();
        private static readonly System.Random Random = new System.Random();

        private static string[] ClipNames
        {
            get
            {
                var cache = cachedClipNames;
                if (cache != null)
                {
                    return cache;
                }

                lock (ClipCacheSyncRoot)
                {
                    if (cachedClipNames == null)
                    {
                        cachedLocalMusicTracks = LoadLocalMusicTracks();
                        cachedClipNames = cachedLocalMusicTracks.Keys
                            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    }

                    return cachedClipNames;
                }
            }
        }

        private static readonly Dictionary<Vector3i, Handle> ActiveHandles = new Dictionary<Vector3i, Handle>();
        private static readonly Dictionary<Vector3i, HandleVolumes> ActiveHandleBaseVolumes = new Dictionary<Vector3i, HandleVolumes>();
        private static readonly Dictionary<Vector3i, string> ClientStates = new Dictionary<Vector3i, string>();
        private static readonly Dictionary<Vector3i, ClientPlaybackState> ClientPlaybackCoroutines = new Dictionary<Vector3i, ClientPlaybackState>();
        private static readonly object ClientSyncRoot = new object();

        private static readonly Dictionary<Vector3i, BoomboxServerState> ServerStates = new Dictionary<Vector3i, BoomboxServerState>();
        private static readonly object ServerSyncRoot = new object();
        private static readonly HashSet<Vector3i> KnownBoomboxPositions = new HashSet<Vector3i>();
        private static float ServerVolume = 1f;
        private static float ClientVolume = 1f;

        private static bool IsClient => !GameManager.IsDedicatedServer;

        private static Vector3 ToWorld(Vector3i pos) => new Vector3(pos.x + 0.5f, pos.y + 0.5f, pos.z + 0.5f);

        private static bool IsServer()
        {
            var connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
            return GameManager.IsDedicatedServer || connection != null && connection.IsServer;
        }

        public static IReadOnlyList<string> AvailableClips => ClipNames;

        public static void RegisterBoombox(World world, Vector3i position)
        {
            if (world == null || !IsServer())
            {
                return;
            }

            lock (ServerSyncRoot)
            {
                KnownBoomboxPositions.Add(position);
            }
        }

        public static void UnregisterBoombox(Vector3i position)
        {
            lock (ServerSyncRoot)
            {
                KnownBoomboxPositions.Remove(position);
            }
        }

        public static List<Vector3i> GetKnownBoomboxPositions(World world)
        {
            var result = new HashSet<Vector3i>();

            lock (ServerSyncRoot)
            {
                foreach (var position in KnownBoomboxPositions)
                {
                    if (IsBoomboxAt(world, position))
                    {
                        result.Add(position);
                    }
                }
            }

            try
            {
                var indexed = world?.ChunkCache?.GetIndexedBlocks("boomboxBlock");
                if (indexed != null)
                {
                    foreach (var position in indexed)
                    {
                        if (IsBoomboxAt(world, position))
                        {
                            result.Add(position);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Boombox] Failed to scan indexed boombox blocks: {ex}");
            }

            return result.ToList();
        }

        private static bool IsBoomboxAt(World world, Vector3i position)
        {
            try
            {
                var block = world?.GetBlock(position).Block;
                return block != null && string.Equals(block.GetBlockName(), "boomboxBlock", StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void ServerInitialize()
        {
            if (!IsServer())
            {
                return;
            }

            lock (ServerSyncRoot)
            {
                foreach (var state in ServerStates.Values)
                {
                    state.IsPlaying = false;
                    StopNoiseLoop(state);
                }

                ServerStates.Clear();
                KnownBoomboxPositions.Clear();
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
                foreach (var state in ServerStates.Values)
                {
                    state.IsPlaying = false;
                    StopNoiseLoop(state);
                }

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
                    var previousClip = state.ClipName;
                    state.ToggleCount++;
                    state.PlaybackToken++;
                    state.ClipName = SelectClip(world, position, state.ToggleCount, previousClip);
                    state.IsPlaying = true;
                    shouldPlay = true;
                }
                else
                {
                    state.IsPlaying = false;
                    state.PlaybackToken++;
                    shouldStop = true;
                    ServerStates.Remove(position);
                }
            }

            if (shouldPlay)
            {
                var usesRuntimeTransfer = BroadcastPlay(position, state);
                if (!usesRuntimeTransfer && !GameManager.IsDedicatedServer)
                {
                    ClientPlay(position, state.ClipName);
                }

                EmitNoise(world, position, player);
                StartNoiseLoop(world, position, state, player);
            }

            if (shouldStop)
            {
                StopNoiseLoop(state);
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
            previousState.IsPlaying = false;
            StopNoiseLoop(previousState);
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

        public static void ServerHandleTrackFinished(World world, Vector3i position, ClientInfo clientInfo, string clipName)
        {
            if (world == null || !IsServer())
            {
                Debug.LogWarning("[Boombox] TrackFinished ignored (world missing or not server)");
                return;
            }

            var normalizedClip = clipName ?? string.Empty;
            BoomboxServerState state;
            string nextClip = null;
            var shouldStop = false;

            lock (ServerSyncRoot)
            {
                if (!ServerStates.TryGetValue(position, out state) || !state.IsPlaying)
                {
                    Debug.LogWarning($"[Boombox] TrackFinished ignored (no active state) clip='{normalizedClip}' pos={position}");
                    return;
                }

                var currentClip = state.ClipName ?? string.Empty;
                if (!string.Equals(currentClip, normalizedClip, StringComparison.Ordinal))
                {
                    Debug.LogWarning($"[Boombox] TrackFinished ignored (clip mismatch) expected='{currentClip}' got='{normalizedClip}' pos={position}");
                    return;
                }

                state.ToggleCount++;
                var selected = SelectClip(world, position, state.ToggleCount, currentClip);
                if (string.IsNullOrEmpty(selected))
                {
                    state.IsPlaying = false;
                    state.PlaybackToken++;
                    shouldStop = true;
                    ServerStates.Remove(position);
                    Debug.Log($"[Boombox] TrackFinished stopping playback pos={position} clip='{normalizedClip}'");
                }
                else
                {
                    state.PlaybackToken++;
                    state.ClipName = selected;
                    nextClip = selected;
                    Debug.Log($"[Boombox] TrackFinished advancing pos={position} clip='{normalizedClip}' -> next='{nextClip}'");
                }
            }

            if (shouldStop)
            {
                StopNoiseLoop(state);
                BroadcastStop(position);
                if (!GameManager.IsDedicatedServer)
                {
                    ClientStop(position);
                }
                Debug.Log($"[Boombox] TrackFinished broadcast stop pos={position}");
                return;
            }

            if (string.IsNullOrEmpty(nextClip))
            {
                Debug.LogWarning($"[Boombox] TrackFinished had no next clip pos={position}");
                return;
            }

            var usesRuntimeTransfer = BroadcastPlay(position, state);
            if (!usesRuntimeTransfer && !GameManager.IsDedicatedServer)
            {
                ClientPlay(position, nextClip);
            }

            EntityPlayer instigator = null;
            if (state != null && state.LastActivatorEntityId != -1)
            {
                instigator = world?.GetEntity(state.LastActivatorEntityId) as EntityPlayer;
            }

            Debug.Log($"[Boombox] TrackFinished triggered new playback pos={position} by entity={instigator?.entityId ?? -1}");
            EmitNoise(world, position, instigator);
        }

        private static void EmitNoise(World world, Vector3i position, EntityPlayer instigator)
        {
            if (world?.aiDirector == null)
            {
                return;
            }

            try
            {
        world.aiDirector.NotifyNoise(instigator, ToWorld(position), NoiseSoundName, 1f);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Boombox] Failed to emit noise: {ex}");
            }
        }

        private static void StartNoiseLoop(World world, Vector3i position, BoomboxServerState state, EntityPlayer instigator)
        {
            StopNoiseLoop(state);

            var gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return;
            }

            state.LastActivatorEntityId = instigator?.entityId ?? -1;
            state.NoiseCoroutine = gameManager.StartCoroutine(NoisePulseRoutine(world, position, state));
        }

        private static void StopNoiseLoop(BoomboxServerState state)
        {
            if (state == null)
            {
                return;
            }

            if (state.NoiseCoroutine != null)
            {
                var gameManager = GameManager.Instance;
                if (gameManager != null)
                {
                    gameManager.StopCoroutine(state.NoiseCoroutine);
                }

                state.NoiseCoroutine = null;
            }

            state.LastActivatorEntityId = -1;
        }

        private static IEnumerator NoisePulseRoutine(World world, Vector3i position, BoomboxServerState state)
        {
            var wait = new WaitForSeconds(10f);
            while (state.IsPlaying)
            {
                if (world == null)
                {
                    break;
                }

                yield return wait;

                if (!state.IsPlaying || world == null)
                {
                    break;
                }

                EntityPlayer instigator = null;
                if (state.LastActivatorEntityId != -1)
                {
                    instigator = world.GetEntity(state.LastActivatorEntityId) as EntityPlayer;
                }

                EmitNoise(world, position, instigator);
            }

            state.NoiseCoroutine = null;
            state.LastActivatorEntityId = -1;
        }

        public static void ServerHandleBlockRemoved(World world, Vector3i position)
        {
            if (world == null || !IsServer())
            {
                return;
            }

            var shouldStop = false;
            BoomboxServerState removedState = null;
            lock (ServerSyncRoot)
            {
                if (ServerStates.TryGetValue(position, out var state))
                {
                    removedState = state;
                    shouldStop = state.IsPlaying;
                }

                ServerStates.Remove(position);
            }

        if (removedState != null)
        {
            removedState.IsPlaying = false;
            StopNoiseLoop(removedState);
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

        public static void ServerSyncVolumeClient(ClientInfo client)
        {
            if (client == null || !IsServer())
            {
                return;
            }

            float volume;
            lock (ServerSyncRoot)
            {
                volume = ServerVolume;
            }

            client.SendPackage(NetPackageManager.GetPackage<NetPackageBoomboxVolume>().Setup(volume));
        }

        public static void ServerSetVolume(float volume)
        {
            if (!IsServer())
            {
                return;
            }

            var normalizedVolume = Mathf.Clamp(volume, 0f, MaxVolumeMultiplier);
            lock (ServerSyncRoot)
            {
                ServerVolume = normalizedVolume;
            }

            var package = NetPackageManager.GetPackage<NetPackageBoomboxVolume>().Setup(normalizedVolume);
            SingletonMonoBehaviour<ConnectionManager>.Instance?.SendPackage(package, false, -1, -1, -1, null, -1, false);

            if (!GameManager.IsDedicatedServer)
            {
                ClientSetVolume(normalizedVolume);
            }

            Debug.Log($"[Boombox] Volume set to {normalizedVolume:0.00}");
        }

        private static bool BroadcastPlay(Vector3i position, BoomboxServerState state)
        {
            if (state != null && TryGetLocalMusicTrack(state.ClipName, out var track))
            {
                var gameManager = GameManager.Instance;
                if (gameManager != null)
                {
                    var expectedClipName = state.ClipName ?? string.Empty;
                    var expectedToken = state.PlaybackToken;
                    gameManager.StartCoroutine(BoomboxRuntimeSongManager.ServerTransferSongRoutine(
                        track.FilePath,
                        Path.GetFileNameWithoutExtension(track.FilePath),
                        new List<Vector3i> { position },
                        true,
                        expectedClipName,
                        () => IsServerPlaybackCurrent(position, expectedClipName, expectedToken)));

                    return true;
                }

                Debug.LogWarning($"[Boombox] Runtime local music transfer skipped (game manager missing) pos={position}");
            }

            var package = NetPackageManager
                .GetPackage<NetPackageBoomboxPlay>()
                .Setup(position, state.ClipName);

            SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(package, false, -1, -1, -1, null, -1, false);
            return false;
        }

        private static void BroadcastStop(Vector3i position)
        {
            var package = NetPackageManager
                .GetPackage<NetPackageBoomboxStop>()
                .Setup(position);

            SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(package, false, -1, -1, -1, null, -1, false);
        }

        private static Dictionary<string, LocalMusicTrack> LoadLocalMusicTracks()
        {
            var tracks = new Dictionary<string, LocalMusicTrack>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var musicDirectory = GetLocalMusicDirectory();
                if (!Directory.Exists(musicDirectory))
                {
                    Directory.CreateDirectory(musicDirectory);
                    Debug.Log($"[Boombox] Created local music directory: {musicDirectory}");
                    return tracks;
                }

                foreach (var filePath in Directory.GetFiles(musicDirectory, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var extension = Path.GetExtension(filePath).ToLowerInvariant();
                    if (!SupportedLocalMusicExtensions.Contains(extension))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(filePath);
                    var soundGroupName = LocalMusicSoundPrefix + ComputeStableFileId(fileName);
                    if (!tracks.ContainsKey(soundGroupName))
                    {
                        tracks.Add(soundGroupName, new LocalMusicTrack(soundGroupName, filePath));
                    }
                }

                Debug.Log($"[Boombox] Local music tracks discovered: {tracks.Count}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Boombox] Failed to load local music tracks: {ex}");
            }

            return tracks;
        }

        private static string SelectClip(World world, Vector3i position, int toggleIndex, string previousClip)
        {
            var clipNames = ClipNames;
            if (clipNames.Length == 0)
            {
                return string.Empty;
            }

            var pool = clipNames;
            if (!string.IsNullOrEmpty(previousClip) && pool.Length > 1)
            {
                pool = clipNames
                    .Where(name => !string.Equals(name, previousClip, StringComparison.Ordinal))
                    .ToArray();

                if (pool.Length == 0)
                {
                    pool = clipNames;
                }
            }

            lock (RandomSyncRoot)
            {
                var index = Random.Next(pool.Length);
                return pool[index];
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

            ClientPlayInternal(position, clipName ?? string.Empty);
        }

        public static void ClientPlayRuntime(IEnumerable<Vector3i> positions, string soundGroupName)
        {
            ClientPlayRuntime(positions, soundGroupName, 0f);
        }

        public static void ClientPlayRuntime(IEnumerable<Vector3i> positions, string soundGroupName, float startOffsetSeconds)
        {
            ClientPlayRuntime(positions, soundGroupName, startOffsetSeconds, false, string.Empty);
        }

        public static void ClientPlayRuntime(IEnumerable<Vector3i> positions, string soundGroupName, float startOffsetSeconds, bool notifyServerOnFinished, string finishedClipName)
        {
            if (!IsClient || positions == null)
            {
                return;
            }

            foreach (var position in positions)
            {
                ClientPlayInternal(position, soundGroupName ?? string.Empty, notifyServerOnFinished, startOffsetSeconds, finishedClipName);
            }
        }

        public static void ClientMarkRuntimePending(IEnumerable<Vector3i> positions, string finishedClipName)
        {
            if (!IsClient || positions == null || string.IsNullOrEmpty(finishedClipName))
            {
                return;
            }

            lock (ClientSyncRoot)
            {
                foreach (var position in positions)
                {
                    ClientStates[position] = finishedClipName;
                }
            }
        }

        public static List<Vector3i> ClientFilterRuntimePendingPositions(IEnumerable<Vector3i> positions, string finishedClipName)
        {
            var result = new List<Vector3i>();
            if (!IsClient || positions == null || string.IsNullOrEmpty(finishedClipName))
            {
                return result;
            }

            lock (ClientSyncRoot)
            {
                foreach (var position in positions)
                {
                    if (ClientStates.TryGetValue(position, out var activeClip) &&
                        string.Equals(activeClip ?? string.Empty, finishedClipName, StringComparison.Ordinal))
                    {
                        result.Add(position);
                    }
                }
            }

            return result;
        }

        public static void ClientSync(IEnumerable<BoomboxStateSnapshot> states)
        {
            if (!IsClient)
            {
                return;
            }

            StopAll();

            if (states == null)
            {
                return;
            }

            foreach (var entry in states)
            {
                ClientPlay(entry.Position, entry.ClipName);
            }
        }

        public static void ClientStop(Vector3i position)
        {
            if (!IsClient)
            {
                return;
            }

            StopInternal(position);
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

        private static void ClientPlayInternal(Vector3i position, string clipName, bool notifyServerOnFinished = true, float startOffsetSeconds = 0f, string finishedClipName = null)
        {
            var normalizedClip = clipName ?? string.Empty;
            var gameManager = GameManager.Instance;

            lock (ClientSyncRoot)
            {
                StopInternalLocked(position, gameManager);

                if (string.IsNullOrEmpty(normalizedClip))
                {
                    normalizedClip = ClipNames.FirstOrDefault() ?? string.Empty;
                }

                if (string.IsNullOrEmpty(normalizedClip))
                {
                    Debug.LogWarning("[Boombox] ClientPlayInternal aborted (no clip name available)");
                    return;
                }

                ClientStates[position] = normalizedClip;

                if (IsLocalMusicClip(normalizedClip) && !IsLocalMusicGroupRegistered(normalizedClip))
                {
                    if (gameManager == null)
                    {
                        Debug.LogWarning($"[Boombox] Local music playback skipped (game manager missing) clip='{normalizedClip}'");
                        ClientStates.Remove(position);
                        return;
                    }

                    gameManager.StartCoroutine(ClientLoadLocalMusicAndPlayRoutine(position, normalizedClip, notifyServerOnFinished));
                    return;
                }

                var handle = Manager.Play(ToWorld(position), normalizedClip, -1, true);
                ApplyStartOffset(handle, startOffsetSeconds);
                ActiveHandles[position] = handle;
                ActiveHandleBaseVolumes[position] = CaptureHandleVolumes(handle);
                ApplyVolumeToHandle(handle, ActiveHandleBaseVolumes[position], ClientVolume);

                if (notifyServerOnFinished)
                {
                    RegisterClientMonitorLocked(position, normalizedClip, handle, gameManager, finishedClipName);
                }
            }
        }

        private static IEnumerator ClientLoadLocalMusicAndPlayRoutine(Vector3i position, string soundGroupName, bool notifyServerOnFinished)
        {
            if (!TryGetLocalMusicTrack(soundGroupName, out var track))
            {
                Debug.LogWarning($"[Boombox] Local music track missing clip='{soundGroupName}'");
                yield break;
            }

            var audioType = GetAudioType(track.Extension);
            if (audioType == AudioType.UNKNOWN)
            {
                Debug.LogWarning($"[Boombox] Unsupported local music extension '{track.Extension}' path='{track.FilePath}'");
                yield break;
            }

            var uri = new Uri(track.FilePath).AbsoluteUri;
            using (var request = UnityWebRequestMultimedia.GetAudioClip(uri, audioType))
            {
                var handler = request.downloadHandler as DownloadHandlerAudioClip;
                if (handler != null)
                {
                    handler.streamAudio = false;
                    handler.compressed = false;
                }

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[Boombox] Failed to load local music '{track.FilePath}': {request.error}");
                    yield break;
                }

                var clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    Debug.LogError($"[Boombox] Local music decoded to null clip '{track.FilePath}'");
                    yield break;
                }

                RegisterLocalMusicSound(track, clip);
            }

            lock (ClientSyncRoot)
            {
                if (!ClientStates.TryGetValue(position, out var activeClip) ||
                    !string.Equals(activeClip, soundGroupName, StringComparison.OrdinalIgnoreCase))
                {
                    yield break;
                }

                var gameManager = GameManager.Instance;
                var handle = Manager.Play(ToWorld(position), soundGroupName, -1, true);
                ApplyStartOffset(handle, 0f);
                ActiveHandles[position] = handle;
                ActiveHandleBaseVolumes[position] = CaptureHandleVolumes(handle);
                ApplyVolumeToHandle(handle, ActiveHandleBaseVolumes[position], ClientVolume);

                if (notifyServerOnFinished)
                {
                    RegisterClientMonitorLocked(position, soundGroupName, handle, gameManager);
                }
            }
        }

        public static void ClientSetVolume(float volume)
        {
            if (!IsClient)
            {
                return;
            }

            lock (ClientSyncRoot)
            {
                ClientVolume = Mathf.Clamp(volume, 0f, MaxVolumeMultiplier);
                foreach (var entry in ActiveHandles)
                {
                    var baseVolumes = ActiveHandleBaseVolumes.TryGetValue(entry.Key, out var volumes)
                        ? volumes
                        : CaptureHandleVolumes(entry.Value);

                    ActiveHandleBaseVolumes[entry.Key] = baseVolumes;
                    ApplyVolumeToHandle(entry.Value, baseVolumes, ClientVolume);
                }
            }

            Debug.Log($"[Boombox] Client volume set to {ClientVolume:0.00}");
        }

        private static void StopAllInternal()
        {
            var gameManager = GameManager.Instance;

            foreach (var kvp in ActiveHandles)
            {
                StopHandle(kvp.Value);
                Manager.Stop(ToWorld(kvp.Key), ResolveSoundNameForPosition(kvp.Key));
            }

            ActiveHandles.Clear();
            ActiveHandleBaseVolumes.Clear();
            ClientStates.Clear();
            StopAllClientMonitorsLocked(gameManager);
        }

        private static void StopInternal(Vector3i position)
        {
            var gameManager = GameManager.Instance;
            lock (ClientSyncRoot)
            {
                StopInternalLocked(position, gameManager);
            }
        }

        private static void StopInternalLocked(Vector3i position, GameManager gameManager)
        {
            StopClientMonitorLocked(position, gameManager);

            if (ActiveHandles.TryGetValue(position, out var handle))
            {
                ActiveHandles.Remove(position);
                ActiveHandleBaseVolumes.Remove(position);
                StopHandle(handle);
                Manager.Stop(ToWorld(position), ResolveSoundNameForPosition(position));
            }

            ClientStates.Remove(position);
        }

        private static string ResolveSoundNameForPosition(Vector3i position)
        {
            if (ClientStates.TryGetValue(position, out var clipName) && !string.IsNullOrEmpty(clipName))
            {
                return clipName;
            }

            var fallback = ClipNames.FirstOrDefault(name => !string.IsNullOrEmpty(name));
            return fallback ?? NoiseSoundName;
        }

        private static bool IsLocalMusicClip(string soundGroupName)
        {
            return !string.IsNullOrEmpty(soundGroupName) &&
                   soundGroupName.StartsWith(LocalMusicSoundPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetLocalMusicTrack(string soundGroupName, out LocalMusicTrack track)
        {
            lock (ClipCacheSyncRoot)
            {
                if (cachedLocalMusicTracks == null)
                {
                    cachedLocalMusicTracks = LoadLocalMusicTracks();
                    cachedClipNames = cachedLocalMusicTracks.Keys
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }

                return cachedLocalMusicTracks.TryGetValue(soundGroupName ?? string.Empty, out track);
            }
        }

        private static bool IsLocalMusicGroupRegistered(string soundGroupName)
        {
            return RegisteredLocalMusicGroups.Contains(soundGroupName) ||
                   Manager.audioData != null && Manager.audioData.ContainsKey(soundGroupName);
        }

        private static void RegisterLocalMusicSound(LocalMusicTrack track, AudioClip clip)
        {
            if (track == null || clip == null)
            {
                return;
            }

            var clipName = track.SoundGroupName + "_clip";
            Manager.audioClipAssetCache[clipName] = clip;

            if (Manager.audioData == null)
            {
                Manager.Init();
            }

            if (RegisteredLocalMusicGroups.Contains(track.SoundGroupName) ||
                Manager.audioData.ContainsKey(track.SoundGroupName))
            {
                return;
            }

            var xmlData = new XmlData
            {
                soundGroupName = track.SoundGroupName,
                maxRepeatRate = 0f,
                maxVoices = 1,
                maxVoicesPerEntity = 1,
                localCrouchVolumeScale = 1f,
                crouchNoiseScale = 0.5f
            };

            xmlData.audioClipMap.Add(new ClipSourceMap
            {
                clipName = clipName,
                audioSourceName = GetAudioSourceName(),
                forceLoop = false
            });

            Manager.AddAudioData(xmlData);
            RegisteredLocalMusicGroups.Add(track.SoundGroupName);
            Debug.Log($"[Boombox] Local music registered group='{track.SoundGroupName}' file='{Path.GetFileName(track.FilePath)}'");
        }

        private static void RegisterClientMonitorLocked(Vector3i position, string clipName, Handle handle, GameManager gameManager, string finishedClipName = null)
        {
            if (GameManager.IsDedicatedServer || gameManager == null)
            {
                Debug.LogWarning("[Boombox] Client monitor skipped (server or no game manager)");
                return;
            }

            var token = 1;
            if (ClientPlaybackCoroutines.TryGetValue(position, out var existing) && existing != null)
            {
                if (existing.Coroutine != null)
                {
                    gameManager.StopCoroutine(existing.Coroutine);
                }

                token = existing.Token + 1;
            }

            var reportClipName = string.IsNullOrEmpty(finishedClipName) ? clipName : finishedClipName;
            var state = new ClientPlaybackState { Token = token };
            state.Coroutine = gameManager.StartCoroutine(ClientTrackMonitorRoutine(position, clipName, reportClipName, handle, token));
            ClientPlaybackCoroutines[position] = state;
            Debug.Log($"[Boombox] Client monitor started pos={position} clip='{clipName}' finishedClip='{reportClipName}' token={token}");
        }

        private static void StopClientMonitorLocked(Vector3i position, GameManager gameManager)
        {
            if (!ClientPlaybackCoroutines.TryGetValue(position, out var state) || state == null)
            {
                return;
            }

            if (gameManager != null && state.Coroutine != null)
            {
                gameManager.StopCoroutine(state.Coroutine);
            }

            ClientPlaybackCoroutines.Remove(position);
        }

        private static void StopAllClientMonitorsLocked(GameManager gameManager)
        {
            if (ClientPlaybackCoroutines.Count == 0)
            {
                return;
            }

            foreach (var state in ClientPlaybackCoroutines.Values)
            {
                if (state == null)
                {
                    continue;
                }

                if (gameManager != null && state.Coroutine != null)
                {
                    gameManager.StopCoroutine(state.Coroutine);
                }
            }

            ClientPlaybackCoroutines.Clear();
        }

        private static IEnumerator ClientTrackMonitorRoutine(Vector3i position, string clipName, string finishedClipName, Handle handle, int token)
        {
            const float clipResolveTimeout = 2f;
            const float fallbackDuration = 30f;
            var normalizedClip = clipName ?? string.Empty;
            var normalizedFinishedClip = string.IsNullOrEmpty(finishedClipName) ? normalizedClip : finishedClipName;
            Debug.Log($"[Boombox] Client monitor routine running pos={position} clip='{normalizedClip}' token={token}");

            var elapsed = 0f;
            AudioClip resolvedClip = null;
            while (elapsed < clipResolveTimeout)
            {
                if (!IsMonitorTokenCurrent(position, token) || !IsClipStillActive(position, normalizedClip))
                {
                    Debug.Log($"[Boombox] Client monitor exit during resolve pos={position} clip='{normalizedClip}' token={token}");
                    yield break;
                }

                resolvedClip = GetClipFromHandle(handle);
                if (resolvedClip != null)
                {
                    Debug.Log($"[Boombox] Client monitor resolved clip length={resolvedClip.length:0.00}s pos={position} token={token}");
                    break;
                }

                elapsed += Time.deltaTime;
                yield return null;
            }

            var waitDuration = resolvedClip != null && resolvedClip.length > 0f ? resolvedClip.length : fallbackDuration;
            var waitTimer = 0f;
            Debug.Log($"[Boombox] Client monitor waiting duration={waitDuration:0.00}s pos={position} clip='{normalizedClip}' token={token}");
            while (waitTimer < waitDuration)
            {
                if (!IsMonitorTokenCurrent(position, token) || !IsClipStillActive(position, normalizedClip))
                {
                    Debug.Log($"[Boombox] Client monitor exit during playback wait pos={position} clip='{normalizedClip}' token={token}");
                    yield break;
                }

                waitTimer += Time.deltaTime;
                yield return null;
            }

            var extraTimer = 0f;
            const float maxExtra = 5f;
            while (extraTimer < maxExtra)
            {
                if (!IsMonitorTokenCurrent(position, token) || !IsClipStillActive(position, normalizedClip))
                {
                    Debug.Log($"[Boombox] Client monitor exit during extra wait pos={position} clip='{normalizedClip}' token={token}");
                    yield break;
                }

                if (!IsHandlePlaying(handle))
                {
                    Debug.Log($"[Boombox] Client monitor detected handle stopped pos={position} clip='{normalizedClip}' token={token}");
                    break;
                }

                extraTimer += Time.deltaTime;
                yield return null;
            }

            if (!IsMonitorTokenCurrent(position, token) || !IsClipStillActive(position, normalizedClip))
            {
                Debug.Log($"[Boombox] Client monitor exit before notify pos={position} clip='{normalizedClip}' token={token}");
                yield break;
            }

            lock (ClientSyncRoot)
            {
                if (!ClientPlaybackCoroutines.TryGetValue(position, out var state) || state == null || state.Token != token)
                {
                    Debug.Log($"[Boombox] Client monitor missing state at notify pos={position} clip='{normalizedClip}' token={token}");
                    yield break;
                }

                ClientPlaybackCoroutines.Remove(position);
            }

            SendTrackFinishedToServer(position, normalizedFinishedClip);
        }

        private static bool IsClipStillActive(Vector3i position, string clipName)
        {
            lock (ClientSyncRoot)
            {
                return ClientStates.TryGetValue(position, out var activeClip) &&
                       string.Equals(activeClip ?? string.Empty, clipName ?? string.Empty, StringComparison.Ordinal);
            }
        }

        private static bool IsMonitorTokenCurrent(Vector3i position, int token)
        {
            lock (ClientSyncRoot)
            {
                return ClientPlaybackCoroutines.TryGetValue(position, out var state) && state != null && state.Token == token;
            }
        }

        private static bool IsServerPlaybackCurrent(Vector3i position, string clipName, int playbackToken)
        {
            lock (ServerSyncRoot)
            {
                return ServerStates.TryGetValue(position, out var state) &&
                       state != null &&
                       state.IsPlaying &&
                       state.PlaybackToken == playbackToken &&
                       string.Equals(state.ClipName ?? string.Empty, clipName ?? string.Empty, StringComparison.Ordinal);
            }
        }

        private static AudioClip GetClipFromHandle(Handle handle)
        {
            try
            {
                if (handle?.nearSource != null && handle.nearSource.clip != null)
                {
                    return handle.nearSource.clip;
                }

                if (handle?.farSource != null && handle.farSource.clip != null)
                {
                    return handle.farSource.clip;
                }
            }
            catch (MissingReferenceException)
            {
                // ignored
            }
            catch (NullReferenceException)
            {
                // ignored
            }

            return null;
        }

        private static AudioType GetAudioType(string extension)
        {
            var ext = (extension ?? string.Empty).ToLowerInvariant();
            if (ext == ".wav")
            {
                return AudioType.WAV;
            }

            if (ext == ".mp3")
            {
                return AudioType.MPEG;
            }

            return AudioType.UNKNOWN;
        }

        private static string GetLocalMusicDirectory()
        {
            return Path.Combine(GetModRootDirectory(), "Resources", "MusicToPlay");
        }

        private static string GetModRootDirectory()
        {
            var assemblyPath = typeof(BoomboxAudioManager).Assembly.Location;
            var assemblyDirectory = Path.GetDirectoryName(assemblyPath) ?? string.Empty;
            if (File.Exists(Path.Combine(assemblyDirectory, "ModInfo.xml")))
            {
                return assemblyDirectory;
            }

            var parent = Directory.GetParent(assemblyDirectory)?.FullName;
            if (!string.IsNullOrEmpty(parent) && File.Exists(Path.Combine(parent, "ModInfo.xml")))
            {
                return parent;
            }

            return assemblyDirectory;
        }

        private static string ComputeStableFileId(string value)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes((value ?? string.Empty).ToLowerInvariant());
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string GetAudioSourceName()
        {
            var existingSource = GetExistingBoomboxAudioSourceName();
            if (!string.IsNullOrEmpty(existingSource))
            {
                return existingSource;
            }

            var bundlePath = Path.Combine(GetModRootDirectory(), "Resources", "boombox.unity3d").Replace('\\', '/');
            return "#" + bundlePath + "?AudioSource_Boombox";
        }

        private static string GetExistingBoomboxAudioSourceName()
        {
            try
            {
                if (Manager.audioData == null)
                {
                    return string.Empty;
                }

                foreach (var entry in Manager.audioData)
                {
                    if (entry.Value == null ||
                        string.IsNullOrEmpty(entry.Key) ||
                        !entry.Key.StartsWith(SoundNodePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var clips = entry.Value.GetClipList();
                    if (clips == null)
                    {
                        continue;
                    }

                    foreach (var clip in clips)
                    {
                        if (clip != null && !string.IsNullOrEmpty(clip.audioSourceName))
                        {
                            return clip.audioSourceName;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Boombox] Failed to resolve existing boombox audio source: {ex}");
            }

            return string.Empty;
        }

        private static bool IsHandlePlaying(Handle handle)
        {
            try
            {
                if (handle?.nearSource != null && handle.nearSource.isPlaying)
                {
                    return true;
                }

                if (handle?.farSource != null && handle.farSource.isPlaying)
                {
                    return true;
                }
            }
            catch (MissingReferenceException)
            {
                // ignored
            }
            catch (NullReferenceException)
            {
                // ignored
            }

            return false;
        }

        private static void ApplyStartOffset(Handle handle, float offsetSeconds)
        {
            if (handle == null || offsetSeconds <= 0f)
            {
                return;
            }

            ApplyStartOffset(handle.nearSource, offsetSeconds);
            ApplyStartOffset(handle.farSource, offsetSeconds);
        }

        private static void ApplyStartOffset(AudioSource source, float offsetSeconds)
        {
            try
            {
                if (source == null || source.clip == null)
                {
                    return;
                }

                source.time = Mathf.Clamp(offsetSeconds, 0f, Math.Max(0f, source.clip.length - 0.1f));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Boombox] Failed to apply audio start offset: {ex.Message}");
            }
        }

        private static HandleVolumes CaptureHandleVolumes(Handle handle)
        {
            return new HandleVolumes
            {
                NearVolume = GetSourceVolume(handle?.nearSource),
                FarVolume = GetSourceVolume(handle?.farSource)
            };
        }

        private static float GetSourceVolume(AudioSource source)
        {
            try
            {
                return source != null ? source.volume : 1f;
            }
            catch (MissingReferenceException)
            {
                return 1f;
            }
            catch (NullReferenceException)
            {
                return 1f;
            }
        }

        private static void ApplyVolumeToHandle(Handle handle, HandleVolumes baseVolumes, float volume)
        {
            try
            {
                ApplyVolumeToSource(handle?.nearSource, baseVolumes.NearVolume, volume);
                ApplyVolumeToSource(handle?.farSource, baseVolumes.FarVolume, volume);
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

        private static void ApplyVolumeToSource(AudioSource source, float baseVolume, float volume)
        {
            if (source == null)
            {
                return;
            }

            source.volume = Mathf.Clamp(baseVolume * volume, 0f, MaxVolumeMultiplier);
        }

        private static void SendTrackFinishedToServer(Vector3i position, string clipName)
        {
            if (GameManager.IsDedicatedServer)
            {
                Debug.LogWarning("[Boombox] SendTrackFinished ignored on server instance");
                return;
            }

            var connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
            if (connection == null)
            {
                Debug.LogWarning("[Boombox] SendTrackFinished failed (no connection manager)");
                return;
            }

            var package = NetPackageManager
                .GetPackage<NetPackageBoomboxTrackFinished>()
                .Setup(position, clipName ?? string.Empty);

            connection.SendToServer(package, false);
            Debug.Log($"[Boombox] SendTrackFinished sent pos={position} clip='{clipName}'");
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

        private sealed class ClientPlaybackState
        {
            public Coroutine Coroutine;
            public int Token;
        }

        private struct HandleVolumes
        {
            public float NearVolume;
            public float FarVolume;
        }

        private sealed class LocalMusicTrack
        {
            public LocalMusicTrack(string soundGroupName, string filePath)
            {
                SoundGroupName = soundGroupName ?? string.Empty;
                FilePath = filePath ?? string.Empty;
                Extension = Path.GetExtension(FilePath).ToLowerInvariant();
            }

            public string SoundGroupName { get; }
            public string FilePath { get; }
            public string Extension { get; }
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
            public int PlaybackToken;
            public Coroutine NoiseCoroutine;
            public int LastActivatorEntityId = -1;
        }
    }
}
