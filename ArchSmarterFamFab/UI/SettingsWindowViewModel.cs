using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ArchSmarterFamFab.Data;

namespace ArchSmarterFamFab.UI
{
    public class SettingsWindowViewModel : INotifyPropertyChanged
    {
        private readonly FamFabSettingsManager _settingsManager;

        public SettingsWindowViewModel()
        {
            _settingsManager = new FamFabSettingsManager();

            _apiKey = _settingsManager.GetClaudeApiKey();
            _selectedModel = _settingsManager.GetModelName();
            AvailableModels = new ObservableCollection<string>(_settingsManager.GetAvailableModels());

            UpdateStatus();
        }

        private string _apiKey;
        public string ApiKey
        {
            get => _apiKey;
            set
            {
                _apiKey = value;
                _settingsManager.SetClaudeApiKey(value);
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
                _settingsManager.SetModelName(value);
                OnPropertyChanged();
            }
        }

        public ObservableCollection<string> AvailableModels { get; }

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        private void UpdateStatus()
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
                StatusText = "Enter your Claude API key to get started.";
            else if (_apiKey.StartsWith("sk-ant-"))
                StatusText = "API key configured.";
            else
                StatusText = "API key set (verify it starts with sk-ant-).";
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
