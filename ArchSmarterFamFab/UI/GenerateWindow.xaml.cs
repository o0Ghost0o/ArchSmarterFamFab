using System.Collections.Generic;
using System.Linq;
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
        private readonly List<ImageInput> _images = new List<ImageInput>();
        private string _firstImagePath;

        public string FamilyJson { get; private set; }
        public IReadOnlyList<ImageInput> SourceImages { get; private set; }
        public string UserContext { get; private set; }
        public string FamilyName { get; private set; }

        public GenerateWindow(string apiKey, FamFabSettingsManager settingsManager)
        {
            InitializeComponent();
            _apiKey = apiKey;
            _settingsManager = settingsManager;

            CmbModel.ItemsSource = settingsManager.GetAvailableModels();
            CmbModel.SelectedItem = settingsManager.GetModelName();
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
                Title = "Select one or more images of the object to create",
                Filter = "Image Files|*.jpg;*.jpeg;*.png;*.webp|All Files|*.*",
                Multiselect = true
            };

            if (dlg.ShowDialog() != true)
                return;

            _images.Clear();
            foreach (string path in dlg.FileNames)
            {
                try
                {
                    byte[] bytes = System.IO.File.ReadAllBytes(path);
                    _images.Add(new ImageInput(bytes, MimeFromPath(path)));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to read image '{path}': {ex.Message}");
                }
            }

            _firstImagePath = _images.Count > 0 ? dlg.FileNames[0] : null;
            TxtFilePath.Text = _images.Count == 1
                ? System.IO.Path.GetFileName(dlg.FileNames[0])
                : $"{_images.Count} images selected";
            BtnGenerate.IsEnabled = _images.Count > 0;
            LoadPreview(_firstImagePath);
        }

        private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            if (_images.Count == 0)
            {
                System.Windows.MessageBox.Show("Please select at least one image file.", "FamFab",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            string modelName = CmbModel.SelectedItem as string ?? _settingsManager.GetModelName();
            _settingsManager.SetModelName(modelName);

            SetGenerating(true, "Analyzing image(s)...");

            try
            {
                string skillPrompt = SkillResources.GetSkillPrompt();
                string schema = SkillResources.GetFamilySchema();

                string plural = _images.Count == 1 ? "image" : "images";
                SetGenerating(true, $"Sending {_images.Count} {plural} to {LlmProviders.DisplayName(_settingsManager.GetProvider())} ({modelName}). This may take a few minutes . . .");

                string userContext = TxtContext.Text?.Trim();
                var images = _images.ToList();

                var client = LlmClientFactory.Create(_settingsManager.GetProvider(), _apiKey, modelName);
                string json = await Task.Run(() =>
                    client.GenerateFamilyFromImagesAsync(images, skillPrompt, schema, userContext));

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
                SourceImages = images;
                UserContext = userContext;
                FamilyName = TxtFamilyName.Text?.Trim();
                DialogResult = true;
            }
            catch (LlmException ex)
            {
                Debug.WriteLine($"Response: {ex.ResponseJson}");
                System.Windows.MessageBox.Show($"Model API error: {ex.Message}{ex.Detail}",
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

        private static string MimeFromPath(string path)
        {
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };
        }

        private void LoadPreview(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath))
            {
                PreviewBorder.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }

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
