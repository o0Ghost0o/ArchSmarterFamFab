using System.Windows;
using System.Windows.Controls;

namespace ArchSmarterFamFab.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly SettingsWindowViewModel _viewModel;
        private bool _showingKey = false;

        public SettingsWindow()
        {
            InitializeComponent();
            _viewModel = new SettingsWindowViewModel();
            DataContext = _viewModel;

            ApiKeyPasswordBox.Password = _viewModel.ApiKey;
        }

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                DragMove();
        }

        private void ApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_showingKey)
                _viewModel.ApiKey = ApiKeyPasswordBox.Password;
        }

        private void BtnToggleKey_Click(object sender, RoutedEventArgs e)
        {
            _showingKey = !_showingKey;

            if (_showingKey)
            {
                ApiKeyTextBox.Text = _viewModel.ApiKey;
                ApiKeyPasswordBox.Visibility = System.Windows.Visibility.Collapsed;
                ApiKeyTextBox.Visibility = System.Windows.Visibility.Visible;
                BtnToggleKey.Content = "Hide";
            }
            else
            {
                ApiKeyPasswordBox.Password = _viewModel.ApiKey;
                ApiKeyTextBox.Visibility = System.Windows.Visibility.Collapsed;
                ApiKeyPasswordBox.Visibility = System.Windows.Visibility.Visible;
                BtnToggleKey.Content = "Show";
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
