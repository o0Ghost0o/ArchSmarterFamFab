using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ArchSmarterFamFab.Data;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace ArchSmarterFamFab.UI
{
    public partial class GenerateWindow : Window
    {
        private readonly string _apiKey;
        private readonly FamFabSettingsManager _settingsManager;

        public string FamilyJson { get; private set; }
        public byte[] SourceImageBytes { get; private set; }
        public string SourceImageMimeType { get; private set; }
        public string UserContext { get; private set; }
        public string FamilyName { get; private set; }

        public GenerateWindow(string apiKey, FamFabSettingsManager settingsManager)
        {
            InitializeComponent();
            _apiKey = apiKey;
            _settingsManager = settingsManager;

            var settings = settingsManager.GetSettings();
            CmbModel.ItemsSource = settings.AvailableModels;
            CmbModel.SelectedItem = settings.ModelName;
        }

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select an image of the object to create",
                Filter = "Image Files|*.jpg;*.jpeg;*.png;*.webp|All Files|*.*",
                Multiselect = false
            };

            if (dlg.ShowDialog() == true)
            {
                TxtFilePath.Text = dlg.FileName;
                BtnGenerate.IsEnabled = true;
                LoadPreview(dlg.FileName);
            }
        }

        private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            string imagePath = TxtFilePath.Text;
            if (string.IsNullOrWhiteSpace(imagePath) || !System.IO.File.Exists(imagePath))
            {
                System.Windows.MessageBox.Show("Please select a valid image file.", "FamFab",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            string modelName = CmbModel.SelectedItem as string ?? "claude-sonnet-4-20250514";
            _settingsManager.SetModelName(modelName);

            SetGenerating(true, "Analyzing image...");

            try
            {
                string extension = System.IO.Path.GetExtension(imagePath).ToLowerInvariant();
                string mimeType = extension switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };

                byte[] imageBytes = System.IO.File.ReadAllBytes(imagePath);
                string skillPrompt = SkillResources.GetSkillPrompt();
                string schema = SkillResources.GetFamilySchema();

                SetGenerating(true, $"Sending image to Claude API ({modelName}). This may take a few minutes . . .");

                string userContext = TxtContext.Text?.Trim();

                var client = new ClaudeClient(_apiKey, modelName);
                string json = await Task.Run(() =>
                    client.GenerateFamilyFromImageAsync(imageBytes, mimeType, skillPrompt, schema, userContext));

                SetGenerating(true, "Validating response...");

                using System.Text.Json.JsonDocument testParse = System.Text.Json.JsonDocument.Parse(json);
                if (!testParse.RootElement.TryGetProperty("metadata", out _) ||
                    !testParse.RootElement.TryGetProperty("geometry", out _))
                {
                    System.Windows.MessageBox.Show(
                        "API returned JSON but it doesn't match the family schema.",
                        "FamFab - Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    SetGenerating(false);
                    return;
                }

                FamilyJson = json;
                SourceImageBytes = imageBytes;
                SourceImageMimeType = mimeType;
                UserContext = userContext;
                FamilyName = TxtFamilyName.Text?.Trim();
                DialogResult = true;
            }
            catch (ClaudeException ex)
            {
                Debug.WriteLine($"Response: {ex.ResponseJson}");
                System.Windows.MessageBox.Show($"Claude API error: {ex.Message}",
                    "FamFab - Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                SetGenerating(false);
            }
            catch (System.Text.Json.JsonException ex)
            {
                System.Windows.MessageBox.Show($"Invalid JSON in API response: {ex.Message}",
                    "FamFab - Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                SetGenerating(false);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error: {ex.Message}",
                    "FamFab - Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                SetGenerating(false);
            }
        }

        private void BtnLoadJson_Click(object sender, RoutedEventArgs e)
        {
            string logsFolder = FamFabSettingsManager.GetLogsFolderPath();

            var dlg = new OpenFileDialog
            {
                Title = "Load a previous family JSON",
                Filter = "JSON Files|*.json|All Files|*.*",
                InitialDirectory = Directory.Exists(logsFolder) ? logsFolder : null
            };

            if (dlg.ShowDialog() != true)
                return;

            try
            {
                string json = System.IO.File.ReadAllText(dlg.FileName);
                using var testParse = System.Text.Json.JsonDocument.Parse(json);
                if (!testParse.RootElement.TryGetProperty("metadata", out _) ||
                    !testParse.RootElement.TryGetProperty("geometry", out _))
                {
                    System.Windows.MessageBox.Show(
                        "This JSON file doesn't match the family schema (missing metadata or geometry).",
                        "FamFab", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                FamilyJson = json;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to load JSON: {ex.Message}",
                    "FamFab - Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void LoadPreview(string imagePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath);
                bitmap.DecodePixelWidth = 400;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                ImagePreview.Source = bitmap;
                PreviewBorder.Visibility = System.Windows.Visibility.Visible;
            }
            catch
            {
                PreviewBorder.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private void SetGenerating(bool generating, string status = null)
        {
            BtnGenerate.IsEnabled = !generating;
            BtnBrowse.IsEnabled = !generating;
            CmbModel.IsEnabled = !generating;
            TxtFamilyName.IsEnabled = !generating;
            TxtContext.IsEnabled = !generating;
            ProgressPanel.Visibility = generating ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (status != null)
                StatusLabel.Text = status;
        }
    }
}
