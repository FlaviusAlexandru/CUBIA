namespace DataViz.Wizard
{
    public enum WizardStep
    {
        Welcome,
        ChooseDataset,
        MarkColumns,
        LeaveOut,
        Summary,
    }

    /// <summary>
    /// Where the Start scene's wizard should open when it is entered again from the data scene
    /// (ImportWizardEntry). Read once by StartSceneBoot; no request = a fresh launch.
    /// </summary>
    public static class WizardStartRequest
    {
        static bool s_Pending;
        static WizardStep s_Step;
        static string s_DatasetFile;

        public static void Set(WizardStep step, string datasetFile)
        {
            s_Pending = true;
            s_Step = step;
            s_DatasetFile = datasetFile;
        }

        public static bool TryConsume(out WizardStep step, out string datasetFile)
        {
            step = s_Step;
            datasetFile = s_DatasetFile;
            bool pending = s_Pending;
            s_Pending = false;
            return pending;
        }
    }
}
