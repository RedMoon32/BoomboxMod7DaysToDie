namespace Boombox
{
    public sealed class MusicSearchItem
    {
        public MusicSearchItem(string source, string id, string title, string artist, string duration, string downloadPath)
        {
            Source = source ?? string.Empty;
            Id = id ?? string.Empty;
            Title = title ?? string.Empty;
            Artist = artist ?? string.Empty;
            Duration = duration ?? string.Empty;
            DownloadPath = downloadPath ?? string.Empty;
        }

        public string Source { get; }
        public string Id { get; }
        public string Title { get; }
        public string Artist { get; }
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
    }
}
