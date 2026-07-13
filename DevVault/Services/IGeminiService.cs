namespace DevVault.Services
{
    public interface IGeminiService
    {
        Task<AIReleaseResponse?> AnalyzeGitDiffAsync(string rawDiff);
    }
}
