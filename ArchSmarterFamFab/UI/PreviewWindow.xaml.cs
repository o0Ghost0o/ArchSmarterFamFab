using System.Collections.Generic;
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
        private readonly IReadOnlyList<ImageInput> _sourceImages;
        private readonly string _sourceImagePath;
        private readonly string _meshPath;
        private readonly string _texturePath;
        private readonly bool _isMeshMode;

        public string FinalFamilyJson { get; private set; }
        public bool GenerateRequested { get; private set; }
        public string FamilyName { get; private set; }

        public PreviewWindow(string familyJson, string provider, string apiKey, string modelName,
            IReadOnlyList<ImageInput> sourceImages = null,
            string sourceImagePath = null, string familyName = null,
            string meshPath = null, string texturePath = null, bool isMeshMode = false)
        {
            InitializeComponent();
            _currentFamilyJson = familyJson;
            _provider = provider;
            _apiKey = apiKey;
            _modelName = modelName;
            _sourceImages = sourceImages;
            _sourceImagePath = sourceImagePath;
            _meshPath = meshPath;
            _texturePath = texturePath;
            _isMeshMode = isMeshMode;
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

                // Enable DevTools for debugging (remove in production)
                WebView.CoreWebView2.OpenDevToolsWindow();

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
            if (_sourceImages == null || _sourceImages.Count == 0) return;
            byte[] bytes = _sourceImages[0].Bytes;
            if (bytes == null || bytes.Length == 0) return;
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new System.IO.MemoryStream(bytes);
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
                if (_isMeshMode && !string.IsNullOrEmpty(_meshPath))
                {
                    LoadMeshIntoViewer();
                }
                else
                {
                    LoadFamilyIntoViewer();
                }
                StatusText.Text = _isMeshMode ? "TripoSR mesh loaded." : "Ready.";
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

        private async void LoadMeshIntoViewer()
        {
            if (!_webViewReady || string.IsNullOrEmpty(_meshPath)) return;

            try
            {
                // Copy mesh to a temp location and set up virtual host mapping
                string tempDir = Path.Combine(Path.GetTempPath(), "famfab_mesh_viewer");
                Directory.CreateDirectory(tempDir);
                string tempMesh = Path.Combine(tempDir, "mesh.obj");
                File.Copy(_meshPath, tempMesh, true);

                string tempTexture = null;
                if (!string.IsNullOrEmpty(_texturePath) && File.Exists(_texturePath))
                {
                    tempTexture = Path.Combine(tempDir, "texture.png");
                    File.Copy(_texturePath, tempTexture, true);
                }

                // Use virtual host mapping instead of file:// URLs (more reliable in WebView2)
                WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "famfab.mesh", tempDir, CoreWebView2HostResourceAccessKind.Allow);

                string meshUrl = "https://famfab.mesh/mesh.obj";
                string textureUrl = tempTexture != null ? "https://famfab.mesh/texture.png" : "";

                // First test if the virtual host mapping works, then load mesh
                string script = $@"
try {{
    async function loadMesh() {{
        console.log('Testing mesh URL: {meshUrl}');
        
        // Step 1: Test if file is accessible
        try {{
            const resp = await fetch('{meshUrl}');
            if (!resp.ok) {{
                throw new Error('HTTP ' + resp.status);
            }}
            const text = await resp.text();
            console.log('OBJ file fetched, length:', text.length);
            
            // Step 2: Try THREE.OBJLoader if available
            if (typeof THREE !== 'undefined' && typeof THREE.OBJLoader !== 'undefined') {{
                console.log('Using THREE.OBJLoader');
                var loader = new THREE.OBJLoader();
                loader.load('{meshUrl}', function(object) {{
                    console.log('OBJLoader success, children:', object.children.length);
                    addMeshToScene(object);
                }}, undefined, function(err) {{
                    console.warn('OBJLoader failed, falling back to manual parser:', err);
                    parseAndDisplayOBJ(text);
                }});
            }} else {{
                console.log('OBJLoader not available, using manual parser');
                parseAndDisplayOBJ(text);
            }}
        }} catch(fetchErr) {{
            console.error('Fetch failed:', fetchErr);
            document.getElementById('errorBanner').textContent = 'Cannot access mesh file: ' + fetchErr.message;
            document.getElementById('errorBanner').classList.add('show');
        }}
    }}
    
    function addMeshToScene(object) {{
        object.traverse(function(child) {{
            if (child.isMesh) {{
                child.material = new THREE.MeshStandardMaterial({{
                    color: 0xb8c3d0,
                    roughness: 0.55,
                    metalness: 0.05
                }});
                {(tempTexture != null ? $@"
                var texLoader = new THREE.TextureLoader();
                texLoader.load('{textureUrl}', function(texture) {{
                    child.material.map = texture;
                    child.material.needsUpdate = true;
                }}, undefined, function(err) {{
                    console.warn('Texture load failed:', err);
                }});" : "")}
            }}
        }});
        scene.add(object);
        var box = new THREE.Box3().setFromObject(object);
        var center = new THREE.Vector3();
        box.getCenter(center);
        var size = new THREE.Vector3();
        box.getSize(size);
        var maxDim = Math.max(size.x, size.y, size.z);
        target.copy(center);
        sphericalRadius = maxDim * 2.5;
        updateCameraFromSpherical();
        document.getElementById('hudElements').textContent = object.children.length;
        console.log('Mesh centered, size:', maxDim);
    }}
    
    function parseAndDisplayOBJ(text) {{
        // Minimal OBJ parser
        var lines = text.split(/\r?\n/);
        var vertices = [];
        var uvs = [];
        var normals = [];
        var faces = [];
        
        for (var i = 0; i < lines.length; i++) {{
            var line = lines[i].trim();
            if (line.length === 0 || line.charAt(0) === '#') continue;
            var parts = line.split(/\s+/);
            if (parts[0] === 'v') {{
                vertices.push(new THREE.Vector3(parseFloat(parts[1]), parseFloat(parts[2]), parseFloat(parts[3])));
            }} else if (parts[0] === 'vt') {{
                uvs.push(new THREE.Vector2(parseFloat(parts[1]), parseFloat(parts[2])));
            }} else if (parts[0] === 'vn') {{
                normals.push(new THREE.Vector3(parseFloat(parts[1]), parseFloat(parts[2]), parseFloat(parts[3])));
            }} else if (parts[0] === 'f') {{
                var face = [];
                for (var j = 1; j < parts.length; j++) {{
                    var indices = parts[j].split('/');
                    face.push({{
                        v: parseInt(indices[0]) - 1,
                        vt: indices.length > 1 && indices[1] ? parseInt(indices[1]) - 1 : -1,
                        vn: indices.length > 2 && indices[2] ? parseInt(indices[2]) - 1 : -1
                    }});
                }}
                faces.push(face);
            }}
        }}
        
        console.log('Manual parse: ' + vertices.length + ' verts, ' + faces.length + ' faces');
        
        if (vertices.length === 0 || faces.length === 0) {{
            document.getElementById('errorBanner').textContent = 'OBJ file has no geometry';
            document.getElementById('errorBanner').classList.add('show');
            return;
        }}
        
        // Triangulate and build geometry
        var positions = [];
        var normalsArr = [];
        var uvsArr = [];
        
        for (var fi = 0; fi < faces.length; fi++) {{
            var face = faces[fi];
            // Simple fan triangulation for convex polygons
            for (var ti = 1; ti < face.length - 1; ti++) {{
                var tri = [face[0], face[ti], face[ti + 1]];
                for (var vi = 0; vi < 3; vi++) {{
                    var idx = tri[vi].v;
                    var v = vertices[idx];
                    positions.push(v.x, v.y, v.z);
                    
                    if (tri[vi].vn >= 0 && normals[tri[vi].vn]) {{
                        var n = normals[tri[vi].vn];
                        normalsArr.push(n.x, n.y, n.z);
                    }} else {{
                        normalsArr.push(0, 0, 1);
                    }}
                    
                    if (tri[vi].vt >= 0 && uvs[tri[vi].vt]) {{
                        var uv = uvs[tri[vi].vt];
                        uvsArr.push(uv.x, uv.y);
                    }} else {{
                        uvsArr.push(0, 0);
                    }}
                }}
            }}
        }}
        
        var geo = new THREE.BufferGeometry();
        geo.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3));
        if (normalsArr.length > 0) geo.setAttribute('normal', new THREE.Float32BufferAttribute(normalsArr, 3));
        if (uvsArr.length > 0) geo.setAttribute('uv', new THREE.Float32BufferAttribute(uvsArr, 2));
        geo.computeVertexNormals();
        
        var mat = new THREE.MeshStandardMaterial({{
            color: 0xb8c3d0,
            roughness: 0.55,
            metalness: 0.05
        }});
        
        {(tempTexture != null ? $@"
        var texLoader = new THREE.TextureLoader();
        texLoader.load('{textureUrl}', function(texture) {{
            mat.map = texture;
            mat.needsUpdate = true;
        }}, undefined, function(err) {{
            console.warn('Texture load failed:', err);
        }});" : "")}
        
        var mesh = new THREE.Mesh(geo, mat);
        scene.add(mesh);
        
        var box = new THREE.Box3().setFromObject(mesh);
        var center = new THREE.Vector3();
        box.getCenter(center);
        var size = new THREE.Vector3();
        box.getSize(size);
        var maxDim = Math.max(size.x, size.y, size.z);
        target.copy(center);
        sphericalRadius = maxDim * 2.5;
        updateCameraFromSpherical();
        document.getElementById('hudElements').textContent = '1';
        console.log('Manual mesh centered, size:', maxDim);
    }}
    
    loadMesh();
}} catch(e) {{
    console.error('Mesh load script error:', e);
    document.getElementById('errorBanner').textContent = 'Mesh load error: ' + e.message;
    document.getElementById('errorBanner').classList.add('show');
}}";

                await WebView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load mesh: {ex.Message}");
                StatusText.Text = $"Mesh load failed: {ex.Message}";
            }
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
            if (_isMeshMode)
            {
                System.Windows.MessageBox.Show("Refine is not available for 3D mesh mode. Use the AI Generate mode instead.",
                    "FamFab", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

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
                    client.RefineFamilyAsync(_currentFamilyJson, instruction, skillPrompt, schema, _sourceImages));

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
                System.Windows.MessageBox.Show($"Model API error: {ex.Message}{ex.Detail}",
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
