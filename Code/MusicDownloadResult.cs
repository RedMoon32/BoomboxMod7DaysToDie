namespace Boombox
{
    public sealed class MusicDownloadResult
    {
        public bool Success;
        public int ExitCode;
        public string FilePath = string.Empty;
        public string Error = string.Empty;
        public string DiagnosticOutput = string.Empty;

        public void Fail(int exitCode, string error, string diagnosticOutput)
        {
            Success = false;
            ExitCode = exitCode;
            Error = error ?? string.Empty;
            DiagnosticOutput = diagnosticOutput ?? string.Empty;
        }

        public void Complete(string filePath, string diagnosticOutput)
        {
            Success = true;
            ExitCode = 0;
            FilePath = filePath ?? string.Empty;
            Error = string.Empty;
            DiagnosticOutput = diagnosticOutput ?? string.Empty;
        }
    }
}
