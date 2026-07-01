using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using ArchSmarterFamFab.Data;

namespace ArchSmarterFamFab.UI
{
    public partial class PreviewWindow : Window
    {
        private string _currentFamilyJson;
        private bool _webViewReady;
        private readonly string _provider;
        private readonly string _apiKey;
        private readonly string _modelName;
        private readonly byte[] _sourceImageBytes;
        private readonly string _sourceImageMimeType;
        private readonly string _sourceImagePath;

        public string FinalFamilyJson { get; private set; }
        public bool GenerateRequested { get; private set; }
        public string FamilyName { get; private set; }

        public PreviewWindow(string familyJson, string provider, string apiKey, string modelName,
            byte[] sourceImageBytes = null, string sourceImageMimeType = null,
            string sourceImagePath = null, string familyName = null)
        {
            InitializeComponent();
            _currentFamilyJson = familyJson;
            _provider = provider;
            _apiKey = apiKey;
            _modelName = modelName;
            _sourceImageBytes = sourceImageBytes;
            _sourceImageMimeType = sourceImageMimeType;
            _sourceImagePath = sourceImagePath;
            TxtFamilyName.Text = familyName ?? "";
            Loaded += PreviewWindow_Loaded;
        }

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async void PreviewWindow_Loaded(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Initializing 3D viewer...";
            UpdateMetadata();
            LoadSourceImagePreview();

            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ArchSmarter", "FamFab", "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await WebView.EnsureCoreWebView2Async(env);

                WebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                string viewerHtml = SkillResources.GetViewerHtml();
                viewerHtml = InjectHostBridge(viewerHtml);

                WebView.CoreWebView2.NavigateToString(viewerHtml);
                WebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"WebView2 init failed: {ex.Message}";
            }
        }

        private void LoadSourceImagePreview()
        {
            if (_sourceImageBytes == null || _sourceImageBytes.Length == 0) return;
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new System.IO.MemoryStream(_sourceImageBytes);
                bmp.DecodePixelWidth = 300;
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                SourceImagePreview.Source = bmp;
                ImageBorder.Visibility = System.Windows.Visibility.Visible;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load source image preview: {ex.Message}");
            }
        }

        private void ImageBorder_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!string.IsNullOrEmpty(_sourceImagePath) && File.Exists(_sourceImagePath))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _sourceImagePath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to open source image: {ex.Message}");
                }
            }
        }

        private void UpdateMetadata()
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(_currentFamilyJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("metadata", out var meta))
                {
                    TxtDescription.Text = meta.TryGetProperty("description", out var desc)
                        ? desc.GetString() : "—";
                    TxtCategory.Text = meta.TryGetProperty("category", out var cat)
                        ? cat.GetString() : "—";
                }

                if (root.TryGetProperty("parameters", out var parms) && parms.ValueKind == JsonValueKind.Array)
                    TxtParamCount.Text = parms.GetArrayLength().ToString();

                int geomCount = 0;
                if (root.TryGetProperty("geometry", out var geom) && geom.ValueKind == JsonValueKind.Array)
                    geomCount = geom.GetArrayLength();
                TxtGeomCount.Text = $"{geomCount} element{(geomCount != 1 ? "s" : "")}";
            }
            catch
            {
                TxtDescription.Text = "—";
                TxtCategory.Text = "—";
                TxtParamCount.Text = "—";
                TxtGeomCount.Text = "—";
            }
        }

        private void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                _webViewReady = true;
                LoadFamilyIntoViewer();
                StatusText.Text = "Ready.";
            }
            else
            {
                StatusText.Text = "Failed to load viewer.";
            }
        }

        private async void LoadFamilyIntoViewer()
        {
            if (!_webViewReady || string.IsNullOrEmpty(_currentFamilyJson)) return;

            string escapedJson = _currentFamilyJson
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");

            string script = $"try {{ loadFamilyData(JSON.parse('{escapedJson}')); }} catch(e) {{ console.error('Load error:', e); }}";
            await WebView.CoreWebView2.ExecuteScriptAsync(script);
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.WebMessageAsJson;
                using JsonDocument doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeEl))
                {
                    string msgType = typeEl.GetString();
                    if ((msgType == "generate" || msgType == "paramUpdate") &&
                        root.TryGetProperty("json", out var jsonEl))
                    {
                        _currentFamilyJson = jsonEl.GetRawText();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WebMessage error: {ex.Message}");
            }
        }

        private async void BtnRefine_Click(object sender, RoutedEventArgs e)
        {
            string instruction = TxtPrompt.Text?.Trim();
            if (string.IsNullOrEmpty(instruction))
                return;

            SetRefining(true, $"Sending refinements to {LlmProviders.DisplayName(_provider)}. This may take a few minutes . . .");

            try
            {
                string skillPrompt = SkillResources.GetSkillPrompt();
                string schema = SkillResources.GetFamilySchema();

                var client = LlmClientFactory.Create(_provider, _apiKey, _modelName);
                string newJson = await Task.Run(() =>
                    client.RefineFamilyAsync(_currentFamilyJson, instruction, skillPrompt, schema,
                        _sourceImageBytes, _sourceImageMimeType));

                using JsonDocument testParse = JsonDocument.Parse(newJson);
                if (!testParse.RootElement.TryGetProperty("metadata", out _) ||
                    !testParse.RootElement.TryGetProperty("geometry", out _))
                {
                    System.Windows.MessageBox.Show(
                        "API returned JSON but it doesn't match the family schema.",
                        "FamFab - Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    SetRefining(false);
                    return;
                }

                _currentFamilyJson = newJson;
                UpdateMetadata();
                LoadFamilyIntoViewer();
                TxtPrompt.Clear();
                StatusText.Text = "Family updated.";
            }
            catch (LlmException ex)
            {
                Debug.WriteLine($"Response: {ex.ResponseJson}");
                System.Windows.MessageBox.Show($"Model API error: {ex.Message}",
                    "FamFab - Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            catch (JsonException ex)
            {
                System.Windows.MessageBox.Show($"Invalid JSON in API response: {ex.Message}",
                    "FamFab - Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error: {ex.Message}",
                    "FamFab - Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                SetRefining(false);
            }
        }

        private void SetRefining(bool refining, string status = null)
        {
            BtnRefine.IsEnabled = !refining;
            BtnGenerate.IsEnabled = !refining;
            TxtPrompt.IsEnabled = !refining;
            RefineProgress.Visibility = refining ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (status != null)
                RefineStatusText.Text = status;
        }

        private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Capturing current state...";

            try
            {
                string result = await WebView.CoreWebView2.ExecuteScriptAsync("JSON.stringify(currentFamily)");
                if (result != null && result != "null")
                {
                    string unescaped = JsonSerializer.Deserialize<string>(result);
                    if (!string.IsNullOrEmpty(unescaped))
                        _currentFamilyJson = unescaped;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting current family state: {ex.Message}");
            }

            FinalFamilyJson = _currentFamilyJson;
            FamilyName = TxtFamilyName.Text?.Trim();
            GenerateRequested = true;
            DialogResult = true;
            Close();
        }

        private static string InjectHostBridge(string html)
        {
            string bridgeScript = @"
<script>
// Bridge for WebView2 communication
window.famfabBridge = {
    sendToHost: function(type, data) {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(JSON.stringify({ type: type, json: data }));
        }
    }
};

// Override parameter change to notify host
var _originalRenderParams = typeof renderParams === 'function' ? renderParams : null;
</script>";

            int bodyClose = html.LastIndexOf("</body>");
            if (bodyClose > 0)
                return html.Insert(bodyClose, bridgeScript);

            return html + bridgeScript;
        }
    }
}
