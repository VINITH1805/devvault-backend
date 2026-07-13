using DevVault.Models;
using Microsoft.EntityFrameworkCore;

namespace DevVault.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users => Set<User>();
        public DbSet<SavedRelease> SavedReleases => Set<SavedRelease>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure User entity
            modelBuilder.Entity<User>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.GitHubId).IsUnique(); // Ensure no duplicate users
            });

            // Configure SavedRelease entity
            modelBuilder.Entity<SavedRelease>(entity =>
            {
                entity.HasKey(e => e.Id);

                // Configure 1-to-Many Relationship (User -> SavedReleases)
                entity.HasOne(d => d.User)
                      .WithMany(p => p.SavedReleases)
                      .HasForeignKey(d => d.UserId)
                      .OnDelete(DeleteBehavior.Cascade); // If a user deletes their account, cascade delete their data

                // Optimize search queries by indexing UserId and CreatedAt
                entity.HasIndex(e => e.UserId);
                entity.HasIndex(e => e.CreatedAt);
            });
        }
    }
}
