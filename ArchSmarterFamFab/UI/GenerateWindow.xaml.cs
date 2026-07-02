using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArchSmarterFamFab.Data;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace ArchSmarterFamFab.UI
{
    public partial class GenerateWindow : Window
    {
        private readonly string _apiKey;
        private readonly FamFabSettingsManager _settingsManager;
        private readonly FamFabHistoryManager _historyManager;
        private readonly List<ImageInput> _images = new List<ImageInput>();
        private string _firstImagePath;

        public string FamilyJson { get; private set; }
        public IReadOnlyList<ImageInput> SourceImages { get; private set; }
        public string UserContext { get; private set; }
        public string FamilyName { get; private set; }

        // TripoSR results
        public string TripoSRMeshPath { get; private set; }
        public string TripoSRTexturePath { get; private set; }
        public bool IsTripoSRMode { get; private set; }

        public GenerateWindow(string apiKey, FamFabSettingsManager settingsManager)
        {
            InitializeComponent();
            _apiKey = apiKey;
            _settingsManager = settingsManager;
            _historyManager = new FamFabHistoryManager();

            CmbModel.ItemsSource = settingsManager.GetAvailableModels();
            CmbModel.SelectedItem = settingsManager.GetModelName();

            LoadHistory();
        }

        private void LoadHistory()
        {
            var entries = _historyManager.GetEntries();
            HistoryList.Children.Clear();

            if (entries.Count == 0)
            {
                HistoryPanel.Visibility = System.Windows.Visibility.Collapsed;
                if (NoHistoryHint != null)
                    NoHistoryHint.Visibility = System.Windows.Visibility.Visible;
                return;
            }

            HistoryPanel.Visibility = System.Windows.Visibility.Visible;
            if (NoHistoryHint != null)
                NoHistoryHint.Visibility = System.Windows.Visibility.Collapsed;

            foreach (var entry in entries)
            {
                var btn = CreateHistoryItem(entry);
                HistoryList.Children.Add(btn);
            }
        }

        private System.Windows.Controls.Button CreateHistoryItem(FamFabHistoryEntry entry)
        {
            var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical, Width = 80 };

            // Thumbnail
            var imgBorder = new Border
            {
                Width = 72,
                Height = 72,
                Background = (System.Windows.Media.Brush)FindResource("SurfaceLightBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("FamFabBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 0, 4)
            };

            if (!string.IsNullOrEmpty(entry.SourceImagePath) && File.Exists(entry.SourceImagePath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(entry.SourceImagePath);
                    bmp.DecodePixelWidth = 72;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    imgBorder.Child = new System.Windows.Controls.Image { Source = bmp, Stretch = Stretch.Uniform };
                }
                catch
                {
                    imgBorder.Child = new TextBlock
                    {
                        Text = "?",
                        Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center
                    };
                }
            }
            else
            {
                imgBorder.Child = new TextBlock
                {
                    Text = entry.GenerationMode == "triposr" ? "3D" : "AI",
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
            }

            panel.Children.Add(imgBorder);

            // Name
            string displayName = !string.IsNullOrEmpty(entry.FamilyName)
                ? entry.FamilyName
                : entry.GenerationMode == "triposr" ? "3D Mesh" : "Family";
            if (displayName.Length > 12)
                displayName = displayName.Substring(0, 10) + "..";

            panel.Children.Add(new TextBlock
            {
                Text = displayName,
                FontSize = 10,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            // Date
            panel.Children.Add(new TextBlock
            {
                Text = entry.Timestamp.ToString("MM/dd HH:mm"),
                FontSize = 9,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            });

            var btn = new System.Windows.Controls.Button
            {
                Content = panel,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(4),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = entry
            };

            btn.Click += HistoryItem_Click;
            return btn;
        }

        private void HistoryItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is FamFabHistoryEntry entry)
            {
                // Load the family JSON directly
                try
                {
                    if (!File.Exists(entry.FamilyJsonPath))
                    {
                        System.Windows.MessageBox.Show("The saved family JSON file no longer exists.", "FamFab",
                            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                        _historyManager.RemoveEntry(entry.Id);
                        LoadHistory();
                        return;
                    }

                    string json = File.ReadAllText(entry.FamilyJsonPath);
                    using var testParse = JsonDocument.Parse(json);
                    if (!testParse.RootElement.TryGetProperty("metadata", out _) ||
                        !testParse.RootElement.TryGetProperty("geometry", out _))
                    {
                        System.Windows.MessageBox.Show("The saved JSON no longer matches the family schema.", "FamFab",
                            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                        return;
                    }

                    FamilyJson = json;
                    FamilyName = entry.FamilyName;
                    TripoSRMeshPath = entry.MeshPath;
                    TripoSRTexturePath = entry.TexturePath;
                    IsTripoSRMode = entry.GenerationMode == "triposr";

                    // Load source image if available
                    if (!string.IsNullOrEmpty(entry.SourceImagePath) && File.Exists(entry.SourceImagePath))
                    {
                        try
                        {
                            byte[] bytes = File.ReadAllBytes(entry.SourceImagePath);
                            string mime = MimeFromPath(entry.SourceImagePath);
                            _images.Clear();
                            _images.Add(new ImageInput(bytes, mime));
                            SourceImages = _images.ToList();
                            _firstImagePath = entry.SourceImagePath;
                            TxtFilePath.Text = Path.GetFileName(entry.SourceImagePath);
                            LoadPreview(_firstImagePath);
                        }
                        catch { }
                    }

                    TxtFamilyName.Text = entry.FamilyName ?? "";
                    DialogResult = true;
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Failed to load history entry: {ex.Message}", "FamFab - Error",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show("Clear all recent generations? This will delete cached files.", "FamFab",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (result == System.Windows.MessageBoxResult.Yes)
            {
                _historyManager.ClearHistory();
                LoadHistory();
            }
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
                    byte[] bytes = File.ReadAllBytes(path);
                    _images.Add(new ImageInput(bytes, MimeFromPath(path)));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to read image '{path}': {ex.Message}");
                }
            }

            _firstImagePath = _images.Count > 0 ? dlg.FileNames[0] : null;
            TxtFilePath.Text = _images.Count == 1
                ? Path.GetFileName(dlg.FileNames[0])
                : $"{_images.Count} images selected";
            BtnGenerate.IsEnabled = _images.Count > 0;
            Btn3DGenerate.IsEnabled = _images.Count > 0;
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
            IsTripoSRMode = false;

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

                Dispatcher.Invoke(() =>
                {
                    SetGenerating(true, "Validating response...");

                    using JsonDocument testParse = JsonDocument.Parse(json);
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
                });
            }
            catch (LlmException ex)
            {
                Dispatcher.Invoke(() =>
                {
                    Debug.WriteLine($"Response: {ex.ResponseJson}");
                    System.Windows.MessageBox.Show($"Model API error: {ex.Message}{ex.Detail}",
                        "FamFab - Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    SetGenerating(false);
                });
            }
            catch (JsonException ex)
            {
                Dispatcher.Invoke(() =>
                {
                    System.Windows.MessageBox.Show($"Invalid JSON in API response: {ex.Message}",
                        "FamFab - Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    SetGenerating(false);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    System.Windows.MessageBox.Show($"Error: {ex.Message}",
                        "FamFab - Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    SetGenerating(false);
                });
            }
        }

        private async void Btn3DGenerate_Click(object sender, RoutedEventArgs e)
        {
            if (_images.Count == 0 || string.IsNullOrEmpty(_firstImagePath))
            {
                System.Windows.MessageBox.Show("Please select at least one image file.", "FamFab",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            SetGenerating(true, "Running TripoSR 3D reconstruction... This may take 3-5 minutes.");
            IsTripoSRMode = true;

            try
            {
                string outputDir = Path.Combine(
                    Path.GetTempPath(),
                    $"triposr_{DateTime.Now:yyyyMMdd_HHmmss}");

                string familyName = TxtFamilyName.Text?.Trim();

                var result = await Task.Run(() =>
                    TripoSRClient.Run(_firstImagePath, outputDir, familyName));

                if (!result.Success)
                {
                    Dispatcher.Invoke(() =>
                    {
                        System.Windows.MessageBox.Show($"TripoSR failed: {result.ErrorMessage}",
                            "FamFab - Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                        SetGenerating(false);
                    });
                    return;
                }

                // Build a minimal family JSON for the mesh
                string familyJson = BuildMeshFamilyJson(result, familyName);

                Dispatcher.Invoke(() =>
                {
                    FamilyJson = familyJson;
                    SourceImages = _images.ToList();
                    FamilyName = familyName;
                    TripoSRMeshPath = result.MeshPath;
                    TripoSRTexturePath = result.TexturePath;
                    DialogResult = true;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    System.Windows.MessageBox.Show($"TripoSR error: {ex.Message}",
                        "FamFab - Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    SetGenerating(false);
                });
            }
        }

        private static string BuildMeshFamilyJson(TripoSRResult result, string familyName)
        {
            using var stream = new System.IO.MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();

                writer.WritePropertyName("metadata");
                writer.WriteStartObject();
                writer.WriteString("schema_version", "0.1");
                writer.WriteString("name", familyName ?? "Imported Mesh");
                writer.WriteString("description", "3D mesh generated from image using TripoSR");
                writer.WriteString("category", "Generic Models");
                writer.WriteEndObject();

                writer.WriteString("units", "millimeters");

                writer.WritePropertyName("parameters");
                writer.WriteStartArray();
                writer.WriteEndArray();

                writer.WritePropertyName("reference_planes");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("name", "Center (Left/Right)");
                writer.WriteString("direction", "x");
                writer.WriteNumber("offset", 0);
                writer.WriteEndObject();
                writer.WriteStartObject();
                writer.WriteString("name", "Center (Front/Back)");
                writer.WriteString("direction", "y");
                writer.WriteNumber("offset", 0);
                writer.WriteEndObject();
                writer.WriteStartObject();
                writer.WriteString("name", "Ref. Level");
                writer.WriteString("direction", "z");
                writer.WriteNumber("offset", 0);
                writer.WriteEndObject();
                writer.WriteEndArray();

                writer.WritePropertyName("geometry");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("name", "MeshImport");
                writer.WriteString("type", "mesh_import");
                writer.WriteString("mesh_path", result.MeshPath);
                if (result.TexturePath != null)
                    writer.WriteString("texture_path", result.TexturePath);
                writer.WriteBoolean("is_void", false);
                writer.WriteEndObject();
                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
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
                string json = File.ReadAllText(dlg.FileName);
                using var testParse = JsonDocument.Parse(json);
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
            string extension = Path.GetExtension(path).ToLowerInvariant();
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
            BtnGenerate.IsEnabled = !generating && _images.Count > 0;
            Btn3DGenerate.IsEnabled = !generating && _images.Count > 0;
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
