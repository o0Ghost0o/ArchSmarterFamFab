namespace ArchSmarterFamFab.Data
{
    public class FamFabHistoryEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string FamilyName { get; set; } = "";
        public string SourceImagePath { get; set; } = "";
        public string MeshPath { get; set; } = "";
        public string TexturePath { get; set; } = "";
        public string FamilyJsonPath { get; set; } = "";
        public string GenerationMode { get; set; } = "llm"; // "llm" or "triposr"
        public int VertexCount { get; set; }
        public int FaceCount { get; set; }
    }

    public class FamFabHistory
    {
        public List<FamFabHistoryEntry> Entries { get; set; } = new List<FamFabHistoryEntry>();
        public int MaxEntries { get; set; } = 20;
    }

    public class FamFabHistoryManager
    {
        private static readonly string HistoryFileName = "history.json";
        private readonly string _filePath;
        private FamFabHistory _history;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = null
        };

        public FamFabHistoryManager()
        {
            _filePath = GetHistoryFilePath();
            _history = LoadHistory();
        }

        public static string GetHistoryFilePath()
        {
            string folderPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ArchSmarter", "FamFab");
            return Path.Combine(folderPath, HistoryFileName);
        }

        public static string GetHistoryFolderPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ArchSmarter", "FamFab", "History");
        }

        private FamFabHistory LoadHistory()
        {
            if (!File.Exists(_filePath))
            {
                return new FamFabHistory();
            }

            try
            {
                string json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<FamFabHistory>(json) ?? new FamFabHistory();
            }
            catch (Exception)
            {
                return new FamFabHistory();
            }
        }

        public void SaveHistory()
        {
            try
            {
                string folderPath = Path.GetDirectoryName(_filePath);
                if (!Directory.Exists(folderPath))
                    Directory.CreateDirectory(folderPath);

                // Trim to max entries
                while (_history.Entries.Count > _history.MaxEntries)
                {
                    var oldest = _history.Entries.OrderBy(e => e.Timestamp).FirstOrDefault();
                    if (oldest != null)
                    {
                        _history.Entries.Remove(oldest);
                        TryDeleteEntryFiles(oldest);
                    }
                }

                string json = JsonSerializer.Serialize(_history, JsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving history: {ex.Message}");
            }
        }

        private void TryDeleteEntryFiles(FamFabHistoryEntry entry)
        {
            try
            {
                if (!string.IsNullOrEmpty(entry.MeshPath) && File.Exists(entry.MeshPath))
                    File.Delete(entry.MeshPath);
                if (!string.IsNullOrEmpty(entry.TexturePath) && File.Exists(entry.TexturePath))
                    File.Delete(entry.TexturePath);
                if (!string.IsNullOrEmpty(entry.FamilyJsonPath) && File.Exists(entry.FamilyJsonPath))
                    File.Delete(entry.FamilyJsonPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error cleaning up history entry files: {ex.Message}");
            }
        }

        public IReadOnlyList<FamFabHistoryEntry> GetEntries()
        {
            // Filter out entries with missing files
            return _history.Entries
                .Where(e => !string.IsNullOrEmpty(e.FamilyJsonPath) && File.Exists(e.FamilyJsonPath))
                .OrderByDescending(e => e.Timestamp)
                .ToList();
        }

        public FamFabHistoryEntry AddEntry(string familyName, string sourceImagePath,
            string meshPath, string texturePath, string familyJsonPath,
            string generationMode, int vertexCount = 0, int faceCount = 0)
        {
            var entry = new FamFabHistoryEntry
            {
                FamilyName = familyName ?? "",
                SourceImagePath = sourceImagePath ?? "",
                MeshPath = meshPath ?? "",
                TexturePath = texturePath ?? "",
                FamilyJsonPath = familyJsonPath ?? "",
                GenerationMode = generationMode ?? "llm",
                VertexCount = vertexCount,
                FaceCount = faceCount
            };

            _history.Entries.Add(entry);
            SaveHistory();
            return entry;
        }

        public void RemoveEntry(string id)
        {
            var entry = _history.Entries.FirstOrDefault(e => e.Id == id);
            if (entry != null)
            {
                _history.Entries.Remove(entry);
                TryDeleteEntryFiles(entry);
                SaveHistory();
            }
        }

        public void ClearHistory()
        {
            foreach (var entry in _history.Entries.ToList())
            {
                TryDeleteEntryFiles(entry);
            }
            _history.Entries.Clear();
            SaveHistory();
        }
    }
}
