namespace DevVault.Models
{
    public class SavedRelease
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; } // Foreign key linking back to our User

        public string Title { get; set; } = string.Empty;         // e.g., "Feature: JWT Authentication Implementation"
        public string RawInput { get; set; } = string.Empty;      // The original messy diff/commits pasted by user
        public string MarkdownNotes { get; set; } = string.Empty; // The beautiful AI-generated release notes
        public string SocialPost { get; set; } = string.Empty;    // The AI-generated LinkedIn/X post content

        public string SemanticVersion { get; set; } = string.Empty; // "Major", "Minor", or "Patch"
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation property for EF Core
        public User? User { get; set; }
    }
}
