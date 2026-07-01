using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace ArchSmarterFamFab.Data
{
    /// <summary>
    /// Moonshot Kimi client. Moonshot exposes an OpenAI-compatible Chat Completions API,
    /// so the image is sent as a base64 data URL inside a multi-part user message.
    /// </summary>
    public class KimiClient : IFamilyModelClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        private const string Endpoint = "https://api.moonshot.ai/v1/chat/completions";
        private readonly string _apiKey;
        private readonly string _modelName;

        public KimiClient(string apiKey, string modelName)
        {
            _apiKey = apiKey;
            _modelName = modelName;
        }

        public Task<string> GenerateFamilyFromImageAsync(byte[] imageBytes, string mimeType,
            string skillPrompt, string schemaJson, string userContext = null)
        {
            string userText = LlmPrompts.GenerateUserText;
            if (!string.IsNullOrEmpty(userContext))
                userText += "\n\nAdditional context from the user:\n" + userContext;

            string dataUrl = "data:" + mimeType + ";base64," + Convert.ToBase64String(imageBytes);
            var content = new object[]
            {
                new { type = "image_url", image_url = new { url = dataUrl } },
                new { type = "text", text = userText }
            };

            return SendAsync(LlmPrompts.SystemPrompt(skillPrompt, schemaJson), content);
        }

        public Task<string> RefineFamilyAsync(string currentJson, string userInstruction,
            string skillPrompt, string schemaJson, byte[] imageBytes = null, string imageMimeType = null)
        {
            string text = LlmPrompts.RefineUserText(currentJson, userInstruction);

            object userContent;
            if (imageBytes != null && !string.IsNullOrEmpty(imageMimeType))
            {
                string dataUrl = "data:" + imageMimeType + ";base64," + Convert.ToBase64String(imageBytes);
                userContent = new object[]
                {
                    new { type = "image_url", image_url = new { url = dataUrl } },
                    new { type = "text", text }
                };
            }
            else
            {
                userContent = text;
            }

            return SendAsync(LlmPrompts.SystemPrompt(skillPrompt, schemaJson), userContent);
        }

        private async Task<string> SendAsync(string systemPrompt, object userContent)
        {
            var requestBody = new
            {
                model = _modelName,
                max_tokens = 32768,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent }
                }
            };

            string jsonPayload = JsonSerializer.Serialize(requestBody);

            var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await Http.SendAsync(request);
            string responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new LlmException(
                    $"Kimi API returned {(int)response.StatusCode}: {response.ReasonPhrase}",
                    responseJson);
            }

            return ExtractTextFromResponse(responseJson);
        }

        private static string ExtractTextFromResponse(string responseJson)
        {
            using JsonDocument doc = JsonDocument.Parse(responseJson);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("choices", out JsonElement choices) ||
                choices.GetArrayLength() == 0)
            {
                throw new LlmException("No choices in response", responseJson);
            }

            JsonElement choice = choices[0];

            if (choice.TryGetProperty("finish_reason", out JsonElement finishReason) &&
                finishReason.GetString() == "length")
            {
                throw new LlmException(
                    "Response was truncated — the family definition exceeded the token limit. Try a simpler object or reduce detail level.",
                    responseJson);
            }

            if (choice.TryGetProperty("message", out JsonElement message) &&
                message.TryGetProperty("content", out JsonElement contentEl) &&
                contentEl.ValueKind == JsonValueKind.String)
            {
                string text = contentEl.GetString();
                if (!string.IsNullOrEmpty(text))
                    return LlmJson.CleanJsonResponse(text);
            }

            throw new LlmException("No text content in response", responseJson);
        }
    }
}
