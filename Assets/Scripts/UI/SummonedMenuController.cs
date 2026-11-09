using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort;

namespace DataViz
{
    /// <summary>
    /// Summons the big full-screen menu (SummonedMenuCanvas) in front of the
    /// player on a button press, and dismisses it the same way. Uses the
    /// project's existing TunnelingVignetteController (already in the scene,
    /// driving locomotion comfort) for the "void" fade, via its confirmed
    /// public API: BeginTunnelingVignette/EndTunnelingVignette, both taking
    /// an ITunnelingVignetteProvider - this class implements that interface
    /// directly rather than needing a separate provider class.
    ///
    /// Unlike HandMenu/WristShortcutMenu, this menu is NOT wrist-docked and
    /// NOT gaze-gated - it appears at a fixed distance in front of wherever
    /// the player is looking at the moment of the button press, and stays
    /// there (doesn't follow the head afterward) until dismissed.
    /// </summary>
    public class SummonedMenuController : MonoBehaviour, ITunnelingVignetteProvider
    {
        [Header("Target")]
        [Tooltip("The full-screen menu canvas to summon/dismiss. Expected to start inactive.")]
        public GameObject m_MenuCanvas;

        [Header("Input")]
        [Tooltip("Same action toggles summon/dismiss, based on current state - mirrors the " +
                 "pattern used for the wrist panel's manual toggle before HandMenu replaced it.")]
        public InputActionReference m_SummonAction;

        [Tooltip("The summon button is also the grab button, so pressing it while that hand's ray or " +
                 "hand is on (or already holding) one of these is a grab or resize, not a summon: the " +
                 "menu is left as it is. Leave empty to use the coordinate system (plot) cube.")]
        public XRBaseInteractable[] m_GrabPriorityInteractables;

        [Header("Placement")]
        [Tooltip("Distance in front of the player's head the menu appears at, in meters.")]
        public float m_SpawnDistance = 1.8f;

        [Tooltip("If true, the menu spawns at the camera's current height (eye level). If false, " +
                 "it's placed at m_FixedWorldHeight instead - useful if you want it to always " +
                 "appear at a consistent height regardless of whether the player is standing, " +
                 "sitting, or has since changed height.")]
        public bool m_UseCameraHeight = true;

        [Tooltip("Only used when m_UseCameraHeight is false - world-space Y position for the menu.")]
        public float m_FixedWorldHeight = 1.4f;

        [Header("Void Transition")]
        [Tooltip("The scene's existing TunnelingVignetteController (already used for locomotion " +
                 "comfort) - reused here rather than creating a second one.")]
        public TunnelingVignetteController m_VignetteController;

        [Tooltip("Parameters for the summon/dismiss fade specifically - deliberately separate from " +
                 "whatever the locomotion providers use, since this needs a FULL black-out, not a " +
                 "partial comfort vignette. Set Aperture Size to 0 here (locomotion's default of " +
                 "0.7 is a partial edge vignette, not what 'entering a void' needs) - ease in/out " +
                 "time here also directly control how long this script waits before repositioning " +
                 "the menu and before considering the transition complete.")]
        public VignetteParameters m_VoidVignetteParameters = new VignetteParameters();

        private Camera m_PlayerCamera;
        private bool m_MenuActive;
        private bool m_IsTransitioning;

        /// <inheritdoc />
        public VignetteParameters vignetteParameters => m_VoidVignetteParameters;

        private void Awake()
        {
            m_PlayerCamera = Camera.main;

            if (m_MenuCanvas != null)
            {
                m_MenuCanvas.SetActive(false);
            }
        }

        private void OnEnable()
        {
            if (m_SummonAction != null && m_SummonAction.action != null)
            {
                m_SummonAction.action.performed += OnSummonActionPerformed;
                m_SummonAction.action.Enable();
            }
        }

        private void OnDisable()
        {
            if (m_SummonAction != null && m_SummonAction.action != null)
            {
                m_SummonAction.action.performed -= OnSummonActionPerformed;
            }
        }

        private void OnSummonActionPerformed(InputAction.CallbackContext ctx)
        {
            if (m_IsTransitioning)
                return; // ignore repeated presses mid-transition rather than queuing/stacking them

            // Pilot (WAM+IA, Nov 2026): the menu kept appearing with the controllers idle, seemingly
            // on head movement. When controllers rest, the headset can switch to hand tracking, and
            // the hand-interaction profiles also report a "grip pressed" (e.g. a relaxed fist coming
            // into view). Only real controllers may summon the menu.
            if (IsHandTrackingDevice(ctx.control?.device))
            {
                Debug.Log($"[SummonedMenuController] Ignored summon from hand-tracking device '{ctx.control.device.layout}' ({ctx.control.path}).");
                return;
            }

            // Grabbing/resizing the plot takes precedence over the menu.
            if (IsPressAimedAtGrabPriorityInteractable(ctx))
                return;

            if (m_MenuActive)
            {
                StartCoroutine(DismissMenu());
            }
            else
            {
                StartCoroutine(SummonMenu());
            }
        }

        /// <summary>
        /// True when an interactor of the hand that pressed the summon button is hovering
        /// (ray or near) or selecting one of the grab-priority interactables. Hover is checked
        /// as well as select because the grip's select may only register after this callback.
        /// </summary>
        private bool IsPressAimedAtGrabPriorityInteractable(InputAction.CallbackContext ctx)
        {
            if (m_GrabPriorityInteractables == null || m_GrabPriorityInteractables.Length == 0)
            {
                var plot = FindAnyObjectByType<CoordinateSystemInteractable>(FindObjectsInactive.Include);
                var plotGrab = plot != null ? plot.GetComponent<XRBaseInteractable>() : null;
                m_GrabPriorityInteractables = plotGrab != null ? new[] { plotGrab } : new XRBaseInteractable[0];
            }

            InteractorHandedness pressedHand = HandednessOf(ctx.control);

            foreach (XRBaseInteractable interactable in m_GrabPriorityInteractables)
            {
                if (interactable == null || !interactable.isActiveAndEnabled)
                    continue;

                foreach (var interactor in interactable.interactorsHovering)
                    if (IsSameHand(interactor, pressedHand))
                        return true;

                foreach (var interactor in interactable.interactorsSelecting)
                    if (IsSameHand(interactor, pressedHand))
                        return true;
            }

            return false;
        }

        /// <summary>
        /// True for hand-tracking devices (OpenXR hand interaction, Meta aim hand, XR Hands
        /// devices, pinch/poke pose profiles), judged by the device layout and its base layouts.
        /// </summary>
        private static bool IsHandTrackingDevice(InputDevice device)
        {
            if (device == null)
                return false;

            string id = (device.layout + " " + device.name).ToLowerInvariant();
            return id.Contains("handinteraction") || id.Contains("aimhand") || id.Contains("xrhanddevice") ||
                   id.Contains("handtracking") || id.Contains("pinchpose") || id.Contains("pokepose");
        }

        private static InteractorHandedness HandednessOf(InputControl control)
        {
            if (control?.device == null)
                return InteractorHandedness.None;
            if (control.device.usages.Contains(UnityEngine.InputSystem.CommonUsages.LeftHand))
                return InteractorHandedness.Left;
            if (control.device.usages.Contains(UnityEngine.InputSystem.CommonUsages.RightHand))
                return InteractorHandedness.Right;
            return InteractorHandedness.None;
        }

        // Unknown handedness on either side counts as a match, so the plot still wins.
        private static bool IsSameHand(IXRInteractor interactor, InteractorHandedness pressedHand) =>
            pressedHand == InteractorHandedness.None ||
            interactor.handedness == InteractorHandedness.None ||
            interactor.handedness == pressedHand;

        private IEnumerator SummonMenu()
        {
            m_IsTransitioning = true;

            if (m_VignetteController != null)
            {
                m_VignetteController.BeginTunnelingVignette(this);
            }

            // Wait for the ease-in to actually finish closing to full black before
            // moving/revealing the menu behind it - using the vignette's own
            // configured time keeps this in sync automatically if you retune it,
            // rather than a separate hardcoded duration that could drift out of sync.
            yield return new WaitForSeconds(m_VoidVignetteParameters.easeInTime);

            PositionMenuInFrontOfPlayer();

            if (m_MenuCanvas != null)
            {
                m_MenuCanvas.SetActive(true);
            }

            if (m_VignetteController != null)
            {
                m_VignetteController.EndTunnelingVignette(this);
            }

            yield return new WaitForSeconds(m_VoidVignetteParameters.easeOutTime);

            m_MenuActive = true;
            m_IsTransitioning = false;
        }

        private IEnumerator DismissMenu()
        {
            m_IsTransitioning = true;

            if (m_VignetteController != null)
            {
                m_VignetteController.BeginTunnelingVignette(this);
            }

            yield return new WaitForSeconds(m_VoidVignetteParameters.easeInTime);

            if (m_MenuCanvas != null)
            {
                m_MenuCanvas.SetActive(false);
            }

            if (m_VignetteController != null)
            {
                m_VignetteController.EndTunnelingVignette(this);
            }

            yield return new WaitForSeconds(m_VoidVignetteParameters.easeOutTime);

            m_MenuActive = false;
            m_IsTransitioning = false;
        }

        /// <summary>
        /// Computes a spawn position at m_SpawnDistance in front of the camera's
        /// current facing direction, and orients the menu to face back toward
        /// the player. If the menu renders backwards (readable from behind
        /// instead of in front), flip the LookRotation argument order below -
        /// this depends on which local axis your canvas's front face renders
        /// along, same caveat as every other orientation tuning in this project.
        /// </summary>
        private void PositionMenuInFrontOfPlayer()
        {
            if (m_MenuCanvas == null || m_PlayerCamera == null)
                return;

            Vector3 flatForward = m_PlayerCamera.transform.forward;
            flatForward.y = 0f; // ignore head pitch - don't spawn the menu tilted into the floor/ceiling if the player was looking up/down
            flatForward.Normalize();

            Vector3 spawnPosition = m_PlayerCamera.transform.position + flatForward * m_SpawnDistance;

            if (m_UseCameraHeight)
            {
                spawnPosition.y = m_PlayerCamera.transform.position.y;
            }
            else
            {
                spawnPosition.y = m_FixedWorldHeight;
            }

            m_MenuCanvas.transform.position = spawnPosition;
            m_MenuCanvas.transform.rotation = Quaternion.LookRotation(m_MenuCanvas.transform.position - m_PlayerCamera.transform.position);
        }
    }
}