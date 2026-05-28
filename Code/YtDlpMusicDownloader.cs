using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Boombox
{
    public sealed class YtDlpMusicDownloader : IMusicDownloader
    {
        private const int DownloadTimeoutSeconds = 300;

        private readonly string _modRoot;

        public YtDlpMusicDownloader(string modRoot)
        {
            _modRoot = modRoot ?? string.Empty;
        }

        public string Name => "yt-dlp";

        public IEnumerator SearchByQuery(string query, int limit, MusicSearchResult result)
        {
            result?.Fail(-1, "yt-dlp search listing is not implemented", string.Empty);
            yield break;
        }

        public IEnumerator DownloadByQuery(string query, MusicDownloadResult result)
        {
            if (result == null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                result.Fail(-1, "empty query", string.Empty);
                yield break;
            }

            var toolsDir = Path.Combine(_modRoot, "YtdlpDownloads", "bin");
            var ytDlpPath = Path.Combine(toolsDir, "yt-dlp.exe");
            var ffmpegPath = Path.Combine(toolsDir, "ffmpeg.exe");
            if (!File.Exists(ytDlpPath) || !File.Exists(ffmpegPath))
            {
                result.Fail(-1, $"yt-dlp/ffmpeg missing: '{toolsDir}'", string.Empty);
                yield break;
            }

            var downloadDir = Path.Combine(_modRoot, "YtdlpDownloads", "audio");
            Directory.CreateDirectory(downloadDir);

            var fileBaseName = "playu_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var outputTemplate = fileBaseName + ".%(ext)s";
            var attempt = new DownloadAttemptResult();

            Debug.Log($"[Boombox] PLAYU download queued query='{query}' downloader='{Name}' mode=cookies");
            yield return RunDownloadAttempt(ytDlpPath, query, toolsDir, downloadDir, outputTemplate, true, attempt);

            if (!attempt.Success)
            {
                Debug.LogWarning($"[Boombox] PLAYU cookies download failed exit={attempt.ExitCode} query='{query}' output='{Truncate(attempt.Output, 2000)}'");
                DeleteDownloadOutputs(downloadDir, fileBaseName);
                attempt.Reset();

                Debug.Log($"[Boombox] PLAYU retry queued query='{query}' downloader='{Name}' mode=no-cookies");
                yield return RunDownloadAttempt(ytDlpPath, query, toolsDir, downloadDir, outputTemplate, false, attempt);
            }

            if (!attempt.Success)
            {
                result.Fail(attempt.ExitCode, "download failed after fallback", attempt.Output);
                yield break;
            }

            var mp3Path = Path.Combine(downloadDir, fileBaseName + ".mp3");
            if (!File.Exists(mp3Path))
            {
                result.Fail(-3, "download produced no mp3", attempt.Output);
                yield break;
            }

            result.Complete(mp3Path, attempt.Output);
        }

        public IEnumerator DownloadSearchResult(MusicSearchItem item, MusicDownloadResult result)
        {
            result?.Fail(-1, "yt-dlp numbered search playback is not implemented", string.Empty);
            yield break;
        }

        private IEnumerator RunDownloadAttempt(
            string ytDlpPath,
            string query,
            string toolsDir,
            string downloadDir,
            string outputTemplate,
            bool useCookies,
            DownloadAttemptResult result)
        {
            var arguments = BuildArguments(query, toolsDir, downloadDir, outputTemplate, useCookies);
            var process = StartProcess(ytDlpPath, arguments, out var output);
            if (process == null)
            {
                result.ExitCode = -1;
                result.Output = "failed to start yt-dlp";
                yield break;
            }

            var startTime = Time.realtimeSinceStartup;
            while (!process.HasExited)
            {
                if (Time.realtimeSinceStartup - startTime > DownloadTimeoutSeconds)
                {
                    TryKill(process);
                    result.ExitCode = -2;
                    result.Output = output.ToString();
                    yield break;
                }

                yield return null;
            }

            process.WaitForExit();
            result.ExitCode = process.ExitCode;
            result.Output = output.ToString();
            result.Success = result.ExitCode == 0;
            process.Dispose();
        }

        private string BuildArguments(string query, string toolsDir, string downloadDir, string outputTemplate, bool useCookies)
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

            var cookiesPath = useCookies ? GetCookiesPath() : string.Empty;
            if (useCookies && !string.IsNullOrEmpty(cookiesPath))
            {
                args.Add("--cookies");
                args.Add(cookiesPath);
            }

            args.Add("ytsearch1:" + query);
            return string.Join(" ", args.Select(QuoteArgument).ToArray());
        }

        private string GetCookiesPath()
        {
            var envPath = Environment.GetEnvironmentVariable("BOOMBOX_YTDLP_COOKIES");
            if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            {
                return envPath;
            }

            var candidates = new[]
            {
                Path.Combine(_modRoot, "YtdlpDownloads", "cookies.txt"),
                Path.Combine(_modRoot, "Cookies", "youtube.txt"),
                Path.Combine(_modRoot, "cookies.txt")
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

        private Process StartProcess(string fileName, string arguments, out StringBuilder output)
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
                        WorkingDirectory = _modRoot
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

        private static void DeleteDownloadOutputs(string downloadDir, string fileBaseName)
        {
            var extensions = new[] { ".mp3", ".webm", ".m4a", ".part", ".ytdl" };
            foreach (var extension in extensions)
            {
                try
                {
                    var path = Path.Combine(downloadDir, fileBaseName + extension);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Boombox] Failed to delete PLAYU temp file '{fileBaseName}{extension}': {ex.Message}");
                }
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

        private sealed class DownloadAttemptResult
        {
            public bool Success;
            public int ExitCode;
            public string Output = string.Empty;

            public void Reset()
            {
                Success = false;
                ExitCode = 0;
                Output = string.Empty;
            }
        }
    }
}
