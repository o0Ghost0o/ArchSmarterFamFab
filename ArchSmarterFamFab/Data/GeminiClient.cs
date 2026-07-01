using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace ArchSmarterFamFab.Data
{
    /// <summary>
    /// Google Gemini client (Generative Language API v1beta). Sends one or more images as
    /// inline base64 data plus a system instruction, and requests a JSON response.
    /// </summary>
    public class GeminiClient : IFamilyModelClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
        private readonly string _apiKey;
        private readonly string _modelName;

        public GeminiClient(string apiKey, string modelName)
        {
            _apiKey = apiKey;
            _modelName = modelName;
        }

        public Task<string> GenerateFamilyFromImagesAsync(IReadOnlyList<ImageInput> images,
            string skillPrompt, string schemaJson, string userContext = null)
        {
            string userText = LlmPrompts.GenerateUserText;
            if (!string.IsNullOrEmpty(userContext))
                userText += "\n\nAdditional context from the user:\n" + userContext;

            return SendAsync(LlmPrompts.SystemPrompt(skillPrompt, schemaJson), BuildParts(images, userText));
        }

        public Task<string> RefineFamilyAsync(string currentJson, string userInstruction,
            string skillPrompt, string schemaJson, IReadOnlyList<ImageInput> images = null)
        {
            string text = LlmPrompts.RefineUserText(currentJson, userInstruction);
            return SendAsync(LlmPrompts.SystemPrompt(skillPrompt, schemaJson), BuildParts(images, text));
        }

        private static object[] BuildParts(IReadOnlyList<ImageInput> images, string text)
        {
            var parts = new List<object>();
            if (images != null)
            {
                foreach (ImageInput img in images)
                    parts.Add(new { inline_data = new { mime_type = img.MimeType, data = Convert.ToBase64String(img.Bytes) } });
            }
            parts.Add(new { text });
            return parts.ToArray();
        }

        private async Task<string> SendAsync(string systemPrompt, object[] userParts)
        {
            var requestBody = new
            {
                system_instruction = new { parts = new object[] { new { text = systemPrompt } } },
                contents = new object[] { new { role = "user", parts = userParts } },
                generationConfig = new { maxOutputTokens = 32768, responseMimeType = "application/json" }
            };

            string jsonPayload = JsonSerializer.Serialize(requestBody);
            string url = BaseUrl + Uri.EscapeDataString(_modelName) + ":generateContent";

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("x-goog-api-key", _apiKey);
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await Http.SendAsync(request);
            string responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new LlmException(
                    $"Gemini API returned {(int)response.StatusCode}: {response.ReasonPhrase}",
                    responseJson);
            }

            return ExtractTextFromResponse(responseJson);
        }

        private static string ExtractTextFromResponse(string responseJson)
        {
            using JsonDocument doc = JsonDocument.Parse(responseJson);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("promptFeedback", out JsonElement feedback) &&
                feedback.TryGetProperty("blockReason", out JsonElement blockReason))
            {
                throw new LlmException(
                    $"Gemini blocked the request ({blockReason.GetString()}).", responseJson);
            }

            if (!root.TryGetProperty("candidates", out JsonElement candidates) ||
                candidates.GetArrayLength() == 0)
            {
                throw new LlmException("No candidates in response", responseJson);
            }

            JsonElement candidate = candidates[0];

            if (candidate.TryGetProperty("finishReason", out JsonElement finishReason) &&
                finishReason.GetString() == "MAX_TOKENS")
            {
                throw new LlmException(
                    "Response was truncated — the family definition exceeded the token limit. Try a simpler object or reduce detail level.",
                    responseJson);
            }

            if (candidate.TryGetProperty("content", out JsonElement content) &&
                content.TryGetProperty("parts", out JsonElement parts) &&
                parts.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (JsonElement part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out JsonElement textEl))
                        sb.Append(textEl.GetString());
                }

                string text = sb.ToString();
                if (!string.IsNullOrEmpty(text))
                    return LlmJson.CleanJsonResponse(text);
            }

            throw new LlmException("No text content in response", responseJson);
        }
    }
}
