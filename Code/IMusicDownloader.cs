using System.Collections;

namespace Boombox
{
    public interface IMusicDownloader
    {
        string Name { get; }

        IEnumerator DownloadByQuery(string query, MusicDownloadResult result);
    }
}
