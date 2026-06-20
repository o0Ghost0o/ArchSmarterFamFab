using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace ArchSmarterFamFab.Data
{
    public class ClaudeClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        private readonly string _apiKey;
        private readonly string _modelName;

        public ClaudeClient(string apiKey, string modelName)
        {
            _apiKey = apiKey;
            _modelName = modelName;
        }

        public async Task<string> GenerateFamilyFromImageAsync(byte[] imageBytes, string mimeType,
            string skillPrompt, string schemaJson, string userContext = null)
        {
            string base64Image = Convert.ToBase64String(imageBytes);

            string systemPrompt = skillPrompt + "\n\n## JSON Schema\n\nThe generated JSON must conform to this schema:\n\n```json\n" + schemaJson + "\n```\n\n## Output Format\n\nReturn ONLY the raw JSON object. Do not wrap it in markdown code fences. Do not include any explanation before or after the JSON. The response must start with `{` and end with `}`.";

            string userText = "Analyze this image and generate a Revit family JSON definition for this object. Use Standard detail level. Propose reasonable default dimensions based on the object type. Do not ask questions — generate the JSON directly.";
            if (!string.IsNullOrEmpty(userContext))
                userText += "\n\nAdditional context from the user:\n" + userContext;

            var requestBody = new
            {
                model = _modelName,
                max_tokens = 32768,
                system = systemPrompt,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                type = "image",
                                source = new
                                {
                                    type = "base64",
                                    media_type = mimeType,
                                    data = base64Image
                                }
                            },
                            new
                            {
                                type = "text",
                                text = userText
                            }
                        }
                    }
                }
            };

            string jsonPayload = JsonSerializer.Serialize(requestBody);

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await Http.SendAsync(request);
            string responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new ClaudeException(
                    $"Claude API returned {(int)response.StatusCode}: {response.ReasonPhrase}",
                    responseJson);
            }

            return ExtractTextFromResponse(responseJson);
        }

        public async Task<string> RefineFamilyAsync(string currentJson, string userInstruction,
            string skillPrompt, string schemaJson, byte[] imageBytes = null, string imageMimeType = null)
        {
            string systemPrompt = skillPrompt +
                "\n\n## JSON Schema\n\nThe generated JSON must conform to this schema:\n\n```json\n" + schemaJson + "\n```" +
                "\n\n## Output Format\n\nReturn ONLY the raw JSON object. Do not wrap it in markdown code fences. Do not include any explanation before or after the JSON. The response must start with `{` and end with `}`.";

            string textContent = "Here is the current Revit family JSON definition:\n\n```json\n" + currentJson + "\n```\n\n" +
                "Modify this family definition according to the following instruction. Return the complete updated JSON:\n\n" + userInstruction;

            object[] contentParts;
            if (imageBytes != null && !string.IsNullOrEmpty(imageMimeType))
            {
                contentParts = new object[]
                {
                    new
                    {
                        type = "image",
                        source = new
                        {
                            type = "base64",
                            media_type = imageMimeType,
                            data = Convert.ToBase64String(imageBytes)
                        }
                    },
                    new { type = "text", text = textContent }
                };
            }
            else
            {
                contentParts = new object[]
                {
                    new { type = "text", text = textContent }
                };
            }

            var requestBody = new
            {
                model = _modelName,
                max_tokens = 32768,
                system = systemPrompt,
                messages = new object[]
                {
                    new { role = "user", content = contentParts }
                }
            };

            string jsonPayload = JsonSerializer.Serialize(requestBody);

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await Http.SendAsync(request);
            string responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new ClaudeException(
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
                throw new ClaudeException(
                    "Response was truncated — the family definition exceeded the token limit. Try a simpler object or reduce detail level.",
                    responseJson);
            }

            if (!root.TryGetProperty("content", out JsonElement content) || content.GetArrayLength() == 0)
                throw new ClaudeException("No content in response", responseJson);

            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out JsonElement typeEl) &&
                    typeEl.GetString() == "text" &&
                    block.TryGetProperty("text", out JsonElement textEl))
                {
                    string text = textEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                        return CleanJsonResponse(text);
                }
            }

            throw new ClaudeException("No text content in response", responseJson);
        }

        private static string CleanJsonResponse(string text)
        {
            text = text.Trim();

            if (text.StartsWith("```json"))
                text = text.Substring(7);
            else if (text.StartsWith("```"))
                text = text.Substring(3);

            if (text.EndsWith("```"))
                text = text.Substring(0, text.Length - 3);

            text = text.Trim();

            int firstBrace = text.IndexOf('{');
            int lastBrace = text.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
                text = text.Substring(firstBrace, lastBrace - firstBrace + 1);

            return text;
        }
    }

    public class ClaudeException : Exception
    {
        public string ResponseJson { get; }

        public ClaudeException(string message, string responseJson)
            : base(message)
        {
            ResponseJson = responseJson;
        }
    }
}
