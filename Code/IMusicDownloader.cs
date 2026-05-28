using System.Collections;

namespace Boombox
{
    public interface IMusicDownloader
    {
        string Name { get; }

        IEnumerator SearchByQuery(string query, int limit, MusicSearchResult result);

        IEnumerator DownloadByQuery(string query, MusicDownloadResult result);

        IEnumerator DownloadSearchResult(MusicSearchItem item, MusicDownloadResult result);
    }
}
