using System;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.Wizard
{
    /// <summary>One wizard step: rebuilds the panel's header, body and footer when shown.</summary>
    public abstract class WizardStepView
    {
        protected readonly WizardController Wizard;
        protected WizardPanel Panel => Wizard.Panel;

        protected WizardStepView(WizardController wizard) => Wizard = wizard;

        public abstract void Show();

        /// <summary>Back on the left, then optional Skip and the main button on the right.</summary>
        protected Button BuildFooter(WizardStep step, string nextLabel, Action onNext, Action onSkip = null)
        {
            Transform footer = Panel.Footer;
            bool cancels = Wizard.InPlace && step == WizardStep.MarkColumns;
            WizardUI.Button(footer, cancels ? "Cancel" : "Back", WizardUI.ButtonStyle.Secondary,
                            () => Wizard.Back(step), cancels ? null : WizardUI.Assets.Back);
            WizardUI.Spacer(footer);
            if (onSkip != null)
                WizardUI.Button(footer, "Skip", WizardUI.ButtonStyle.Quiet, onSkip);
            return WizardUI.Button(footer, nextLabel, WizardUI.ButtonStyle.Primary, onNext, WizardUI.Assets.Next, iconAfterLabel: true);
        }

        /// <summary>Scroll list filling the body below any other body content.</summary>
        protected RectTransform BodyList(Transform parent)
        {
            RectTransform content = WizardUI.ScrollList(parent, out ScrollRect scroll);
            WizardUI.Size(scroll, flexibleHeight: 1f);
            return content;
        }

        /// <summary>Vertical stack filling the body.</summary>
        protected Transform BodyColumn(float spacing = 16f)
        {
            VerticalLayoutGroup column = WizardUI.Column(Panel.Body, "Content", spacing);
            column.childForceExpandWidth = true;
            WizardUI.Stretch((RectTransform)column.transform);
            return column.transform;
        }
    }
}
