using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.Wizard
{
    /// <summary>
    /// The shared parts of a column row in the column steps: a card background, the name with
    /// three sample values under it, a type badge (number, date, category) and the share of empty
    /// cells. Steps add their own control (order badge, leave-out box) around it.
    /// </summary>
    public static class ColumnRowView
    {
        public const float RowHeight = 104f;

        static readonly Color k_DateColor = new Color(0.52f, 0.32f, 0.82f, 1f);
        static readonly Color k_CategoryColor = new Color(0.85f, 0.48f, 0.12f, 1f);

        /// <summary>Card row; returns the row background (the step's hit area).</summary>
        public static Image Card(Transform list, string name)
        {
            Image card = WizardUI.RoundedImage(list, name, WizardUI.CardColor, 18f);
            var layout = card.gameObject.AddComponent<HorizontalLayoutGroup>();
            WizardUI.Configure(layout, 20f, new RectOffset(20, 24, 10, 10), TextAnchor.MiddleLeft);
            WizardUI.Size(card, -1f, RowHeight);
            return card;
        }

        /// <summary>Name + samples (flexible), type badge and empty share, in that order.</summary>
        public static void AddInfo(Transform row, ColumnSummary column, int rowCount)
        {
            VerticalLayoutGroup text = WizardUI.Column(row, "Name", 2f, null, TextAnchor.MiddleLeft);
            text.childForceExpandWidth = true;
            WizardUI.Size(text, flexibleWidth: 1f);
            TextMeshProUGUI name = WizardUI.Text(text.transform, column.Name, WizardUI.BodySize, WizardUI.Theme.Text, FontStyles.Bold);
            name.textWrappingMode = TextWrappingModes.NoWrap;
            TextMeshProUGUI samples = WizardUI.Text(text.transform, SamplesText(column), WizardUI.SmallSize, WizardUI.Theme.TextMuted);
            samples.textWrappingMode = TextWrappingModes.NoWrap;

            Color typeColor = TypeColor(column.Type);
            WizardUI.Chip(row, TypeLabel(column.Type), WizardUI.WithAlpha(typeColor, 0.14f), typeColor, 150f);

            TextMeshProUGUI empty = WizardUI.Text(row, EmptyText(column.MissingCount, rowCount), WizardUI.SmallSize,
                                                  WizardUI.Theme.TextMuted, FontStyles.Normal, TextAlignmentOptions.MidlineRight);
            empty.textWrappingMode = TextWrappingModes.NoWrap;
            WizardUI.Size(empty, 190f);
        }

        public static string TypeLabel(DataValueType type) => type switch
        {
            DataValueType.DateTime => "date",
            DataValueType.Categorical => "category",
            _ => "number",
        };

        static Color TypeColor(DataValueType type) => type switch
        {
            DataValueType.DateTime => k_DateColor,
            DataValueType.Categorical => k_CategoryColor,
            _ => WizardUI.Theme.Accent,
        };

        static string SamplesText(ColumnSummary column) =>
            column.Samples.Count == 0 ? "no values" : "e.g. " + string.Join("   ", column.Samples);

        static string EmptyText(int missing, int rows)
        {
            if (missing <= 0 || rows <= 0)
                return "no empty cells";
            float percent = 100f * missing / rows;
            return percent < 1f ? "<1% empty" : percent.ToString("0", CultureInfo.InvariantCulture) + "% empty";
        }
    }
}
