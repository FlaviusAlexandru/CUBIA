using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.Wizard
{
    /// <summary>
    /// "Mark the important columns": one row per column; tapping bookmarks it, and tap order is
    /// palette order (shown as a number on the row). No limit, but past
    /// WizardSelection.ShortListLimit a hint suggests keeping the list short.
    /// </summary>
    public sealed class MarkColumnsStep : WizardStepView
    {
        readonly List<(string column, Image badge, TextMeshProUGUI number, Image outline)> m_Rows = new();
        TextMeshProUGUI m_Hint;

        public MarkColumnsStep(WizardController wizard) : base(wizard) { }

        public override void Show()
        {
            DatasetSummary summary = Wizard.Selection.Summary;
            Panel.Show(Wizard.StepLabel(WizardStep.MarkColumns), "Mark the important columns",
                       $"We found {summary.Columns.Count} columns in {summary.Name}. Mark the ones that matter most.");
            m_Rows.Clear();

            Transform body = BodyColumn(12f);
            m_Hint = WizardUI.Text(body, "", WizardUI.SmallSize, WizardUI.Theme.TextMuted);
            RectTransform list = BodyList(body);
            foreach (ColumnSummary column in summary.Columns)
                AddRow(list, column, summary.RowCount);

            BuildFooter(WizardStep.MarkColumns, "Next", () => Wizard.GoTo(WizardStep.LeaveOut), Skip);
            Refresh();
        }

        void AddRow(Transform list, ColumnSummary column, int rowCount)
        {
            Image card = ColumnRowView.Card(list, column.Name);
            Image outline = WizardUI.OutlineOverlay(card.transform, WizardUI.Theme.Accent, 18f);

            // Order badge: the palette position once marked, an empty ring before.
            Image badge = WizardUI.RoundedImage(card.transform, "Order", WizardUI.Theme.Accent, 28f);
            badge.raycastTarget = false;
            WizardUI.Size(badge, 56f, 56f);
            TextMeshProUGUI number = WizardUI.Text(badge.transform, "", WizardUI.BodySize, Color.white,
                                                   FontStyles.Bold, TextAlignmentOptions.Center);
            WizardUI.Stretch(number.rectTransform);

            ColumnRowView.AddInfo(card.transform, column, rowCount);

            var button = card.gameObject.AddComponent<Button>();
            button.targetGraphic = card;
            button.colors = WizardUI.PressColors();
            string name = column.Name;
            button.onClick.AddListener(() => { Wizard.Selection.ToggleBookmark(name); Refresh(); });

            m_Rows.Add((name, badge, number, outline));
        }

        void Refresh()
        {
            WizardSelection selection = Wizard.Selection;
            foreach (var row in m_Rows)
            {
                int position = selection.BookmarkPosition(row.column);
                bool marked = position > 0;
                row.number.text = marked ? position.ToString() : "";
                row.badge.sprite = marked ? WizardUI.Rounded : WizardUI.Outline;
                row.badge.color = marked ? WizardUI.Theme.Accent : WizardUI.Theme.TextMuted;
                row.outline.enabled = marked;
            }

            int count = selection.Counts().important;
            bool tooMany = count > WizardSelection.ShortListLimit;
            m_Hint.text = tooMany
                ? $"{count} marked. A short list (up to {WizardSelection.ShortListLimit}) keeps the bookmark palette easy to scan."
                : "Tap in order of importance: your bookmark palette shows them in the order you tap.";
            m_Hint.color = tooMany ? WizardUI.Theme.Accent : WizardUI.Theme.TextMuted;
        }

        void Skip()
        {
            Wizard.Selection.RevertBookmarks();
            Wizard.Selection.RevertBlacklist();
            Wizard.GoTo(WizardStep.Summary);
        }
    }
}
