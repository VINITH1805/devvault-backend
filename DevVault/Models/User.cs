namespace DevVault.Models
{
    public class User
    {
        public Guid Id { get; set; }
        public string GitHubId { get; set; } = string.Empty; // GitHub's unique user identifier
        public string Username { get; set; } = string.Empty; // GitHub username
        public string Name { get; set; } = string.Empty;     // Display name
        public string AvatarUrl { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation property for EF Core
        public ICollection<SavedRelease> SavedReleases { get; set; } = new List<SavedRelease>();
    }
}
