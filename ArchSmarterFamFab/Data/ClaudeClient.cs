using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace ArchSmarterFamFab.Data
{
    /// <summary>
    /// Anthropic Claude client (Messages API). Sends one or more images as base64 plus a
    /// system prompt built from the embedded skill + schema.
    /// </summary>
    public class ClaudeClient : IFamilyModelClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        private const string Endpoint = "https://api.anthropic.com/v1/messages";
        private readonly string _apiKey;
        private readonly string _modelName;

        public ClaudeClient(string apiKey, string modelName)
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

            return SendAsync(LlmPrompts.SystemPrompt(skillPrompt, schemaJson), BuildContent(images, userText));
        }

        public Task<string> RefineFamilyAsync(string currentJson, string userInstruction,
            string skillPrompt, string schemaJson, IReadOnlyList<ImageInput> images = null)
        {
            string textContent = LlmPrompts.RefineUserText(currentJson, userInstruction);
            return SendAsync(LlmPrompts.SystemPrompt(skillPrompt, schemaJson), BuildContent(images, textContent));
        }

        private static object[] BuildContent(IReadOnlyList<ImageInput> images, string text)
        {
            var content = new List<object>();
            if (images != null)
            {
                foreach (ImageInput img in images)
                {
                    content.Add(new
                    {
                        type = "image",
                        source = new { type = "base64", media_type = img.MimeType, data = Convert.ToBase64String(img.Bytes) }
                    });
                }
            }
            content.Add(new { type = "text", text });
            return content.ToArray();
        }

        private async Task<string> SendAsync(string systemPrompt, object[] userContent)
        {
            var requestBody = new
            {
                model = _modelName,
                max_tokens = 32768,
                system = systemPrompt,
                messages = new object[]
                {
                    new { role = "user", content = userContent }
                }
            };

            string jsonPayload = JsonSerializer.Serialize(requestBody);

            var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await Http.SendAsync(request);
            string responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new LlmException(
                    $"Claude API returned {(int)response.StatusCode}: {response.ReasonPhrase}",
                    responseJson);
            }

            return ExtractTextFromResponse(responseJson);
        }

        private static string ExtractTextFromResponse(string responseJson)
        {
            using JsonDocument doc = JsonDocument.Parse(responseJson);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("stop_reason", out JsonElement stopReason) &&
                stopReason.GetString() == "max_tokens")
            {
                throw new LlmException(
                    "Response was truncated — the family definition exceeded the token limit. Try a simpler object or reduce detail level.",
                    responseJson);
            }

            if (!root.TryGetProperty("content", out JsonElement content) || content.GetArrayLength() == 0)
                throw new LlmException("No content in response", responseJson);

            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out JsonElement typeEl) &&
                    typeEl.GetString() == "text" &&
                    block.TryGetProperty("text", out JsonElement textEl))
                {
                    string text = textEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                        return LlmJson.CleanJsonResponse(text);
                }
            }

            throw new LlmException("No text content in response", responseJson);
        }
    }
}
