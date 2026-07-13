using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ArchSmarterFamFab.Data;

namespace ArchSmarterFamFab.UI
{
    public partial class InstallerWindow : Window
    {
        private readonly TripoSRDependencyInstaller.EnvironmentInfo _environment;
        private CancellationTokenSource _cts;
        private bool _installationComplete;

        public InstallerWindow(TripoSRDependencyInstaller.EnvironmentInfo environment)
        {
            InitializeComponent();
            _environment = environment;
            _cts = new CancellationTokenSource();

            HeaderLabel.Text = "TripoSR dependencies are missing";
            SubHeaderLabel.Text = $"FamFab can download and install the required Python packages automatically. This may take several minutes.{Environment.NewLine}{Environment.NewLine}Project: {environment.ProjectDir}";

            if (!string.IsNullOrEmpty(environment.UvExe))
                AppendLog($"Installer: uv found at {environment.UvExe}");
            else if (!string.IsNullOrEmpty(environment.PythonExe))
                AppendLog($"Installer: Python found at {environment.PythonExe}");
            else
                AppendLog("Installer: WARNING - neither uv nor Python was found.");
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            DialogResult = _installationComplete;
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            BtnInstall.IsEnabled = false;
            BtnCancel.IsEnabled = true;
            BtnContinue.Visibility = System.Windows.Visibility.Collapsed;
            ErrorLabel.Visibility = System.Windows.Visibility.Collapsed;
            ProgressBar.Visibility = System.Windows.Visibility.Visible;
            _cts = new CancellationTokenSource();

            var progress = new Progress<string>(line =>
            {
                Dispatcher.Invoke(() => AppendLog(line));
            });

            try
            {
                bool success = await Task.Run(() =>
                    TripoSRDependencyInstaller.InstallAsync(_environment, progress, _cts.Token),
                    _cts.Token);

                if (success)
                {
                    _installationComplete = true;
                    HeaderLabel.Text = "Installation complete";
                    SubHeaderLabel.Text = "TripoSR dependencies are installed. Click Continue to open FamFab.";
                    AppendLog("Installation succeeded.");
                    BtnInstall.Visibility = System.Windows.Visibility.Collapsed;
                    BtnCancel.Visibility = System.Windows.Visibility.Collapsed;
                    BtnContinue.Visibility = System.Windows.Visibility.Visible;
                    ProgressBar.Visibility = System.Windows.Visibility.Collapsed;
                }
                else
                {
                    ShowError("Installation failed. Check the log for details.");
                    BtnInstall.IsEnabled = true;
                    ProgressBar.Visibility = System.Windows.Visibility.Collapsed;
                }
            }
            catch (OperationCanceledException)
            {
                AppendLog("Installation was cancelled.");
                BtnInstall.IsEnabled = true;
                ProgressBar.Visibility = System.Windows.Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                ShowError($"Installation error: {ex.Message}");
                BtnInstall.IsEnabled = true;
                ProgressBar.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            DialogResult = false;
        }

        private void BtnContinue_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void AppendLog(string line)
        {
            if (Dispatcher.CheckAccess())
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                LogText.Text += $"{Environment.NewLine}[{timestamp}] {line}";
                LogScroller.ScrollToEnd();
            }
            else
            {
                Dispatcher.Invoke(() => AppendLog(line));
            }
        }

        private void ShowError(string message)
        {
            ErrorLabel.Text = message;
            ErrorLabel.Visibility = System.Windows.Visibility.Visible;
        }
    }
}
