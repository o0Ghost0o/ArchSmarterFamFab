using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using ArchSmarterFamFab.Data;

namespace ArchSmarterFamFab.UI
{
    public class SettingsWindowViewModel : INotifyPropertyChanged
    {
        private readonly FamFabSettingsManager _settingsManager;

        /// <summary>Raised when the visible API key changes because the provider switched,
        /// so the view can resync the (unbound) PasswordBox.</summary>
        public event Action ApiKeyRefreshed;

        public SettingsWindowViewModel()
        {
            _settingsManager = new FamFabSettingsManager();

            ProviderOptions = new ObservableCollection<string>(
                LlmProviders.All.Select(LlmProviders.DisplayName));

            _selectedProviderDisplay = LlmProviders.DisplayName(_settingsManager.GetProvider());
            _apiKey = _settingsManager.GetApiKey();
            AvailableModels = new ObservableCollection<string>(_settingsManager.GetAvailableModels());
            _selectedModel = _settingsManager.GetModelName();

            UpdateKeyLabel();
            UpdateStatus();
        }

        public ObservableCollection<string> ProviderOptions { get; }

        private string _selectedProviderDisplay;
        public string SelectedProviderDisplay
        {
            get => _selectedProviderDisplay;
            set
            {
                if (_selectedProviderDisplay == value || string.IsNullOrEmpty(value)) return;
                _selectedProviderDisplay = value;
                _settingsManager.SetProvider(LlmProviders.FromDisplay(value));
                OnPropertyChanged();

                // Refresh everything that depends on the active provider.
                _apiKey = _settingsManager.GetApiKey();
                OnPropertyChanged(nameof(ApiKey));

                AvailableModels.Clear();
                foreach (string model in _settingsManager.GetAvailableModels())
                    AvailableModels.Add(model);
                _selectedModel = _settingsManager.GetModelName();
                OnPropertyChanged(nameof(SelectedModel));

                UpdateKeyLabel();
                UpdateStatus();
                ApiKeyRefreshed?.Invoke();
            }
        }

        private string _apiKey;
        public string ApiKey
        {
            get => _apiKey;
            set
            {
                _apiKey = value;
                _settingsManager.SetApiKey(value);
                UpdateStatus();
                OnPropertyChanged();
            }
        }

        private string _selectedModel;
        public string SelectedModel
        {
            get => _selectedModel;
            set
            {
                _selectedModel = value;
                if (!string.IsNullOrEmpty(value))
                    _settingsManager.SetModelName(value);
                OnPropertyChanged();
            }
        }

        public ObservableCollection<string> AvailableModels { get; }

        private string _keyLabel;
        public string KeyLabel
        {
            get => _keyLabel;
            set { _keyLabel = value; OnPropertyChanged(); }
        }

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        private void UpdateKeyLabel()
        {
            KeyLabel = LlmProviders.KeyLabel(_settingsManager.GetProvider());
        }

        private void UpdateStatus()
        {
            string provider = _settingsManager.GetProvider();
            if (string.IsNullOrWhiteSpace(_apiKey))
                StatusText = $"Enter your {LlmProviders.DisplayName(provider)} API key to get started.";
            else
                StatusText = "API key configured. " + LlmProviders.KeyHint(provider);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
