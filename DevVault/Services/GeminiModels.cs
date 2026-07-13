namespace DevVault.Services
{
    public class GenerateReleaseRequest
    {
        public string RawGitDiff { get; set; } = string.Empty;
    }

    // What we force Gemini to return as perfectly formatted JSON
    public class AIReleaseResponse
    {
        public string SemanticVersion { get; set; } = string.Empty; // "Major", "Minor", or "Patch"
        public string MarkdownNotes { get; set; } = string.Empty;
        public string SocialPost { get; set; } = string.Empty;
    }
}
