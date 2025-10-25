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
        public const string DefaultMusicFolder = @"C:\\BoomboxMusic";

        private static readonly System.Random Random = new System.Random();
        private static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3",
            ".ogg",
            ".wav"
        };

        private static readonly List<string> Tracks = new List<string>();

        private static bool _initialized;
        private static AudioSource _audioSource;
        private static GameObject _audioObject;
        private static Coroutine _currentCoroutine;
        private static string _lastTrack = string.Empty;
        private static bool _isLoading;

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            RefreshTrackList();
        }

        public static void PlayRandomTrack(EntityPlayerLocal player)
        {
            if (player == null)
            {
                return;
            }

            Initialize();
            EnsureAudioSource(player);
            RefreshTrackList();

            if (_isLoading)
            {
                NotifyPlayer(player, "Boombox is loading audio, please wait...");
                return;
            }

            if (Tracks.Count == 0)
            {
                NotifyPlayer(player, $"No audio files found in {DefaultMusicFolder}. Supported: *.mp3, *.ogg, *.wav");
                return;
            }

            var trackPath = PickRandomTrack();
            if (string.IsNullOrEmpty(trackPath))
            {
                NotifyPlayer(player, "Unable to select a track.");
                return;
            }

            if (_audioSource.isPlaying)
            {
                _audioSource.Stop();
            }

            var manager = GameManager.Instance;
            if (manager == null)
            {
                Debug.LogWarning("[Boombox] GameManager instance is unavailable; cannot play audio.");
                return;
            }

            if (_currentCoroutine != null)
            {
                manager.StopCoroutine(_currentCoroutine);
                _currentCoroutine = null;
            }

            _currentCoroutine = manager.StartCoroutine(PlayTrackCoroutine(player, trackPath));
        }

        private static void EnsureAudioSource(EntityPlayerLocal player)
        {
            if (_audioSource != null)
            {
                return;
            }

            var holder = new GameObject("BoomboxAudioEmitter")
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            holder.transform.SetParent(player.gameObject.transform, false);
            holder.transform.localPosition = Vector3.zero;

            var listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
            Debug.Log($"[Boombox] AudioListener present: {listener != null}");

            _audioObject = holder;
            _audioSource = holder.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.loop = false;
            _audioSource.spatialBlend = 0f;
            _audioSource.ignoreListenerPause = true;
            _audioSource.bypassListenerEffects = true;
            _audioSource.bypassReverbZones = true;
            _audioSource.volume = GetGameVolume();
            _audioSource.mute = false;
            _audioSource.dopplerLevel = 0f;
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
                    if (SupportedExtensions.Contains(extension))
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

        private static string PickRandomTrack()
        {
            if (Tracks.Count == 0)
            {
                return string.Empty;
            }

            if (Tracks.Count == 1)
            {
                _lastTrack = Tracks[0];
                return Tracks[0];
            }

            string candidate;
            var attempts = 0;
            do
            {
                candidate = Tracks[Random.Next(Tracks.Count)];
                attempts++;
            } while (candidate.Equals(_lastTrack, StringComparison.OrdinalIgnoreCase) && attempts < 5);

            _lastTrack = candidate;
            return candidate;
        }

        private static IEnumerator PlayTrackCoroutine(EntityPlayerLocal player, string trackPath)
        {
            _isLoading = true;
            var audioType = GetAudioType(trackPath);
            if (audioType == AudioType.UNKNOWN)
            {
                NotifyPlayer(player, $"Unsupported audio format: {Path.GetFileName(trackPath)}");
                _isLoading = false;
                yield break;
            }

            var uri = "file:///" + trackPath.Replace("\\", "/");
            using var request = UnityWebRequestMultimedia.GetAudioClip(uri, audioType);
            yield return request.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
            var failed = request.result != UnityWebRequest.Result.Success;
#else
#pragma warning disable 618
            var failed = request.isNetworkError || request.isHttpError;
#pragma warning restore 618
#endif
            if (failed)
            {
                NotifyPlayer(player, $"Failed to load {Path.GetFileName(trackPath)}: {request.error}");
                Debug.LogWarning($"[Boombox] UnityWebRequest failure: {request.error}");
                _isLoading = false;
                yield break;
            }

            var clip = DownloadHandlerAudioClip.GetContent(request);
            if (clip == null || clip.length <= 0f)
            {
                NotifyPlayer(player, $"Loaded clip but it is empty: {Path.GetFileName(trackPath)}");
                Debug.LogWarning($"[Boombox] Clip empty or null. length={clip?.length}");
                _isLoading = false;
                yield break;
            }

            if (clip.loadState == AudioDataLoadState.Unloaded)
            {
                clip.LoadAudioData();
                while (clip.loadState == AudioDataLoadState.Loading)
                {
                    yield return null;
                }
            }

            clip.name = Path.GetFileName(trackPath);

            _audioSource.clip = clip;
            _audioSource.volume = GetGameVolume();
            _audioSource.mute = false;
            _audioSource.Play();

            Debug.Log($"[Boombox] Playing '{clip.name}' length={clip.length:F2}s freq={clip.frequency}Hz channels={clip.channels} volume={_audioSource.volume:F2}");
            NotifyPlayer(player, $"Playing {clip.name} [{clip.frequency}Hz, {clip.channels}ch, {clip.length:0.0}s]");

            _isLoading = false;
            _currentCoroutine = null;
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

            Debug.Log($"[Boombox] Volume overall={overall:F2} music={music:F2} ambient={ambient:F2} -> {volume:F2}");
            return volume;
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
