using TMPro;
using UnityEngine;

namespace DataViz.Wizard
{
    /// <summary>"Welcome to CUBIA" with the three things the wizard will ask, and Start.</summary>
    public sealed class WelcomeStep : WizardStepView
    {
        static readonly string[] k_Outline =
        {
            "Choose a dataset",
            "Mark the columns that matter most",
            "Leave out the ones Shuffle should never pick",
        };

        public WelcomeStep(WizardController wizard) : base(wizard) { }

        public override void Show()
        {
            Panel.Show("", "Welcome to CUBIA",
                       "Explore your data as points in 3D. Three quick questions first, then your plot appears.");

            Transform body = BodyColumn(18f);
            WizardUI.Size(WizardUI.Rect("Gap", body), -1f, 24f);
            for (int i = 0; i < k_Outline.Length; i++)
            {
                var row = WizardUI.Row(body, "Item", 24f);
                var badge = WizardUI.RoundedImage(row.transform, "Number", WizardUI.AccentSoft, 30f);
                badge.raycastTarget = false;
                WizardUI.Size(badge, 60f, 60f);
                TextMeshProUGUI number = WizardUI.Text(badge.transform, (i + 1).ToString(), WizardUI.BodySize,
                                                       WizardUI.Theme.Accent, FontStyles.Bold, TextAlignmentOptions.Center);
                WizardUI.Stretch(number.rectTransform);
                WizardUI.Text(row.transform, k_Outline[i], WizardUI.BodySize, WizardUI.Theme.Text);
            }

            WizardUI.Spacer(Panel.Footer);
            WizardUI.Button(Panel.Footer, "Start", WizardUI.ButtonStyle.Primary,
                            () => Wizard.GoTo(WizardStep.ChooseDataset), WizardUI.Assets.Next, iconAfterLabel: true);
        }
    }
}
