namespace ArchSmarterFamFab.Helpers
{
    internal class CommandAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        {
            UIDocument activeDoc = applicationData.ActiveUIDocument;
            return activeDoc != null && activeDoc.Document != null;
        }
    }
}
