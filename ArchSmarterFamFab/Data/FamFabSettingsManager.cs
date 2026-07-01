namespace ArchSmarterFamFab.Data
{
    public class FamFabSettingsManager
    {
        private static readonly string SettingsFileName = "FamFab.json";
        private readonly string _filePath;
        private FamFabSettings _settings;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = null
        };

        public FamFabSettingsManager()
        {
            _filePath = GetSettingsFilePath();
            _settings = LoadSettings();
        }

        public static string GetSettingsFilePath()
        {
            string folderPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ArchSmarter", "FamFab");
            return Path.Combine(folderPath, SettingsFileName);
        }

        private FamFabSettings LoadSettings()
        {
            if (!File.Exists(_filePath))
            {
                CreateDefaultSettingsFile();
            }

            try
            {
                string json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<FamFabSettings>(json) ?? new FamFabSettings();
            }
            catch (Exception)
            {
                return new FamFabSettings();
            }
        }

        private void CreateDefaultSettingsFile()
        {
            string folderPath = Path.GetDirectoryName(_filePath);
            if (!Directory.Exists(folderPath))
                Directory.CreateDirectory(folderPath);

            if (!File.Exists(_filePath))
            {
                string json = JsonSerializer.Serialize(new FamFabSettings(), JsonOptions);
                File.WriteAllText(_filePath, json);
            }
        }

        public void SaveSettings()
        {
            try
            {
                string folderPath = Path.GetDirectoryName(_filePath);
                if (!Directory.Exists(folderPath))
                    Directory.CreateDirectory(folderPath);

                string json = JsonSerializer.Serialize(_settings, JsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        public FamFabSettings GetSettings()
        {
            return _settings;
        }

        public string GetProvider()
        {
            return LlmProviders.Normalize(_settings.Provider);
        }

        public void SetProvider(string provider)
        {
            _settings.Provider = LlmProviders.Normalize(provider);
            SaveSettings();
        }

        public string GetApiKey()
        {
            switch (GetProvider())
            {
                case LlmProviders.Google: return _settings.GeminiApiKey ?? "";
                case LlmProviders.Moonshot: return _settings.MoonshotApiKey ?? "";
                default: return _settings.ClaudeApiKey ?? "";
            }
        }

        public void SetApiKey(string apiKey)
        {
            apiKey ??= "";
            switch (GetProvider())
            {
                case LlmProviders.Google: _settings.GeminiApiKey = apiKey; break;
                case LlmProviders.Moonshot: _settings.MoonshotApiKey = apiKey; break;
                default: _settings.ClaudeApiKey = apiKey; break;
            }
            SaveSettings();
        }

        public string GetModelName()
        {
            var defaults = new FamFabSettings();
            switch (GetProvider())
            {
                case LlmProviders.Google:
                    return string.IsNullOrWhiteSpace(_settings.GeminiModel) ? defaults.GeminiModel : _settings.GeminiModel;
                case LlmProviders.Moonshot:
                    return string.IsNullOrWhiteSpace(_settings.MoonshotModel) ? defaults.MoonshotModel : _settings.MoonshotModel;
                default:
                    return string.IsNullOrWhiteSpace(_settings.ClaudeModel) ? defaults.ClaudeModel : _settings.ClaudeModel;
            }
        }

        public void SetModelName(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return;
            switch (GetProvider())
            {
                case LlmProviders.Google: _settings.GeminiModel = modelName; break;
                case LlmProviders.Moonshot: _settings.MoonshotModel = modelName; break;
                default: _settings.ClaudeModel = modelName; break;
            }
            SaveSettings();
        }

        public List<string> GetAvailableModels()
        {
            var defaults = new FamFabSettings();
            List<string> models;
            List<string> fallback;
            switch (GetProvider())
            {
                case LlmProviders.Google:
                    models = _settings.GeminiModels; fallback = defaults.GeminiModels; break;
                case LlmProviders.Moonshot:
                    models = _settings.MoonshotModels; fallback = defaults.MoonshotModels; break;
                default:
                    models = _settings.ClaudeModels; fallback = defaults.ClaudeModels; break;
            }
            return (models == null || models.Count == 0) ? fallback : models;
        }

        public static string GetLogsFolderPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ArchSmarter", "FamFab", "Logs");
        }
    }
}
