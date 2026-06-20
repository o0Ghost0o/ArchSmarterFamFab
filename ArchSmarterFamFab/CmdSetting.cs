using System.Windows.Interop;
using ArchSmarterFamFab.UI;

namespace ArchSmarterFamFab
{
    [Transaction(TransactionMode.Manual)]
    public class CmdSetting : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;

            var window = new SettingsWindow();
            new WindowInteropHelper(window).Owner = uiapp.MainWindowHandle;
            window.ShowDialog();

            return Result.Succeeded;
        }

        internal static PushButtonData GetButtonData()
        {
            string buttonInternalName = "btnFamFabSettings";
            string buttonTitle = "Settings";

            Helpers.ButtonDataClass myButtonData = new Helpers.ButtonDataClass(
                buttonInternalName,
                buttonTitle,
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.settings_sliders_32,
                Properties.Resources.settings_sliders_16,
                "Configure FamFab settings and API key");

            return myButtonData.Data;
        }
    }
}
