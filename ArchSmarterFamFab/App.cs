namespace ArchSmarterFamFab
{
    internal class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication app)
        {
            string tabName = "ArchSmarter";
            try
            {
                app.CreateRibbonTab(tabName);
            }
            catch (Exception)
            {
                Debug.Print("Tab already exists.");
            }

            RibbonPanel panel = Helpers.Utils.CreateRibbonPanel(app, tabName, "Family Fabricator");

            PushButtonData btnFamFab = CmdFamFab.GetButtonData();
            PushButtonData btnSettings = CmdSetting.GetButtonData();

            panel.AddItem(btnFamFab);
            panel.AddItem(btnSettings);

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication a)
        {
            return Result.Succeeded;
        }
    }
}
