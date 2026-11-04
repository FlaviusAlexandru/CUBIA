using TMPro;
using UnityEngine;

namespace DataViz.Wizard
{
    /// <summary>"3 important, 12 left out, 5 neither", the first X/Y/Z, Back and Start visualising.</summary>
    public sealed class SummaryStep : WizardStepView
    {
        public SummaryStep(WizardController wizard) : base(wizard) { }

        public override void Show()
        {
            WizardSelection selection = Wizard.Selection;
            Panel.Show(Wizard.StepLabel(WizardStep.Summary), "All set", selection.Summary.Name);

            Transform body = BodyColumn(20f);
            WizardUI.Size(WizardUI.Rect("Gap", body), -1f, 16f);

            var (important, leftOut, neither) = selection.Counts();
            WizardUI.Text(body, $"{important} important, {leftOut} left out, {neither} neither",
                          WizardUI.TitleSize * 0.8f, WizardUI.Theme.Text, FontStyles.Bold);

            if (!Wizard.InPlace)
            {
                int[] axes = selection.FirstAxes();
                if (axes != null)
                    WizardUI.Text(body, $"Your plot opens with  X: {Name(axes[0])}   Y: {Name(axes[1])}   Z: {Name(axes[2])}",
                                  WizardUI.BodySize, WizardUI.Theme.TextMuted);
            }

            string note = Wizard.InPlace
                ? "Your bookmarks and Shuffle update as soon as you press Done. These settings are saved on this computer only."
                : "Marked columns appear in your bookmark palette in the order you tapped them. You can change all of this later in Preferences.";
            WizardUI.Text(body, note, WizardUI.BodySize, WizardUI.Theme.TextMuted);

            BuildFooter(WizardStep.Summary, Wizard.InPlace ? "Done" : "Start visualising", Wizard.Finish);
        }

        string Name(int column) => Wizard.Selection.Summary.Columns[column].Name;
    }
}
