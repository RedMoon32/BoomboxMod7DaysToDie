using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Boombox
{
    public static class BoomboxAudioManager
    {
        public const string DefaultMusicFolder = @"C:\BoomboxMusic";

        private sealed class AudioContext
        {
            public GameObject Host;
            public AudioSource Source;
            public Coroutine Coroutine;
            public string LastTrackPath = string.Empty;
            public Vector3i? BlockPosition;

            public bool IsValid => Host != null && Source != null;
        }

        private static readonly System.Random Random = new System.Random();
        private static readonly List<string> Tracks = new List<string>();
        private static readonly Dictionary<Vector3i, AudioContext> WorldContexts = new Dictionary<Vector3i, AudioContext>();
        private static EntityPlayerLocal _pendingPlacementPlayer;
        private static AudioContext _playerContext;
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            RefreshTrackList();
        }

        public static void PlayNext(EntityPlayerLocal player)
        {
            if (player == null)
            {
                return;
            }

            Initialize();
            RefreshTrackList();

            var context = EnsurePlayerContext(player);
            var trackPath = PickRandomTrack(context.LastTrackPath);
            if (string.IsNullOrEmpty(trackPath))
            {
                NotifyPlayer(player, $"No audio files found in {DefaultMusicFolder}. Supported: *.mp3, *.ogg, *.wav");
                return;
            }

            StartPlayback(context, trackPath, player);
        }

        public static void StopPlayer(EntityPlayerLocal player)
        {
            if (_playerContext == null)
            {
                return;
            }

            StopContext(_playerContext);
            NotifyPlayer(player, "Boombox stopped.");
        }

        public static void OnBlockPlaced(EntityPlayerLocal player, Vector3i position)
        {
            Initialize();

            Debug.Log($"[Boombox] OnBlockPlaced transfer check at {position} from {player?.EntityName ?? "unknown"}");
            var worldContext = EnsureWorldContext(position);
            worldContext.Host.transform.position = GetBlockCenter(position);

            if (_playerContext != null && _playerContext.Source != null && _playerContext.Source.clip != null)
            {
                var clip = _playerContext.Source.clip;
                var time = Mathf.Clamp(_playerContext.Source.time, 0f, clip.length);

                worldContext.Source.clip = clip;
                worldContext.Source.time = time;
                worldContext.Source.volume = GetGameVolume();
                worldContext.Source.Play();

                worldContext.LastTrackPath = _playerContext.LastTrackPath;

                StopContext(_playerContext);
                NotifyPlayer(player, $"Transferred playback to placed boombox ({clip.name}).");
            }
            else
            {
                Debug.Log("[Boombox] No handheld audio to transfer; ensuring world source idle.");
                StopContext(worldContext);
            }
        }

        public static void OnBlockRemoved(Vector3i position)
        {
            Debug.Log($"[Boombox] OnBlockRemoved at {position}");
            if (WorldContexts.TryGetValue(position, out var context))
            {
                StopContext(context);
                DestroyContext(context);
                WorldContexts.Remove(position);
            }
        }

        internal static void BeginPlacement(EntityPlayerLocal player)
        {
            Debug.Log($"[Boombox] BeginPlacement by {player?.EntityName ?? "unknown"}");
            _pendingPlacementPlayer = player;
        }

        internal static EntityPlayerLocal ConsumePendingPlacementPlayer()
        {
            var player = _pendingPlacementPlayer;
            _pendingPlacementPlayer = null;
            return player;
        }

        public static void OnBlockUnloaded(Vector3i position)
        {
            Debug.Log($"[Boombox] OnBlockUnloaded at {position}");
            if (WorldContexts.TryGetValue(position, out var context))
            {
                StopContext(context);
                DestroyContext(context);
                WorldContexts.Remove(position);
            }
        }

        public static void PlayNextAt(Vector3i position, EntityPlayerLocal player)
        {
            Initialize();
            RefreshTrackList();

            Debug.Log($"[Boombox] PlayNextAt {position}");
            var context = EnsureWorldContext(position);
            context.Host.transform.position = GetBlockCenter(position);

            var trackPath = PickRandomTrack(context.LastTrackPath);
            if (string.IsNullOrEmpty(trackPath))
            {
                NotifyPlayer(player, $"No audio files found in {DefaultMusicFolder}. Supported: *.mp3, *.ogg, *.wav");
                return;
            }

            StartPlayback(context, trackPath, player);
        }

        public static void StopAt(Vector3i position, EntityPlayerLocal player = null)
        {
            Debug.Log($"[Boombox] StopAt {position}");
            if (!WorldContexts.TryGetValue(position, out var context))
            {
                return;
            }

            StopContext(context);
            NotifyPlayer(player, "Boombox stopped.");
        }

        public static bool IsWorldPlaying(Vector3i position)
        {
            return WorldContexts.TryGetValue(position, out var context) && context.Source != null && context.Source.isPlaying;
        }

        public static void OnBlockPickedUp(EntityPlayerLocal player, Vector3i position)
        {
            Debug.Log($"[Boombox] OnBlockPickedUp at {position} by {player?.EntityName ?? "unknown"}");
            if (!WorldContexts.TryGetValue(position, out var worldContext) || worldContext.Source == null || worldContext.Source.clip == null)
            {
                return;
            }

            var clip = worldContext.Source.clip;
            var time = Mathf.Clamp(worldContext.Source.time, 0f, clip.length);
            var wasPlaying = worldContext.Source.isPlaying;
            var lastPath = worldContext.LastTrackPath;

            StopContext(worldContext);
            DestroyContext(worldContext);
            WorldContexts.Remove(position);

            var playerContext = EnsurePlayerContext(player);
            playerContext.Source.clip = clip;
            playerContext.Source.time = time;
            playerContext.Source.volume = GetGameVolume();
            playerContext.LastTrackPath = lastPath;

            if (wasPlaying)
            {
                playerContext.Source.Play();
                NotifyPlayer(player, $"Resumed {clip.name} in inventory.");
            }
        }

        private static AudioContext EnsurePlayerContext(EntityPlayerLocal player)
        {
            if (_playerContext == null || !_playerContext.IsValid)
            {
                _playerContext = CreateContext("BoomboxPlayerEmitter");
            }

            if (_playerContext.Host != null)
            {
                _playerContext.Host.transform.SetParent(player.gameObject.transform, false);
                _playerContext.Host.transform.localPosition = Vector3.zero;
            }

            return _playerContext;
        }

        private static AudioContext EnsureWorldContext(Vector3i position)
        {
            if (WorldContexts.TryGetValue(position, out var context) && context.IsValid)
            {
                return context;
            }

            context = CreateContext($"BoomboxWorldEmitter_{position.x}_{position.y}_{position.z}");
            context.Host.transform.position = GetBlockCenter(position);
            context.BlockPosition = position;

            WorldContexts[position] = context;
            return context;
        }

        private static AudioContext CreateContext(string name)
        {
            var host = new GameObject(name)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            var source = host.AddComponent<AudioSource>();
            ConfigureSource(source);

            return new AudioContext
            {
                Host = host,
                Source = source
            };
        }

        private static void ConfigureSource(AudioSource source)
        {
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f;
            source.volume = GetGameVolume();
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 2f;
            source.maxDistance = 25f;
            source.dopplerLevel = 0f;
            source.spread = 0f;
            source.mute = false;
            source.bypassListenerEffects = false;
            source.bypassReverbZones = false;
            source.spatialize = false;
            source.spread = 180f;

        }

        private static void StartPlayback(AudioContext context, string trackPath, EntityPlayerLocal notifyPlayer)
        {
            if (context == null || context.Source == null)
            {
                return;
            }

            if (context.Coroutine != null && GameManager.Instance != null)
            {
                GameManager.Instance.StopCoroutine(context.Coroutine);
                context.Coroutine = null;
            }

            context.LastTrackPath = trackPath;
            var coroutine = PlayTrackCoroutine(context, trackPath, notifyPlayer);
            context.Coroutine = GameManager.Instance != null ? GameManager.Instance.StartCoroutine(coroutine) : null;
        }

        private static IEnumerator PlayTrackCoroutine(AudioContext context, string trackPath, EntityPlayerLocal notifyPlayer)
        {
            using var request = UnityWebRequestMultimedia.GetAudioClip(BuildFileUri(trackPath), GetAudioType(trackPath));
            yield return request.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
            var failed = request.result != UnityWebRequest.Result.Success;
#else
            var failed = request.isNetworkError || request.isHttpError;
#endif
            if (failed)
            {
                Debug.LogWarning($"[Boombox] Failed to load {trackPath}: {request.error}");
                NotifyPlayer(notifyPlayer, $"Failed to load {Path.GetFileName(trackPath)}");
                context.Coroutine = null;
                yield break;
            }

            var clip = DownloadHandlerAudioClip.GetContent(request);
            if (clip == null || clip.length <= 0f)
            {
                Debug.LogWarning($"[Boombox] Loaded clip is empty: {trackPath}");
                NotifyPlayer(notifyPlayer, $"File has no audio data: {Path.GetFileName(trackPath)}");
                context.Coroutine = null;
                yield break;
            }

            clip.name = Path.GetFileName(trackPath);

            context.Source.clip = clip;
            context.Source.time = 0f;
            context.Source.volume = GetGameVolume();
            context.Source.mute = false;
            context.Source.Play();

            Debug.Log($"[Boombox] Playing '{clip.name}' length={clip.length:F2}s freq={clip.frequency}Hz channels={clip.channels} volume={context.Source.volume:F2}");
            NotifyPlayer(notifyPlayer, $"Playing {clip.name} [{clip.frequency}Hz, {clip.channels}ch, {clip.length:0.0}s]");

            context.Coroutine = null;
        }

        private static void StopContext(AudioContext context)
        {
            if (context == null)
            {
                return;
            }

            if (context.Coroutine != null && GameManager.Instance != null)
            {
                GameManager.Instance.StopCoroutine(context.Coroutine);
                context.Coroutine = null;
            }

            if (context.Source != null)
            {
                context.Source.Stop();
                context.Source.clip = null;
            }
        }

        private static void DestroyContext(AudioContext context)
        {
            if (context?.Host != null)
            {
                UnityEngine.Object.Destroy(context.Host);
                context.Host = null;
            }

            if (context != null)
            {
                context.Source = null;
                context.Coroutine = null;
                context.LastTrackPath = string.Empty;
            }
        }

        private static string PickRandomTrack(string lastTrackPath)
        {
            if (Tracks.Count == 0)
            {
                RefreshTrackList();
            }

            if (Tracks.Count == 0)
            {
                return string.Empty;
            }

            if (Tracks.Count == 1)
            {
                return Tracks[0];
            }

            string candidate;
            var attempts = 0;
            do
            {
                candidate = Tracks[Random.Next(Tracks.Count)];
                attempts++;
            }
            while (candidate.Equals(lastTrackPath, StringComparison.OrdinalIgnoreCase) && attempts < 10);

            return candidate;
        }

        private static void RefreshTrackList()
        {
            Tracks.Clear();
            try
            {
                if (!Directory.Exists(DefaultMusicFolder))
                {
                    Directory.CreateDirectory(DefaultMusicFolder);
                    Debug.Log($"[Boombox] Created music folder at {DefaultMusicFolder}");
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(DefaultMusicFolder))
                {
                    var extension = Path.GetExtension(file);
                    if (IsSupportedExtension(extension))
                    {
                        Tracks.Add(file);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Boombox] Failed to enumerate tracks: {ex.Message}");
            }
        }

        private static bool IsSupportedExtension(string extension)
        {
            return extension != null && (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
                                         || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                                         || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase));
        }

        private static float GetGameVolume()
        {
            float overall = GamePrefs.GetFloat(EnumGamePrefs.OptionsOverallAudioVolumeLevel);
            float music = GamePrefs.GetFloat(EnumGamePrefs.OptionsMusicVolumeLevel);
            float ambient = GamePrefs.GetFloat(EnumGamePrefs.OptionsAmbientVolumeLevel);

            if (overall <= 0f)
            {
                overall = 1f;
            }

            if (music < 0f)
            {
                music = 0f;
            }

            if (ambient < 0f)
            {
                ambient = 0f;
            }

            float volume = overall;
            if (music > 0f)
            {
                volume *= music;
            }
            else if (ambient > 0f)
            {
                volume *= ambient;
            }

            volume = Mathf.Clamp(volume, 0f, 1f);
            if (volume <= 0f)
            {
                volume = 1f;
            }

            return volume;
        }

        private static string BuildFileUri(string path)
        {
            return "file:///" + path.Replace("\\", "/");
        }

        private static AudioType GetAudioType(string trackPath)
        {
            var extension = Path.GetExtension(trackPath)?.ToLowerInvariant();
            return extension switch
            {
                ".mp3" => AudioType.MPEG,
                ".ogg" => AudioType.OGGVORBIS,
                ".wav" => AudioType.WAV,
                _ => AudioType.UNKNOWN
            };
        }

        private static Vector3 GetBlockCenter(Vector3i position)
        {
            return new Vector3(position.x + 0.5f, position.y + 0.5f, position.z + 0.5f);
        }

        private static void NotifyPlayer(EntityPlayerLocal player, string message)
        {
            if (player == null)
            {
                return;
            }

            GameManager.ShowTooltip(player, message, false, false, 2f);
        }
    }
}
