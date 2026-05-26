using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
        private const int ChunkSize = 32 * 1024;
        private const int ChunksPerFrame = 6;
        private const int YoutubeDownloadTimeoutSeconds = 300;

        private static readonly Dictionary<string, ClientTransfer> ClientTransfers = new Dictionary<string, ClientTransfer>();
        private static readonly HashSet<string> RegisteredSoundGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static ModEvents.EModEventResult ServerHandleChatMessage(ref ModEvents.SChatMessageData data)
        {
            var message = data.Message ?? string.Empty;
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

            var songName = message.Substring(ChatPrefix.Length).Trim();
            var world = GameManager.Instance?.World;
            if (world == null)
            {
                Debug.LogWarning("[Boombox] PLAY ignored (world missing)");
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            if (!TryResolveSongPath(songName, out var songPath, out var normalizedName))
            {
                Debug.LogWarning($"[Boombox] PLAY ignored (song not found or invalid): '{songName}'");
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            var positions = BoomboxAudioManager.GetKnownBoomboxPositions(world);
            if (positions.Count == 0)
            {
                Debug.LogWarning($"[Boombox] PLAY ignored (no known boombox positions): '{normalizedName}'");
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            var gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                Debug.LogWarning("[Boombox] PLAY ignored (game manager missing)");
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            gameManager.StartCoroutine(ServerTransferSongRoutine(songPath, normalizedName, positions));
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

            var world = GameManager.Instance?.World;
            if (world == null)
            {
                Debug.LogWarning("[Boombox] PLAYU ignored (world missing)");
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            var positions = BoomboxAudioManager.GetKnownBoomboxPositions(world);
            if (positions.Count == 0)
            {
                Debug.LogWarning($"[Boombox] PLAYU ignored (no known boombox positions): '{query}'");
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            var gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                Debug.LogWarning("[Boombox] PLAYU ignored (game manager missing)");
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }

            gameManager.StartCoroutine(ServerDownloadYoutubeAndTransferRoutine(query, positions));
            return ModEvents.EModEventResult.StopHandlersAndVanilla;
        }

        private static IEnumerator ServerDownloadYoutubeAndTransferRoutine(string query, List<Vector3i> positions)
        {
            var toolsDir = Path.Combine(GetModRootDirectory(), "YtdlpDownloads", "bin");
            var ytDlpPath = Path.Combine(toolsDir, "yt-dlp.exe");
            var ffmpegPath = Path.Combine(toolsDir, "ffmpeg.exe");
            if (!File.Exists(ytDlpPath) || !File.Exists(ffmpegPath))
            {
                Debug.LogWarning($"[Boombox] PLAYU ignored (yt-dlp/ffmpeg missing): '{toolsDir}'");
                yield break;
            }

            var downloadDir = Path.Combine(GetModRootDirectory(), "YtdlpDownloads", "audio");
            Directory.CreateDirectory(downloadDir);

            var fileBaseName = "playu_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var outputTemplate = fileBaseName + ".%(ext)s";
            var arguments = BuildYoutubeDownloadArguments(query, toolsDir, downloadDir, outputTemplate);

            Debug.Log($"[Boombox] PLAYU download queued query='{query}'");
            var process = StartProcess(ytDlpPath, arguments, out var output);
            if (process == null)
            {
                yield break;
            }

            var startTime = Time.realtimeSinceStartup;
            while (!process.HasExited)
            {
                if (Time.realtimeSinceStartup - startTime > YoutubeDownloadTimeoutSeconds)
                {
                    TryKill(process);
                    Debug.LogWarning($"[Boombox] PLAYU download timed out query='{query}' output='{Truncate(output.ToString(), 1000)}'");
                    yield break;
                }

                yield return null;
            }

            process.WaitForExit();
            var exitCode = process.ExitCode;
            process.Dispose();

            if (exitCode != 0)
            {
                Debug.LogWarning($"[Boombox] PLAYU download failed exit={exitCode} query='{query}' output='{Truncate(output.ToString(), 2000)}'");
                yield break;
            }

            var mp3Path = Path.Combine(downloadDir, fileBaseName + ".mp3");
            if (!File.Exists(mp3Path))
            {
                Debug.LogWarning($"[Boombox] PLAYU download produced no mp3 query='{query}' output='{Truncate(output.ToString(), 2000)}'");
                yield break;
            }

            yield return ServerTransferSongRoutine(mp3Path, query, positions);
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

        private static string BuildYoutubeDownloadArguments(string query, string toolsDir, string downloadDir, string outputTemplate)
        {
            var args = new List<string>
            {
                "--js-runtimes",
                "node",
                "--ffmpeg-location",
                toolsDir,
                "-x",
                "--audio-format",
                "mp3",
                "--audio-quality",
                "0",
                "--no-playlist",
                "--no-part",
                "--force-overwrites",
                "-P",
                downloadDir,
                "-o",
                outputTemplate
            };

            var cookiesPath = GetYoutubeCookiesPath();
            if (!string.IsNullOrEmpty(cookiesPath))
            {
                args.Add("--cookies");
                args.Add(cookiesPath);
            }

            args.Add("ytsearch1:" + query);
            return string.Join(" ", args.Select(QuoteArgument).ToArray());
        }

        private static string GetYoutubeCookiesPath()
        {
            var envPath = Environment.GetEnvironmentVariable("BOOMBOX_YTDLP_COOKIES");
            if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            {
                return envPath;
            }

            var root = GetModRootDirectory();
            var candidates = new[]
            {
                Path.Combine(root, "YtdlpDownloads", "cookies.txt"),
                Path.Combine(root, "Cookies", "youtube.txt"),
                Path.Combine(root, "cookies.txt")
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static Process StartProcess(string fileName, string arguments, out StringBuilder output)
        {
            output = new StringBuilder();

            try
            {
                var capturedOutput = output;
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = GetModRootDirectory()
                    },
                    EnableRaisingEvents = true
                };

                process.OutputDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        capturedOutput.AppendLine(args.Data);
                    }
                };

                process.ErrorDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        capturedOutput.AppendLine(args.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                return process;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Boombox] Failed to start process '{fileName}': {ex}");
                return null;
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // ignored
            }
            finally
            {
                process?.Dispose();
            }
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            var result = new StringBuilder();
            result.Append('"');

            var backslashes = 0;
            foreach (var ch in value)
            {
                if (ch == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (ch == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                    continue;
                }

                result.Append('\\', backslashes);
                result.Append(ch);
                backslashes = 0;
            }

            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
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
    }
}
