using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Audio;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace Boombox
{
    public static class BoomboxRuntimeSongManager
    {
        private const string ChatPrefix = "PLAY ";
        private const string YoutubeChatPrefix = "PLAYU ";
        private const string VolumeChatPrefix = "BVOL ";
        private const string SearchChatPrefix = "SEARCH ";
        private const string PlayNumberChatPrefix = "PLAYNUM ";
        private const int ChunkSize = 32 * 1024;
        private const int ChunksPerFrame = 6;
        private const int SearchResultLimit = 10;

        private static readonly Dictionary<string, ClientTransfer> ClientTransfers = new Dictionary<string, ClientTransfer>();
        private static readonly HashSet<string> RegisteredSoundGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SearchSession> SearchSessions = new Dictionary<string, SearchSession>();

        public static ModEvents.EModEventResult ServerHandleChatMessage(ref ModEvents.SChatMessageData data)
        {
            var message = data.Message ?? string.Empty;
            if (message.StartsWith(VolumeChatPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return ServerHandleVolumeChatMessage(message.Substring(VolumeChatPrefix.Length).Trim());
            }

            if (message.StartsWith(SearchChatPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return ServerHandleSearchChatMessage(message.Substring(SearchChatPrefix.Length).Trim(), ref data);
            }

            if (message.StartsWith(PlayNumberChatPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return ServerHandlePlayNumberChatMessage(message.Substring(PlayNumberChatPrefix.Length).Trim(), ref data);
            }

            if (message.StartsWith(YoutubeChatPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return ServerHandleYoutubeChatMessage(message.Substring(YoutubeChatPrefix.Length).Trim());
            }

            if (!message.StartsWith(ChatPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return ModEvents.EModEventResult.Continue;
            }

            if (!IsServer())
            {
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            if (!TryGetServerPlaybackContext("PLAY", out var positions, out var gameManager))
            {
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            var songName = message.Substring(ChatPrefix.Length).Trim();
            if (!TryResolveSongPath(songName, out var songPath, out var normalizedName))
            {
                Debug.LogWarning($"[Boombox] PLAY ignored (song not found or invalid): '{songName}'");
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            gameManager.StartCoroutine(ServerTransferSongRoutine(songPath, normalizedName, positions));
            return ModEvents.EModEventResult.StopHandlersAndVanilla;
        }

        private static ModEvents.EModEventResult ServerHandleVolumeChatMessage(string value)
        {
            if (!IsServer())
            {
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            if (!TryParseVolume(value, out var volume))
            {
                Debug.LogWarning($"[Boombox] BVOL ignored (expected 0..5 or 0..500): '{value}'");
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            BoomboxAudioManager.ServerSetVolume(volume);
            return ModEvents.EModEventResult.StopHandlersAndVanilla;
        }

        private static ModEvents.EModEventResult ServerHandleSearchChatMessage(string query, ref ModEvents.SChatMessageData data)
        {
            if (!IsServer())
            {
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                SendChatReply(data.ClientInfo, "Usage: SEARCH <query>");
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            var gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                Debug.LogWarning("[Boombox] SEARCH ignored (game manager missing)");
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            gameManager.StartCoroutine(ServerSearchRoutine(query, GetSearchSessionKey(ref data), data.ClientInfo));
            return ModEvents.EModEventResult.StopHandlersAndVanilla;
        }

        private static ModEvents.EModEventResult ServerHandlePlayNumberChatMessage(string value, ref ModEvents.SChatMessageData data)
        {
            if (!IsServer())
            {
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            if (!int.TryParse(value.Trim(), out var number) || number < 1)
            {
                SendChatReply(data.ClientInfo, "Usage: PLAYNUM <number>");
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            if (!TryGetServerPlaybackContext("PLAYNUM", out var positions, out var gameManager))
            {
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            var sessionKey = GetSearchSessionKey(ref data);
            if (!SearchSessions.TryGetValue(sessionKey, out var session) || session.Items.Count == 0)
            {
                SendChatReply(data.ClientInfo, "No SEARCH results cached. Use SEARCH <query> first.");
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            if (number > session.Items.Count)
            {
                SendChatReply(data.ClientInfo, $"PLAYNUM {number} is out of range. Last SEARCH has {session.Items.Count} result(s).");
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            gameManager.StartCoroutine(ServerDownloadSearchResultAndTransferRoutine(session.Items[number - 1], positions, data.ClientInfo));
            return ModEvents.EModEventResult.StopHandlersAndVanilla;
        }

        private static ModEvents.EModEventResult ServerHandleYoutubeChatMessage(string query)
        {
            if (!IsServer())
            {
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                Debug.LogWarning("[Boombox] PLAYU ignored (empty query)");
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            if (!TryGetServerPlaybackContext("PLAYU", out var positions, out var gameManager))
            {
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            gameManager.StartCoroutine(ServerDownloadAndTransferRoutine(query, positions));
            return ModEvents.EModEventResult.StopHandlersAndVanilla;
        }

        private static bool TryGetServerPlaybackContext(string commandName, out List<Vector3i> positions, out GameManager gameManager)
        {
            positions = null;
            gameManager = null;

            if (!IsServer())
            {
                return false;
            }

            var world = GameManager.Instance?.World;
            if (world == null)
            {
                Debug.LogWarning($"[Boombox] {commandName} ignored (world missing)");
                return false;
            }

            positions = BoomboxAudioManager.GetKnownBoomboxPositions(world);
            if (positions.Count == 0)
            {
                Debug.LogWarning($"[Boombox] {commandName} ignored (no known boombox positions)");
                return false;
            }

            gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                Debug.LogWarning($"[Boombox] {commandName} ignored (game manager missing)");
                return false;
            }

            return true;
        }

        private static IEnumerator ServerDownloadAndTransferRoutine(string query, List<Vector3i> positions)
        {
            var downloader = CreateDefaultMusicDownloader();
            var result = new MusicDownloadResult();
            yield return downloader.DownloadByQuery(query, result);

            if (!result.Success)
            {
                Debug.LogWarning($"[Boombox] PLAYU download failed downloader='{downloader.Name}' exit={result.ExitCode} query='{query}' error='{result.Error}' output='{Truncate(result.DiagnosticOutput, 2000)}'");
                yield break;
            }

            yield return ServerTransferSongRoutine(result.FilePath, query, positions);
        }

        private static IEnumerator ServerSearchRoutine(string query, string sessionKey, ClientInfo clientInfo)
        {
            var downloader = CreateDefaultMusicDownloader();
            var result = new MusicSearchResult();
            Debug.Log($"[Boombox] SEARCH started downloader='{downloader.Name}' query='{query}'");
            SendChatReply(clientInfo, $"Searching {downloader.Name}: {query}");

            yield return downloader.SearchByQuery(query, SearchResultLimit, result);

            if (!result.Success)
            {
                Debug.LogWarning($"[Boombox] SEARCH failed downloader='{downloader.Name}' exit={result.ExitCode} query='{query}' error='{result.Error}' output='{Truncate(result.DiagnosticOutput, 1000)}'");
                SendChatReply(clientInfo, $"Search failed: {result.Error}");
                yield break;
            }

            SearchSessions[sessionKey] = new SearchSession(query, downloader.Name, result.Items);
            SendSearchResults(clientInfo, query, result.Items);
            Debug.Log($"[Boombox] SEARCH completed downloader='{downloader.Name}' query='{query}' results={result.Items.Count}");
        }

        private static IEnumerator ServerDownloadSearchResultAndTransferRoutine(MusicSearchItem item, List<Vector3i> positions, ClientInfo clientInfo)
        {
            var downloader = CreateMusicDownloader(item.Source);
            var result = new MusicDownloadResult();
            SendChatReply(clientInfo, $"Downloading #{item.DisplayName}");
            yield return downloader.DownloadSearchResult(item, result);

            if (!result.Success)
            {
                Debug.LogWarning($"[Boombox] PLAYNUM download failed downloader='{downloader.Name}' exit={result.ExitCode} item='{item.DisplayName}' error='{result.Error}' output='{Truncate(result.DiagnosticOutput, 1000)}'");
                SendChatReply(clientInfo, $"Download failed: {result.Error}");
                yield break;
            }

            yield return ServerTransferSongRoutine(result.FilePath, item.DisplayName, positions);
        }

        private static IEnumerator ServerTransferSongRoutine(string songPath, string songName, List<Vector3i> positions)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(songPath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Boombox] Failed to read song '{songPath}': {ex}");
                yield break;
            }

            var songId = ComputeSha256(bytes);
            var extension = Path.GetExtension(songPath).ToLowerInvariant();
            var start = NetPackageManager
                .GetPackage<NetPackageBoomboxSongStart>()
                .Setup(songId, songName, extension, bytes.Length, positions);

            BroadcastToClients(start);
            if (!GameManager.IsDedicatedServer)
            {
                ClientReceiveSongStart(songId, songName, extension, bytes.Length, positions);
            }

            var chunkIndex = 0;
            var sentThisFrame = 0;
            for (var offset = 0; offset < bytes.Length; offset += ChunkSize)
            {
                var count = Math.Min(ChunkSize, bytes.Length - offset);
                var chunk = new byte[count];
                Buffer.BlockCopy(bytes, offset, chunk, 0, count);

                var package = NetPackageManager
                    .GetPackage<NetPackageBoomboxSongChunk>()
                    .Setup(songId, chunkIndex, chunk);

                BroadcastToClients(package);
                if (!GameManager.IsDedicatedServer)
                {
                    ClientReceiveSongChunk(songId, chunkIndex, chunk);
                }

                chunkIndex++;
                sentThisFrame++;
                if (sentThisFrame >= ChunksPerFrame)
                {
                    sentThisFrame = 0;
                    yield return null;
                }
            }

            var complete = NetPackageManager
                .GetPackage<NetPackageBoomboxSongComplete>()
                .Setup(songId);

            BroadcastToClients(complete);
            if (!GameManager.IsDedicatedServer)
            {
                ClientReceiveSongComplete(songId);
            }

            Debug.Log($"[Boombox] Runtime song transfer queued song='{songName}' bytes={bytes.Length} chunks={chunkIndex} positions={positions.Count}");
        }

        public static void ClientReceiveSongStart(string songId, string songName, string extension, long totalBytes, List<Vector3i> positions)
        {
            if (GameManager.IsDedicatedServer || string.IsNullOrEmpty(songId))
            {
                return;
            }

            var cacheDir = GetCacheDirectory();
            Directory.CreateDirectory(cacheDir);

            var safeExtension = NormalizeExtension(extension);
            var finalPath = Path.Combine(cacheDir, songId + safeExtension);
            var tempPath = finalPath + ".part";

            CloseTransfer(songId);
            var transfer = new ClientTransfer
            {
                SongId = songId,
                SongName = songName ?? string.Empty,
                Extension = safeExtension,
                TotalBytes = totalBytes,
                ReceivedBytes = 0,
                FinalPath = finalPath,
                TempPath = tempPath,
                Positions = positions ?? new List<Vector3i>(),
                Stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)
            };

            ClientTransfers[songId] = transfer;
            Debug.Log($"[Boombox] Receiving runtime song '{transfer.SongName}' id={songId} bytes={totalBytes}");
        }

        public static void ClientReceiveSongChunk(string songId, int chunkIndex, byte[] bytes)
        {
            if (GameManager.IsDedicatedServer || string.IsNullOrEmpty(songId) || bytes == null)
            {
                return;
            }

            if (!ClientTransfers.TryGetValue(songId, out var transfer) || transfer?.Stream == null)
            {
                Debug.LogWarning($"[Boombox] Song chunk ignored (missing transfer) id={songId} chunk={chunkIndex}");
                return;
            }

            try
            {
                transfer.Stream.Write(bytes, 0, bytes.Length);
                transfer.ReceivedBytes += bytes.Length;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Boombox] Failed to write song chunk id={songId}: {ex}");
                CloseTransfer(songId);
            }
        }

        public static void ClientReceiveSongComplete(string songId)
        {
            if (GameManager.IsDedicatedServer || string.IsNullOrEmpty(songId))
            {
                return;
            }

            if (!ClientTransfers.TryGetValue(songId, out var transfer) || transfer == null)
            {
                Debug.LogWarning($"[Boombox] Song complete ignored (missing transfer) id={songId}");
                return;
            }

            try
            {
                transfer.Stream?.Dispose();
                transfer.Stream = null;

                if (transfer.TotalBytes >= 0 && transfer.ReceivedBytes != transfer.TotalBytes)
                {
                    Debug.LogWarning($"[Boombox] Song transfer size mismatch id={songId} expected={transfer.TotalBytes} got={transfer.ReceivedBytes}");
                    ClientTransfers.Remove(songId);
                    return;
                }

                if (File.Exists(transfer.FinalPath))
                {
                    File.Delete(transfer.FinalPath);
                }

                File.Move(transfer.TempPath, transfer.FinalPath);
                ClientTransfers.Remove(songId);

                var gameManager = GameManager.Instance;
                if (gameManager != null)
                {
                    gameManager.StartCoroutine(ClientLoadAndPlayRoutine(transfer));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Boombox] Failed to complete song transfer id={songId}: {ex}");
                CloseTransfer(songId);
            }
        }

        private static IEnumerator ClientLoadAndPlayRoutine(ClientTransfer transfer)
        {
            var audioType = GetAudioType(transfer.Extension);
            if (audioType == AudioType.UNKNOWN)
            {
                Debug.LogWarning($"[Boombox] Unsupported song extension '{transfer.Extension}'");
                yield break;
            }

            var uri = new Uri(transfer.FinalPath).AbsoluteUri;
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
                    Debug.LogError($"[Boombox] Failed to load runtime song '{transfer.FinalPath}': {request.error}");
                    yield break;
                }

                var clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    Debug.LogError($"[Boombox] Runtime song decoded to null clip '{transfer.FinalPath}'");
                    yield break;
                }

                var soundGroupName = RegisterRuntimeSound(transfer.SongId, clip);
                BoomboxAudioManager.ClientPlayRuntime(transfer.Positions, soundGroupName);
                Debug.Log($"[Boombox] Runtime song playing group='{soundGroupName}' positions={transfer.Positions.Count} length={clip.length:0.00}s");
            }
        }

        private static string RegisterRuntimeSound(string songId, AudioClip clip)
        {
            var soundGroupName = "boombox_runtime_" + songId.Substring(0, Math.Min(16, songId.Length));
            var clipName = soundGroupName + "_clip";

            Manager.audioClipAssetCache[clipName] = clip;

            if (Manager.audioData == null)
            {
                Manager.Init();
            }

            if (RegisteredSoundGroups.Contains(soundGroupName) || Manager.audioData.ContainsKey(soundGroupName))
            {
                return soundGroupName;
            }

            var xmlData = new XmlData
            {
                soundGroupName = soundGroupName,
                maxRepeatRate = 0f,
                maxVoices = 64,
                maxVoicesPerEntity = 64,
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
            RegisteredSoundGroups.Add(soundGroupName);
            return soundGroupName;
        }

        private static bool TryResolveSongPath(string requestedName, out string path, out string normalizedName)
        {
            path = null;
            normalizedName = null;

            if (string.IsNullOrWhiteSpace(requestedName))
            {
                return false;
            }

            var fileName = Path.GetFileName(requestedName.Trim());
            if (!string.Equals(fileName, requestedName.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            var root = GetModRootDirectory();
            var codeDir = Path.Combine(root, "Code");
            var extension = Path.GetExtension(fileName);
            var candidates = string.IsNullOrEmpty(extension)
                ? new[] { Path.Combine(codeDir, fileName + ".wav"), Path.Combine(codeDir, fileName + ".mp3") }
                : new[] { Path.Combine(codeDir, fileName) };

            foreach (var candidate in candidates)
            {
                var ext = Path.GetExtension(candidate).ToLowerInvariant();
                if ((ext == ".wav" || ext == ".mp3") && File.Exists(candidate))
                {
                    path = candidate;
                    normalizedName = Path.GetFileNameWithoutExtension(candidate);
                    return true;
                }
            }

            return false;
        }

        private static void BroadcastToClients(NetPackage package)
        {
            var connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
            connection?.SendPackage(package, false, -1, -1, -1, null, -1, false);
        }

        private static bool IsServer()
        {
            var connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
            return GameManager.IsDedicatedServer || connection != null && connection.IsServer;
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string GetModRootDirectory()
        {
            var assemblyPath = typeof(BoomboxRuntimeSongManager).Assembly.Location;
            return Path.GetDirectoryName(assemblyPath) ?? string.Empty;
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
                        !entry.Key.StartsWith("boombox_music_track", StringComparison.OrdinalIgnoreCase))
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

        private static string GetCacheDirectory()
        {
            return Path.Combine(GetModRootDirectory(), "Cache");
        }

        private static string NormalizeExtension(string extension)
        {
            var ext = (extension ?? string.Empty).ToLowerInvariant();
            return ext == ".mp3" ? ".mp3" : ".wav";
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

        private static bool TryParseVolume(string value, out float volume)
        {
            volume = 1f;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim().Replace(',', '.');
            if (!float.TryParse(normalized, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            if (parsed > 5f && parsed <= 500f)
            {
                parsed /= 100f;
            }

            if (parsed < 0f || parsed > 5f)
            {
                return false;
            }

            volume = parsed;
            return true;
        }

        private static IMusicDownloader CreateDefaultMusicDownloader()
        {
            return new HitmozMusicDownloader(GetModRootDirectory());
        }

        private static IMusicDownloader CreateMusicDownloader(string source)
        {
            if (string.Equals(source, "yt-dlp", StringComparison.OrdinalIgnoreCase))
            {
                return new YtDlpMusicDownloader(GetModRootDirectory());
            }

            return new HitmozMusicDownloader(GetModRootDirectory());
        }

        private static string GetSearchSessionKey(ref ModEvents.SChatMessageData data)
        {
            if (data.ClientInfo != null)
            {
                return data.ClientInfo.ToString();
            }

            return "entity:" + data.SenderEntityId;
        }

        private static void SendSearchResults(ClientInfo clientInfo, string query, List<MusicSearchItem> items)
        {
            if (items == null || items.Count == 0)
            {
                SendChatReply(clientInfo, $"No results for: {query}");
                return;
            }

            SendChatReply(clientInfo, $"Results for '{query}' ({items.Count}). Use PLAYNUM <n>:");
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var duration = string.IsNullOrEmpty(item.Duration) ? string.Empty : " [" + item.Duration + "]";
                SendChatReply(clientInfo, $"{i + 1}. {item.DisplayName}{duration}");
            }
        }

        private static void SendChatReply(ClientInfo clientInfo, string message)
        {
            var text = "[Boombox] " + (message ?? string.Empty);
            Debug.Log(text);

            if (clientInfo == null)
            {
                return;
            }

            try
            {
                clientInfo.SendPackage(NetPackageManager.GetPackage<NetPackageSimpleChat>().Setup(text));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Boombox] Failed to send chat reply: {ex}");
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(value.Length - maxLength, maxLength);
        }

        private static void CloseTransfer(string songId)
        {
            if (!ClientTransfers.TryGetValue(songId, out var transfer) || transfer == null)
            {
                return;
            }

            try
            {
                transfer.Stream?.Dispose();
            }
            catch
            {
                // ignored
            }

            ClientTransfers.Remove(songId);
        }

        private sealed class ClientTransfer
        {
            public string SongId;
            public string SongName;
            public string Extension;
            public long TotalBytes;
            public long ReceivedBytes;
            public string FinalPath;
            public string TempPath;
            public List<Vector3i> Positions;
            public FileStream Stream;
        }

        private sealed class SearchSession
        {
            public SearchSession(string query, string source, IEnumerable<MusicSearchItem> items)
            {
                Query = query ?? string.Empty;
                Source = source ?? string.Empty;
                Items = items?.ToList() ?? new List<MusicSearchItem>();
            }

            public string Query { get; }
            public string Source { get; }
            public List<MusicSearchItem> Items { get; }
        }

    }
}
