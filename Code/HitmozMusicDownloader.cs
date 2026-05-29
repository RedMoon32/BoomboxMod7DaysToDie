using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace Boombox
{
    public sealed class HitmozMusicDownloader : IMusicDownloader
    {
        private const string BaseUrl = "https://rus.hitmoz.org";
        private const string SearchUrl = BaseUrl + "/search";
        private const int TimeoutMilliseconds = 30000;
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36";

        private static readonly Regex TrackItemRegex = new Regex(
            "<li\\b(?=[^>]*\\btracks__item\\b)(?=[^>]*\\btrack\\b)[^>]*>(?<body>.*?)</li>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex DownloadHrefRegex = new Regex(
            "<a\\b(?=[^>]*\\btrack__download-btn\\b)[^>]*\\bhref\\s*=\\s*(?:\"(?<href>[^\"]+)\"|'(?<href>[^']+)'|(?<href>[^\\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex TitleRegex = new Regex(
            "<div\\b(?=[^>]*\\btrack__title\\b)[^>]*>(?<text>.*?)</div>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex ArtistRegex = new Regex(
            "<div\\b(?=[^>]*\\btrack__desc\\b)[^>]*>(?<text>.*?)</div>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex DurationRegex = new Regex(
            "<div\\b(?=[^>]*\\btrack__fulltime\\b)[^>]*>(?<text>.*?)</div>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex TrackIdRegex = new Regex(
            "<span\\b(?=[^>]*\\btrack__like-btn\\b)[^>]*\\bdata-track-id\\s*=\\s*(?:\"(?<id>[^\"]+)\"|'(?<id>[^']+)'|(?<id>[^\\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private readonly string _modRoot;

        public HitmozMusicDownloader(string modRoot)
        {
            _modRoot = modRoot ?? string.Empty;
        }

        public string Name => "hitmoz";

        public IEnumerator SearchByQuery(string query, int limit, MusicSearchResult result)
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

            var task = Task.Run(() => SearchByQuerySync(query, limit));
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.IsFaulted)
            {
                result.Fail(-1, task.Exception?.GetBaseException().Message ?? "search failed", task.Exception?.ToString() ?? string.Empty);
                yield break;
            }

            var taskResult = task.Result;
            if (!taskResult.Success)
            {
                result.Fail(taskResult.ExitCode, taskResult.Error, taskResult.DiagnosticOutput);
                yield break;
            }

            result.Complete(taskResult.Items, taskResult.DiagnosticOutput);
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

            var progress = new DownloadProgress();
            var task = Task.Run(() => DownloadByQuerySync(query, progress));
            var lastStage = string.Empty;
            var lastProgressBucket = -1;

            while (!task.IsCompleted)
            {
                LogProgress(query, progress, ref lastStage, ref lastProgressBucket);
                yield return null;
            }

            LogProgress(query, progress, ref lastStage, ref lastProgressBucket);

            if (task.IsFaulted)
            {
                result.Fail(-1, task.Exception?.GetBaseException().Message ?? "download failed", task.Exception?.ToString() ?? string.Empty);
                yield break;
            }

            var taskResult = task.Result;
            if (!taskResult.Success)
            {
                result.Fail(taskResult.ExitCode, taskResult.Error, taskResult.DiagnosticOutput);
                yield break;
            }

            Debug.Log($"[Boombox] Hitmoz download finished query='{query}' file='{Path.GetFileName(taskResult.FilePath)}'");
            result.Complete(taskResult.FilePath, taskResult.DiagnosticOutput);
        }

        public IEnumerator DownloadSearchResult(MusicSearchItem item, MusicDownloadResult result)
        {
            if (result == null)
            {
                yield break;
            }

            if (item == null || string.IsNullOrWhiteSpace(item.DownloadPath))
            {
                result.Fail(-1, "empty search result", string.Empty);
                yield break;
            }

            var progress = new DownloadProgress();
            var task = Task.Run(() => DownloadSearchItemSync(item, progress));
            var lastStage = string.Empty;
            var lastProgressBucket = -1;

            while (!task.IsCompleted)
            {
                LogProgress(item.DisplayName, progress, ref lastStage, ref lastProgressBucket);
                yield return null;
            }

            LogProgress(item.DisplayName, progress, ref lastStage, ref lastProgressBucket);

            if (task.IsFaulted)
            {
                result.Fail(-1, task.Exception?.GetBaseException().Message ?? "download failed", task.Exception?.ToString() ?? string.Empty);
                yield break;
            }

            var taskResult = task.Result;
            if (!taskResult.Success)
            {
                result.Fail(taskResult.ExitCode, taskResult.Error, taskResult.DiagnosticOutput);
                yield break;
            }

            Debug.Log($"[Boombox] Hitmoz download finished selection='{item.DisplayName}' file='{Path.GetFileName(taskResult.FilePath)}'");
            result.Complete(taskResult.FilePath, taskResult.DiagnosticOutput);
        }

        private MusicSearchResult SearchByQuerySync(string query, int limit)
        {
            var result = new MusicSearchResult();
            try
            {
                var pageHtml = FetchSearchPage(query);
                var items = ParseTracks(pageHtml)
                    .Take(Math.Max(1, limit))
                    .Select(track => track.ToSearchItem(Name))
                    .ToArray();

                if (items.Length == 0)
                {
                    result.Fail(1, "no tracks found", string.Empty);
                    return result;
                }

                result.Complete(items, $"found: {items.Length}");
                return result;
            }
            catch (Exception ex)
            {
                result.Fail(1, ex.Message, ex.ToString());
                return result;
            }
        }

        private MusicDownloadResult DownloadByQuerySync(string query, DownloadProgress progress)
        {
            var result = new MusicDownloadResult();
            try
            {
                progress.SetStage("searching");
                var pageHtml = FetchSearchPage(query);
                progress.SetStage("parsing");
                var tracks = ParseTracks(pageHtml);
                if (tracks.Count == 0)
                {
                    result.Fail(1, "no tracks found", string.Empty);
                    return result;
                }

                return DownloadTrackSync(tracks[0], tracks.Count, progress);
            }
            catch (Exception ex)
            {
                result.Fail(1, ex.Message, ex.ToString());
                return result;
            }
        }

        private MusicDownloadResult DownloadSearchItemSync(MusicSearchItem item, DownloadProgress progress)
        {
            try
            {
                return DownloadTrackSync(HitmozTrack.FromSearchItem(item), 1, progress);
            }
            catch (Exception ex)
            {
                var result = new MusicDownloadResult();
                result.Fail(1, ex.Message, ex.ToString());
                return result;
            }
        }

        private MusicDownloadResult DownloadTrackSync(HitmozTrack track, int trackCount, DownloadProgress progress)
        {
            var result = new MusicDownloadResult();
            progress.SetSelectedTrack(track.DisplayName, trackCount);
            var outputDir = GetMusicLibraryDirectory();
            Directory.CreateDirectory(outputDir);

            var fileName = BuildLibraryFileName(track.DisplayName, track.Id, ".mp3");
            var filePath = Path.Combine(outputDir, fileName);
            DownloadTrack(track, filePath, progress);

            result.Complete(filePath, $"selected: {track.DisplayName}");
            return result;
        }

        private static string FetchSearchPage(string query)
        {
            var url = SearchUrl + "?q=" + Uri.EscapeDataString(query);
            var request = CreateRequest(url, "*/*");
            request.Headers["X-PJAX"] = "true";
            request.Headers["X-Requested-With"] = "XMLHttpRequest";

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null, GetEncoding(response)))
            {
                return reader.ReadToEnd();
            }
        }

        private static List<HitmozTrack> ParseTracks(string html)
        {
            var tracks = new List<HitmozTrack>();
            if (string.IsNullOrEmpty(html))
            {
                return tracks;
            }

            foreach (Match itemMatch in TrackItemRegex.Matches(html))
            {
                var body = itemMatch.Groups["body"].Value;
                var hrefMatch = DownloadHrefRegex.Match(body);
                if (!hrefMatch.Success)
                {
                    continue;
                }

                var downloadPath = WebUtility.HtmlDecode(hrefMatch.Groups["href"].Value);
                if (string.IsNullOrWhiteSpace(downloadPath))
                {
                    continue;
                }

                tracks.Add(new HitmozTrack(
                    WebUtility.HtmlDecode(TrackIdRegex.Match(body).Groups["id"].Value),
                    CleanText(ArtistRegex.Match(body).Groups["text"].Value),
                    CleanText(TitleRegex.Match(body).Groups["text"].Value),
                    CleanText(DurationRegex.Match(body).Groups["text"].Value),
                    downloadPath));
            }

            return tracks;
        }

        private static void DownloadTrack(HitmozTrack track, string filePath, DownloadProgress progress)
        {
            var url = new Uri(new Uri(BaseUrl), track.DownloadPath).ToString();
            var request = CreateRequest(url, "audio/mpeg,audio/*,*/*");

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                var contentType = response.ContentType ?? string.Empty;
                if (contentType.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new InvalidOperationException("server returned HTML instead of audio");
                }

                using (var stream = response.GetResponseStream())
                using (var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    if (stream == null)
                    {
                        throw new InvalidOperationException("empty response stream");
                    }

                    var buffer = new byte[128 * 1024];
                    var totalBytes = response.ContentLength > 0 ? response.ContentLength : -1;
                    long downloadedBytes = 0;
                    progress.SetDownloadProgress(downloadedBytes, totalBytes);

                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        file.Write(buffer, 0, read);
                        downloadedBytes += read;
                        progress.SetDownloadProgress(downloadedBytes, totalBytes);
                    }
                }
            }

            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length == 0)
            {
                TryDelete(filePath);
                throw new InvalidOperationException("download produced an empty file");
            }
        }

        private static HttpWebRequest CreateRequest(string url, string accept)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = accept;
            request.UserAgent = UserAgent;
            request.Referer = BaseUrl + "/";
            request.Timeout = TimeoutMilliseconds;
            request.ReadWriteTimeout = TimeoutMilliseconds;
            request.Headers[HttpRequestHeader.AcceptLanguage] = "ru,en;q=0.9";
            return request;
        }

        private static Encoding GetEncoding(HttpWebResponse response)
        {
            try
            {
                if (!string.IsNullOrEmpty(response.CharacterSet))
                {
                    return Encoding.GetEncoding(response.CharacterSet);
                }
            }
            catch
            {
                // ignored
            }

            return Encoding.UTF8;
        }

        private static string CleanText(string html)
        {
            if (string.IsNullOrEmpty(html))
            {
                return string.Empty;
            }

            var text = Regex.Replace(html, "<.*?>", string.Empty);
            return WebUtility.HtmlDecode(Regex.Replace(text, "\\s+", " ").Trim());
        }

        private static void LogProgress(string query, DownloadProgress progress, ref string lastStage, ref int lastProgressBucket)
        {
            var snapshot = progress.Snapshot();
            if (!string.Equals(snapshot.Stage, lastStage, StringComparison.Ordinal))
            {
                lastStage = snapshot.Stage;
                if (snapshot.Stage == "searching")
                {
                    Debug.Log($"[Boombox] Hitmoz search started query='{query}'");
                }
                else if (snapshot.Stage == "parsing")
                {
                    Debug.Log($"[Boombox] Hitmoz search response received query='{query}'");
                }
                else if (snapshot.Stage == "downloading")
                {
                    Debug.Log($"[Boombox] Hitmoz selected first result query='{query}' total={snapshot.TrackCount} track='{snapshot.TrackName}'");
                }
            }

            if (snapshot.Stage != "downloading")
            {
                return;
            }

            if (snapshot.TotalBytes <= 0)
            {
                if (lastProgressBucket < 0 && snapshot.DownloadedBytes > 0)
                {
                    lastProgressBucket = 0;
                    Debug.Log($"[Boombox] Hitmoz download in progress query='{query}' bytes={snapshot.DownloadedBytes}");
                }

                return;
            }

            var percent = (int)Math.Min(100, snapshot.DownloadedBytes * 100 / snapshot.TotalBytes);
            var bucket = percent >= 100 ? 100 : percent / 25 * 25;
            if (bucket > lastProgressBucket)
            {
                lastProgressBucket = bucket;
                Debug.Log($"[Boombox] Hitmoz download progress query='{query}' {bucket}%");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignored
            }
        }

        private string GetMusicLibraryDirectory()
        {
            return Path.Combine(_modRoot, "Resources", "MusicToPlay");
        }

        private static string BuildLibraryFileName(string displayName, string id, string extension)
        {
            var baseName = SanitizeFileName(displayName);
            if (string.IsNullOrEmpty(baseName))
            {
                baseName = SanitizeFileName(id);
            }

            if (string.IsNullOrEmpty(baseName))
            {
                baseName = "hitmoz_track";
            }

            return baseName + extension;
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Trim().Length);
            foreach (var ch in value.Trim())
            {
                if (invalidChars.Contains(ch))
                {
                    builder.Append('_');
                    continue;
                }

                builder.Append(char.IsWhiteSpace(ch) ? '_' : ch);
            }

            var result = Regex.Replace(builder.ToString(), "_+", "_").Trim('_');
            return result.Length > 120 ? result.Substring(0, 120).Trim('_') : result;
        }

        private sealed class DownloadProgress
        {
            private readonly object _syncRoot = new object();
            private string _stage = string.Empty;
            private string _trackName = string.Empty;
            private int _trackCount;
            private long _downloadedBytes;
            private long _totalBytes = -1;

            public void SetStage(string stage)
            {
                lock (_syncRoot)
                {
                    _stage = stage ?? string.Empty;
                }
            }

            public void SetSelectedTrack(string trackName, int trackCount)
            {
                lock (_syncRoot)
                {
                    _stage = "downloading";
                    _trackName = trackName ?? string.Empty;
                    _trackCount = trackCount;
                    _downloadedBytes = 0;
                    _totalBytes = -1;
                }
            }

            public void SetDownloadProgress(long downloadedBytes, long totalBytes)
            {
                lock (_syncRoot)
                {
                    _downloadedBytes = downloadedBytes;
                    _totalBytes = totalBytes;
                }
            }

            public ProgressSnapshot Snapshot()
            {
                lock (_syncRoot)
                {
                    return new ProgressSnapshot(_stage, _trackName, _trackCount, _downloadedBytes, _totalBytes);
                }
            }
        }

        private readonly struct ProgressSnapshot
        {
            public ProgressSnapshot(string stage, string trackName, int trackCount, long downloadedBytes, long totalBytes)
            {
                Stage = stage ?? string.Empty;
                TrackName = trackName ?? string.Empty;
                TrackCount = trackCount;
                DownloadedBytes = downloadedBytes;
                TotalBytes = totalBytes;
            }

            public string Stage { get; }
            public string TrackName { get; }
            public int TrackCount { get; }
            public long DownloadedBytes { get; }
            public long TotalBytes { get; }
        }

        private sealed class HitmozTrack
        {
            public HitmozTrack(string id, string artist, string title, string duration, string downloadPath)
            {
                Id = id ?? string.Empty;
                Artist = artist ?? string.Empty;
                Title = title ?? string.Empty;
                Duration = duration ?? string.Empty;
                DownloadPath = downloadPath ?? string.Empty;
            }

            public string Id { get; }
            public string Artist { get; }
            public string Title { get; }
            public string Duration { get; }
            public string DownloadPath { get; }

            public string DisplayName
            {
                get
                {
                    if (!string.IsNullOrEmpty(Artist) && !string.IsNullOrEmpty(Title))
                    {
                        return Artist + " - " + Title;
                    }

                    return !string.IsNullOrEmpty(Title) ? Title : Artist;
                }
            }

            public MusicSearchItem ToSearchItem(string source)
            {
                return new MusicSearchItem(source, Id, Title, Artist, Duration, DownloadPath);
            }

            public static HitmozTrack FromSearchItem(MusicSearchItem item)
            {
                return new HitmozTrack(item?.Id, item?.Artist, item?.Title, item?.Duration, item?.DownloadPath);
            }
        }
    }
}
