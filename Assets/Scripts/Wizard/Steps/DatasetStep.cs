using System.Collections.Generic;
using System.Globalization;
using DataViz.Flow;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.Wizard
{
    /// <summary>
    /// One card per .cdataset: name, rows, columns, "Setup saved" when prefs exist, and a
    /// "Default on load" box (at most one dataset: ticking one unticks the others). Tapping a card
    /// selects it; Next reads its columns.
    /// </summary>
    public sealed class DatasetStep : WizardStepView
    {
        readonly List<(string file, Image outline, Toggle defaultToggle)> m_Cards = new();
        string m_Selected;
        Button m_Next;

        public DatasetStep(WizardController wizard) : base(wizard) { }

        public override void Show()
        {
            Panel.Show(Wizard.StepLabel(WizardStep.ChooseDataset), "Choose a dataset",
                       "Tap the data you want to explore. Tick Default on load to skip this wizard next time.");
            m_Cards.Clear();
            m_Selected = Wizard.DatasetFile;

            Transform body = BodyColumn();
            RectTransform list = BodyList(body);
            foreach (string file in DatasetCatalog.Files())
            {
                DatasetSummary header = DatasetCatalog.Header(file);
                if (header != null)
                    AddCard(list, file, header);
            }

            if (m_Cards.Count == 0)
                WizardUI.Text(list, "No datasets found in StreamingAssets/DataCSV/ProcessedData. " +
                                    "Convert a CSV with preprocess_csv_columnar.py, then reopen CUBIA.",
                              WizardUI.BodySize, WizardUI.Theme.TextMuted);

            m_Next = BuildFooter(WizardStep.ChooseDataset, "Next", OnNext);
            Refresh();
        }

        void AddCard(Transform list, string file, DatasetSummary header)
        {
            Image card = WizardUI.RoundedImage(list, file, WizardUI.CardColor, 20f);
            card.raycastTarget = false;
            var layout = card.gameObject.AddComponent<HorizontalLayoutGroup>();
            WizardUI.Configure(layout, 16f, new RectOffset(0, 20, 0, 0), TextAnchor.MiddleLeft);
            WizardUI.Size(card, -1f, 120f);
            Image outline = WizardUI.OutlineOverlay(card.transform, WizardUI.Theme.Accent, 20f);

            // Pick area: everything but the Default on load box.
            Image pick = WizardUI.RoundedImage(card.transform, "Pick", new Color(1f, 1f, 1f, 0f), 20f);
            var pickLayout = pick.gameObject.AddComponent<HorizontalLayoutGroup>();
            WizardUI.Configure(pickLayout, 20f, new RectOffset(24, 12, 12, 12), TextAnchor.MiddleLeft);
            WizardUI.Size(pick, -1f, 120f, flexibleWidth: 1f);
            WizardUI.Icon(pick.transform, WizardUI.Assets.Dataset, WizardUI.Theme.IconTint, 52f);

            VerticalLayoutGroup text = WizardUI.Column(pick.transform, "Text", 4f, null, TextAnchor.MiddleLeft);
            WizardUI.Size(text, flexibleWidth: 1f);
            WizardUI.Text(text.transform, header.Name, WizardUI.BodySize, WizardUI.Theme.Text, FontStyles.Bold)
                    .textWrappingMode = TextWrappingModes.NoWrap;
            string meta = $"{header.RowCount.ToString("N0", CultureInfo.InvariantCulture)} rows, {header.Columns.Count} columns";
            WizardUI.Text(text.transform, meta, WizardUI.SmallSize, WizardUI.Theme.TextMuted);

            if (ColumnPrefsStore.HasSavedSetup(header.Name))
                WizardUI.Chip(pick.transform, "Setup saved", WizardUI.AccentSoft, WizardUI.Theme.Accent);

            var button = pick.gameObject.AddComponent<Button>();
            button.targetGraphic = card;
            button.colors = WizardUI.PressColors();
            button.onClick.AddListener(() => { m_Selected = file; Refresh(); });

            Toggle defaultToggle = WizardUI.CheckToggle(card.transform, "Default on load",
                                                        LaunchPrefs.DefaultDataset == file, on => OnDefaultChanged(file, on));
            m_Cards.Add((file, outline, defaultToggle));
        }

        void OnDefaultChanged(string file, bool on)
        {
            if (on)
                LaunchPrefs.DefaultDataset = file;
            else if (LaunchPrefs.DefaultDataset == file)
                LaunchPrefs.DefaultDataset = "";

            foreach (var card in m_Cards)
                if (card.file != file)
                    card.defaultToggle.SetIsOnWithoutNotify(false);
        }

        void Refresh()
        {
            foreach (var card in m_Cards)
                card.outline.enabled = card.file == m_Selected;
            m_Next.interactable = m_Selected != null;
        }

        void OnNext()
        {
            if (Wizard.SelectDataset(m_Selected))
                Wizard.GoTo(WizardStep.MarkColumns);
            else
                Panel.SetSubtitle($"Could not read {m_Selected}. Pick another dataset.");
        }
    }
}
