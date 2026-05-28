using System.Collections.Generic;

namespace Boombox
{
    public sealed class MusicSearchResult
    {
        public bool Success;
        public int ExitCode;
        public string Error = string.Empty;
        public string DiagnosticOutput = string.Empty;
        public readonly List<MusicSearchItem> Items = new List<MusicSearchItem>();

        public void Fail(int exitCode, string error, string diagnosticOutput)
        {
            Success = false;
            ExitCode = exitCode;
            Error = error ?? string.Empty;
            DiagnosticOutput = diagnosticOutput ?? string.Empty;
            Items.Clear();
        }

        public void Complete(IEnumerable<MusicSearchItem> items, string diagnosticOutput)
        {
            Success = true;
            ExitCode = 0;
            Error = string.Empty;
            DiagnosticOutput = diagnosticOutput ?? string.Empty;
            Items.Clear();
            if (items != null)
            {
                Items.AddRange(items);
            }
        }
    }
}
