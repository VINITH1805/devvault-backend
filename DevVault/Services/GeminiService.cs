using System.Text;
using System.Text.Json;

namespace DevVault.Services
{
    public class GeminiService : IGeminiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _endpoint;
        private readonly string _systemPrompt;

        public GeminiService(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _apiKey = config["Gemini:apiKey"] ?? throw new ArgumentNullException("Gemini API Key is missing");
            _endpoint = config["Gemini:endPoint"] ?? throw new ArgumentNullException("Gemini Endpoint is missing");
            _systemPrompt = config["Gemini:systemPrompt"] ?? throw new ArgumentNullException("Gemini System Prompt is missing");
        }

        public async Task<AIReleaseResponse?> AnalyzeGitDiffAsync(string rawDiff)
        {
            var endpoint = $"{_endpoint}{_apiKey}";

            var systemPrompt = _systemPrompt;

            // Construct the payload required by Gemini's API
            var payload = new
            {
                system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                contents = new[]
                {
                    new { parts = new[] { new { text = $"Analyze this diff: \n\n{rawDiff}" } } }
                },
                generationConfig = new
                {
                    response_mime_type = "application/json" // Force JSON output
                }
            };

            var jsonPayload = JsonSerializer.Serialize(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content);
            response.EnsureSuccessStatusCode();

            var responseString = await response.Content.ReadAsStringAsync();

            // Extract the text from Gemini's nested JSON response structure
            using var doc = JsonDocument.Parse(responseString);
            var aiTextResponse = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            if (string.IsNullOrEmpty(aiTextResponse)) return null;

            // Find the very first '{' and the very last '}' in the AI's response
            int startIndex = aiTextResponse.IndexOf('{');
            int endIndex = aiTextResponse.LastIndexOf('}');

            if (startIndex == -1 || endIndex == -1)
            {
                Console.WriteLine("[DevVault Error] Gemini did not return a valid JSON object.");
                return null;
            }

            // Surgically extract ONLY the JSON string, ignoring any chatty text before or after
            string cleanJson = aiTextResponse.Substring(startIndex, (endIndex - startIndex) + 1);

            try
            {
                var result = JsonSerializer.Deserialize<AIReleaseResponse>(cleanJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return result;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[DevVault Error] Failed to parse extracted JSON: {ex.Message}");
                Console.WriteLine($"[Raw Extracted JSON]: {cleanJson}");
                return null;
            }
        }
    }
}
