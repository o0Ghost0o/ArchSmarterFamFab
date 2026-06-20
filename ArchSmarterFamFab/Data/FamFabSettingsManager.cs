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

        public string GetClaudeApiKey()
        {
            return _settings.ClaudeApiKey ?? "";
        }

        public void SetClaudeApiKey(string apiKey)
        {
            _settings.ClaudeApiKey = apiKey ?? "";
            SaveSettings();
        }

        public string GetModelName()
        {
            return _settings.ModelName ?? new FamFabSettings().ModelName;
        }

        public void SetModelName(string modelName)
        {
            _settings.ModelName = modelName ?? new FamFabSettings().ModelName;
            SaveSettings();
        }

        public List<string> GetAvailableModels()
        {
            List<string> models = _settings.AvailableModels;
            if (models == null || models.Count == 0)
                return new FamFabSettings().AvailableModels;
            return models;
        }

        public static string GetLogsFolderPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ArchSmarter", "FamFab", "Logs");
        }
    }
}
