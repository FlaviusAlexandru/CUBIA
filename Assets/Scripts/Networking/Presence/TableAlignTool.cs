using UnityEngine;
using UnityEngine.InputSystem;

namespace DataViz.Networking
{
    /// <summary>
    /// Corner input for the co-located Align flow. While <see cref="TableFrame.AlignStep"/> is 1 or 2,
    /// shows a small marker on each controller's tip and, when that controller's trigger is pressed,
    /// hands the tip's world position to <see cref="TableFrame.SubmitAlignCorner"/>.
    /// Idle (no input, no visuals) otherwise.
    /// </summary>
    [RequireComponent(typeof(TableFrame))]
    public class TableAlignTool : MonoBehaviour
    {
        [Tooltip("Extra offset from the controller's poke point, in controller space, if the touch point needs calibrating.")]
        [SerializeField] Vector3 m_TipOffset = Vector3.zero;

        [Tooltip("Diameter of the tip markers shown while aligning (metres).")]
        [SerializeField] float m_MarkerSize = 0.012f;

        [SerializeField] Material m_MarkerMaterial;

        TableFrame m_Frame;
        XRRigPoseSource m_Rig;
        InputAction m_LeftTrigger;
        InputAction m_RightTrigger;
        Transform m_LeftMarker;
        Transform m_RightMarker;

        void Awake()
        {
            m_Frame = GetComponent<TableFrame>();
            m_LeftTrigger = CreateTriggerAction("LeftHand");
            m_RightTrigger = CreateTriggerAction("RightHand");
            m_LeftTrigger.performed += _ => Submit(true);
            m_RightTrigger.performed += _ => Submit(false);
        }

        void OnEnable() => m_Frame.OnAlignStepChanged += OnAlignStepChanged;

        void OnDisable()
        {
            m_Frame.OnAlignStepChanged -= OnAlignStepChanged;
            SetListening(false);
        }

        void OnDestroy()
        {
            m_LeftTrigger.Dispose();
            m_RightTrigger.Dispose();
            if (m_LeftMarker != null) Destroy(m_LeftMarker.gameObject);
            if (m_RightMarker != null) Destroy(m_RightMarker.gameObject);
        }

        void OnAlignStepChanged() => SetListening(m_Frame.AlignStep != 0);

        void LateUpdate()
        {
            if (m_Frame.AlignStep == 0) return;
            UpdateMarker(true, m_LeftMarker);
            UpdateMarker(false, m_RightMarker);
        }

        void SetListening(bool on)
        {
            if (on)
            {
                m_Rig ??= XRRigPoseSource.Find();
                if (m_LeftMarker == null) m_LeftMarker = CreateMarker("AlignTip L");
                if (m_RightMarker == null) m_RightMarker = CreateMarker("AlignTip R");
                m_LeftTrigger.Enable();
                m_RightTrigger.Enable();
            }
            else
            {
                m_LeftTrigger.Disable();
                m_RightTrigger.Disable();
                if (m_LeftMarker != null) m_LeftMarker.gameObject.SetActive(false);
                if (m_RightMarker != null) m_RightMarker.gameObject.SetActive(false);
            }
        }

        void Submit(bool left)
        {
            if (m_Frame.AlignStep != 0 && TryGetTip(left, out Vector3 tip))
                m_Frame.SubmitAlignCorner(tip);
        }

        bool TryGetTip(bool left, out Vector3 tip)
        {
            tip = default;
            if (m_Rig == null || !m_Rig.TryGetControllerTip(left, out Vector3 raw)) return false;
            // A controller is active here, so TryGetHand returns that controller's pose.
            m_Rig.TryGetHand(left, out Pose controller, out _, out _);
            tip = raw + controller.rotation * m_TipOffset;
            return true;
        }

        void UpdateMarker(bool left, Transform marker)
        {
            bool has = TryGetTip(left, out Vector3 tip);
            marker.gameObject.SetActive(has);
            if (has) marker.position = tip;
        }

        Transform CreateMarker(string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            Destroy(go.GetComponent<Collider>()); // must never block interactor casts
            go.transform.localScale = Vector3.one * m_MarkerSize;
            if (m_MarkerMaterial != null) go.GetComponent<Renderer>().sharedMaterial = m_MarkerMaterial;
            go.SetActive(false);
            return go.transform;
        }

        static InputAction CreateTriggerAction(string hand)
        {
            var action = new InputAction("Align corner " + hand, InputActionType.Button);
            action.AddBinding($"<XRController>{{{hand}}}/{{TriggerButton}}");
            return action;
        }
    }
}
