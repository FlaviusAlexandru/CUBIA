using System;
using DataViz.Flow;
using DataViz.Networking;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace DataViz.Lobby
{
    /// <summary>
    /// The world-space modal behind <see cref="ConfirmDialog"/>: a light panel with title, message and
    /// two buttons, floating in front of the player. Cancel is the default (accent button, Enter and
    /// Escape); a ray click anywhere outside the panel lands on a large, faint backdrop and cancels too.
    /// Built at runtime with <see cref="PanelKit"/>; one dialog at a time (a new one cancels the old).
    /// </summary>
    public class ConfirmDialogView : MonoBehaviour
    {
        const float k_Distance = 0.75f;          // metres in front of the eyes
        const float k_DropBelowEyes = 0.08f;     // a little below eye level reads more comfortably
        static readonly Vector2 k_BackdropSize = new(2600f, 1800f); // canvas units = mm
        const float k_PanelWidth = 760f;
        const float k_PanelHeight = 290f;

        static ConfirmDialogView s_Current;

        Action m_OnConfirm;
        Action m_OnCancel;

        public static bool IsOpen => s_Current != null;

        public static void Open(string title, string message, string confirmLabel, string cancelLabel,
                                Action onConfirm, Action onCancel)
        {
            if (s_Current != null) s_Current.Cancel();

            var root = new GameObject("ConfirmDialog");
            PlaceInFrontOfCamera(root.transform);
            var view = root.AddComponent<ConfirmDialogView>();
            view.m_OnConfirm = onConfirm;
            view.m_OnCancel = onCancel;
            view.Build(title, message, confirmLabel, cancelLabel);
            s_Current = view;
        }

        void OnDestroy()
        {
            if (s_Current == this) s_Current = null;
        }

        void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (keyboard.escapeKey.wasPressedThisFrame || keyboard.enterKey.wasPressedThisFrame)
                Cancel();
        }

        void Confirm() => Close(m_OnConfirm);

        void Cancel() => Close(m_OnCancel);

        void Close(Action callback)
        {
            if (s_Current == this) s_Current = null;
            m_OnConfirm = m_OnCancel = null;
            Destroy(gameObject);
            callback?.Invoke();
        }

        void Build(string title, string message, string confirmLabel, string cancelLabel)
        {
            MenuTheme theme = PanelKit.Theme;
            Canvas canvas = PanelKit.WorldCanvas("Canvas", transform, k_BackdropSize);
            canvas.sortingOrder = 100; // above menus it may overlap
            Transform c = canvas.transform;

            // Backdrop: invisible-ish, catches every ray click that misses the panel.
            RectTransform backdrop = PanelKit.Fill(c, "Backdrop");
            var backdropImage = backdrop.gameObject.AddComponent<Image>();
            backdropImage.color = new Color(0.05f, 0.08f, 0.12f, 0.08f);
            var backdropButton = backdrop.gameObject.AddComponent<Button>();
            backdropButton.transition = Selectable.Transition.None;
            backdropButton.navigation = new Navigation { mode = Navigation.Mode.None };
            backdropButton.onClick.AddListener(Cancel);

            float x = (k_BackdropSize.x - k_PanelWidth) * 0.5f;
            float y = -(k_BackdropSize.y - k_PanelHeight) * 0.5f;
            Image panel = PanelKit.Panel(c, "Panel", x, y, k_PanelWidth, k_PanelHeight,
                new Color(theme.PanelColor.r, theme.PanelColor.g, theme.PanelColor.b, 0.97f), theme.PanelOutline, 28f);
            Transform p = panel.transform;

            PanelKit.Label(p, "Title", title, 40f, -32f, k_PanelWidth - 80f, 52f, theme.TitleFontSize, theme.Text);
            PanelKit.Label(p, "Message", message, 40f, -96f, k_PanelWidth - 80f, 110f, theme.BodyFontSize, theme.TextMuted,
                TextAlignmentOptions.TopLeft, wrap: true);

            const float buttonW = 220f, buttonH = 64f, gap = 16f;
            float buttonY = -(k_PanelHeight - 32f - buttonH);
            Button cancel = PanelKit.Button(p, "Cancel", cancelLabel, null, k_PanelWidth - 40f - buttonW, buttonY, buttonW, buttonH,
                PanelKit.Style.Primary);
            Button confirm = PanelKit.Button(p, "Confirm", confirmLabel, null, k_PanelWidth - 40f - 2f * buttonW - gap, buttonY,
                buttonW, buttonH, PanelKit.Style.Secondary);
            cancel.onClick.AddListener(Cancel);
            confirm.onClick.AddListener(Confirm);
        }

        static void PlaceInFrontOfCamera(Transform root)
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Transform eyes = cam.transform;
            Vector3 forward = Vector3.ProjectOnPlane(eyes.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(eyes.up, Vector3.up); // looking straight down/up
            forward.Normalize();
            root.SetPositionAndRotation(eyes.position + forward * k_Distance + Vector3.down * k_DropBelowEyes,
                Quaternion.LookRotation(forward, Vector3.up));
        }
    }
}
