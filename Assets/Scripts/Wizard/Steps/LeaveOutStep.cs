using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.Wizard
{
    /// <summary>
    /// "Leave out the rest": the columns not marked important, each with a Leave out box
    /// (blacklist: Shuffle never picks them). Unmarked columns start as saved (neutral unless left
    /// out before); "Leave out everything not marked" ticks them all.
    /// </summary>
    public sealed class LeaveOutStep : WizardStepView
    {
        readonly List<(string column, Toggle toggle)> m_Rows = new();

        public LeaveOutStep(WizardController wizard) : base(wizard) { }

        public override void Show()
        {
            Panel.Show(Wizard.StepLabel(WizardStep.LeaveOut), "Leave out the rest",
                       "Shuffle never picks a column you leave out. You can still choose it by hand.");
            m_Rows.Clear();

            WizardSelection selection = Wizard.Selection;
            Transform body = BodyColumn(16f);

            var actions = WizardUI.Row(body, "Actions", 16f);
            WizardUI.Button(actions.transform, "Leave out everything not marked", WizardUI.ButtonStyle.Secondary,
                            LeaveOutAll, WizardUI.Assets.LeaveOut);

            RectTransform list = BodyList(body);
            foreach (ColumnSummary column in selection.Summary.Columns)
                if (!selection.IsBookmarked(column.Name))
                    AddRow(list, column, selection.Summary.RowCount);

            if (m_Rows.Count == 0)
            {
                actions.gameObject.SetActive(false);
                WizardUI.Text(list, "Every column is marked as important, so there is nothing to leave out.",
                              WizardUI.BodySize, WizardUI.Theme.TextMuted);
            }

            BuildFooter(WizardStep.LeaveOut, "Next", () => Wizard.GoTo(WizardStep.Summary), Skip);
        }

        void AddRow(Transform list, ColumnSummary column, int rowCount)
        {
            Image card = ColumnRowView.Card(list, column.Name);
            ColumnRowView.AddInfo(card.transform, column, rowCount);

            WizardUI.CheckBox(card.transform, out Image check);
            WizardUI.Text(card.transform, "Leave out", WizardUI.SmallSize, WizardUI.Theme.Text)
                    .textWrappingMode = TMPro.TextWrappingModes.NoWrap;

            string name = column.Name;
            Toggle toggle = WizardUI.MakeToggle(card.gameObject, card, check, Wizard.Selection.IsLeftOut(name),
                                                on => Wizard.Selection.SetLeftOut(name, on));
            m_Rows.Add((name, toggle));
        }

        void LeaveOutAll()
        {
            Wizard.Selection.LeaveOutAllUnmarked();
            foreach (var row in m_Rows)
                row.toggle.SetIsOnWithoutNotify(true);
        }

        void Skip()
        {
            Wizard.Selection.RevertBlacklist();
            Wizard.GoTo(WizardStep.Summary);
        }
    }
}
