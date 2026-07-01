using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace ArchSmarterFamFab.Data
{
    /// <summary>
    /// Moonshot Kimi client. Moonshot exposes an OpenAI-compatible Chat Completions API,
    /// so images are sent as base64 data URLs inside a multi-part user message.
    ///
    /// Two key systems are supported automatically:
    ///   - Open-platform keys ("sk-...")   -> https://api.moonshot.ai/v1
    ///   - Kimi Code plan keys ("sk-kimi-") -> https://api.kimi.com/coding/v1, which additionally
    ///     requires a recognized coding-agent User-Agent (else 403 access_terminated_error).
    /// </summary>
    public class KimiClient : IFamilyModelClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        private const string MoonshotEndpoint = "https://api.moonshot.ai/v1/chat/completions";
        private const string KimiCodingEndpoint = "https://api.kimi.com/coding/v1/chat/completions";
        private const string CodingUserAgent = "claude-code/0.1.0";
        private readonly string _apiKey;
        private readonly string _modelName;

        public KimiClient(string apiKey, string modelName)
        {
            _apiKey = apiKey;
            _modelName = modelName;
        }

        private bool IsCodingKey => _apiKey != null && _apiKey.StartsWith("sk-kimi-");

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
            string text = LlmPrompts.RefineUserText(currentJson, userInstruction);
            return SendAsync(LlmPrompts.SystemPrompt(skillPrompt, schemaJson), BuildContent(images, text));
        }

        private static object[] BuildContent(IReadOnlyList<ImageInput> images, string text)
        {
            var content = new List<object>();
            if (images != null)
            {
                foreach (ImageInput img in images)
                {
                    string dataUrl = "data:" + img.MimeType + ";base64," + Convert.ToBase64String(img.Bytes);
                    content.Add(new { type = "image_url", image_url = new { url = dataUrl } });
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
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent }
                }
            };

            string jsonPayload = JsonSerializer.Serialize(requestBody);

            string endpoint = IsCodingKey ? KimiCodingEndpoint : MoonshotEndpoint;
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            if (IsCodingKey)
                request.Headers.TryAddWithoutValidation("User-Agent", CodingUserAgent);
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
