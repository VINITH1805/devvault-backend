namespace DevVault.DTOs
{
    public class SaveReleaseRequest
    {
        public Guid UserId { get; set; } // In a real app, we'd extract this securely from the JWT token
        public string Title { get; set; } = string.Empty;
        public string RawInput { get; set; } = string.Empty;
        public string MarkdownNotes { get; set; } = string.Empty;
        public string SocialPost { get; set; } = string.Empty;
        public string SemanticVersion { get; set; } = string.Empty;
    }
}
