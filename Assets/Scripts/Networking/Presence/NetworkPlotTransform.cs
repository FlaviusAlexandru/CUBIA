using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace DataViz.Networking
{
    /// <summary>Plot pose in the table's local frame (see <see cref="TableFrame"/>); Scale is uniform, relative to the table.</summary>
    public struct PlotTablePose : INetworkSerializeByMemcpy
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float Scale;
    }

    /// <summary>
    /// Shares the plot's grab pose. Lives on its own in-scene NetworkObject ("SharedPlotSync" at the
    /// scene root) and drives the plot by reference, so the plot keeps un-parenting/re-docking
    /// locally exactly as offline and never needs a NetworkObject of its own.
    ///
    /// Authority: the host decides who holds the plot (<see cref="HolderClientId"/>). A local grab
    /// asks the host; it is refused while someone else holds it or, in presenter mode, for non-hosts,
    /// and a refused grab is cancelled through the XR interaction manager. The holder streams its
    /// table-relative pose (~20 Hz); everyone else smooths towards it. On release the hold returns
    /// to the host. Reset position is broadcast to everyone. Unspawned (offline) it does nothing.
    /// </summary>
    public class NetworkPlotTransform : NetworkBehaviour
    {
        public const ulong NoHolder = ulong.MaxValue;

        public static NetworkPlotTransform Instance { get; private set; }

        [SerializeField] CoordinateSystemInteractable m_Plot;
        [Tooltip("Seconds between pose updates while holding (0.05 = 20 Hz).")]
        [SerializeField] float m_SendInterval = 0.05f;
        [Tooltip("Exponential smoothing rate for remote poses (1/s). Higher follows faster.")]
        [SerializeField] float m_Smoothing = 18f;

        readonly NetworkVariable<ulong> m_Holder = new(NoHolder);
        readonly NetworkVariable<PlotTablePose> m_Pose = new();

        /// <summary>NGO client id of the player holding the plot, or <see cref="NoHolder"/>.</summary>
        public ulong HolderClientId => IsSpawned ? m_Holder.Value : NoHolder;

        /// <summary>Raised on every peer when the holder changes.</summary>
        public event Action OnHolderChanged;

        XRGrabInteractable m_Grab;
        bool m_LocalGrabActive;      // the local hand drives the plot (granted, or waiting for the host's answer)
        bool m_CancelPending;        // refused grab, cancelled next frame (not from inside XRI's select callback)
        bool m_ApplyingRemoteReset;  // suppresses re-broadcasting a reset we are applying
        float m_NextSendTime;
        PlotTablePose m_LastSent;
        PlotTablePose m_Smoothed;

        void Awake()
        {
            Instance = this;
            if (m_Plot != null) m_Grab = m_Plot.GetComponent<XRGrabInteractable>();
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        public override void OnNetworkSpawn()
        {
            if (m_Plot == null || m_Grab == null || TableFrame.Instance == null)
            {
                Debug.LogError("[NetworkPlotTransform] Missing plot or TableFrame; run Tools/CUBIA/Multiplayer/2 Setup Presence.", this);
                return;
            }

            m_Grab.selectEntered.AddListener(OnSelectEntered);
            m_Grab.selectExited.AddListener(OnSelectExited);
            m_Plot.OnResetPerformed += OnLocalReset;
            m_Holder.OnValueChanged += OnHolderValueChanged;

            if (IsServer)
            {
                m_Holder.Value = NoHolder;
                m_Pose.Value = ReadLocalPose();
                NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
                SharedPlotAuthority.OnPresenterModeChanged += OnPresenterModeChanged;
            }

            m_Smoothed = m_Pose.Value;
        }

        public override void OnNetworkDespawn()
        {
            if (m_Grab != null)
            {
                m_Grab.selectEntered.RemoveListener(OnSelectEntered);
                m_Grab.selectExited.RemoveListener(OnSelectExited);
            }
            if (m_Plot != null) m_Plot.OnResetPerformed -= OnLocalReset;
            m_Holder.OnValueChanged -= OnHolderValueChanged;
            if (NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            SharedPlotAuthority.OnPresenterModeChanged -= OnPresenterModeChanged;

            m_LocalGrabActive = false;
            m_CancelPending = false;
        }

        void LateUpdate()
        {
            if (!IsSpawned || TableFrame.Instance == null) return;

            if (m_CancelPending)
            {
                m_CancelPending = false;
                if (m_Grab.isSelected)
                    m_Grab.interactionManager?.CancelInteractableSelection((IXRSelectInteractable)m_Grab);
            }

            if (m_LocalGrabActive)
            {
                // Only stream once the host has granted the hold; until then the hand moves it locally.
                if (m_Holder.Value == NetworkManager.LocalClientId && Time.unscaledTime >= m_NextSendTime)
                {
                    m_NextSendTime = Time.unscaledTime + m_SendInterval;
                    PlotTablePose pose = ReadLocalPose();
                    if (!Approximately(pose, m_LastSent))
                    {
                        m_LastSent = pose;
                        SubmitPoseRpc(pose);
                    }
                }
                m_Smoothed = ReadLocalPose();
                return;
            }

            // Re-applied every frame, so moving the local table carries the plot with it.
            float t = 1f - Mathf.Exp(-m_Smoothing * Time.deltaTime);
            PlotTablePose target = m_Pose.Value;
            m_Smoothed.Position = Vector3.Lerp(m_Smoothed.Position, target.Position, t);
            m_Smoothed.Rotation = Quaternion.Slerp(m_Smoothed.Rotation, target.Rotation, t);
            m_Smoothed.Scale = Mathf.Lerp(m_Smoothed.Scale, target.Scale, t);
            ApplyLocalPose(m_Smoothed);
        }

        // ---------- local grab ----------

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            if (m_Grab.interactorsSelecting.Count > 1) return; // second hand of a two-handed grab

            ulong me = NetworkManager.LocalClientId;
            bool heldByOther = m_Holder.Value != NoHolder && m_Holder.Value != me;
            if (heldByOther || !SharedPlotAuthority.LocalPlayerCanEdit)
            {
                m_CancelPending = true;
                return;
            }

            m_LocalGrabActive = true;
            m_NextSendTime = 0f;
            RequestGrabRpc();
        }

        void OnSelectExited(SelectExitEventArgs args)
        {
            if (m_Grab.isSelected || !m_LocalGrabActive) return;
            m_LocalGrabActive = false;
            ReleaseRpc(ReadLocalPose());
        }

        void OnLocalReset()
        {
            if (!IsSpawned || m_ApplyingRemoteReset) return;
            RequestResetRpc();
        }

        // ---------- RPCs (SendTo.Server runs immediately on the host) ----------

        [Rpc(SendTo.Server)]
        void RequestGrabRpc(RpcParams rpcParams = default)
        {
            ulong sender = rpcParams.Receive.SenderClientId;
            bool free = m_Holder.Value == NoHolder || m_Holder.Value == sender;
            bool allowed = !SharedPlotAuthority.PresenterMode || sender == NetworkManager.ServerClientId;
            if (free && allowed) m_Holder.Value = sender;
            else RefuseGrabRpc(RpcTarget.Single(sender, RpcTargetUse.Temp));
        }

        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable)]
        void SubmitPoseRpc(PlotTablePose pose, RpcParams rpcParams = default)
        {
            if (m_Holder.Value == rpcParams.Receive.SenderClientId) m_Pose.Value = pose;
        }

        [Rpc(SendTo.Server)]
        void ReleaseRpc(PlotTablePose finalPose, RpcParams rpcParams = default)
        {
            // A stale release (e.g. after a reset already cleared the hold) is ignored.
            if (m_Holder.Value != rpcParams.Receive.SenderClientId) return;
            m_Pose.Value = finalPose;
            m_Holder.Value = NoHolder;
        }

        [Rpc(SendTo.SpecifiedInParams)]
        void RefuseGrabRpc(RpcParams rpcParams)
        {
            if (!m_LocalGrabActive) return;
            m_LocalGrabActive = false;
            m_CancelPending = true;
        }

        [Rpc(SendTo.Server)]
        void RequestResetRpc(RpcParams rpcParams = default)
        {
            // In presenter mode a client's local reset is simply overwritten by the shared pose.
            if (SharedPlotAuthority.PresenterMode && rpcParams.Receive.SenderClientId != NetworkManager.ServerClientId) return;
            ApplyResetRpc();
        }

        [Rpc(SendTo.ClientsAndHost)]
        void ApplyResetRpc()
        {
            m_LocalGrabActive = false; // ResetPosition cancels our grab; the host clears the hold below
            m_ApplyingRemoteReset = true;
            m_Plot.ResetPosition();
            m_ApplyingRemoteReset = false;

            m_Smoothed = ReadLocalPose();
            if (IsServer)
            {
                m_Holder.Value = NoHolder;
                m_Pose.Value = m_Smoothed;
            }
        }

        // ---------- server bookkeeping ----------

        void OnClientDisconnected(ulong clientId)
        {
            if (m_Holder.Value == clientId) m_Holder.Value = NoHolder;
        }

        void OnPresenterModeChanged()
        {
            ulong holder = m_Holder.Value;
            if (!SharedPlotAuthority.PresenterMode || holder == NoHolder || holder == NetworkManager.ServerClientId) return;
            m_Holder.Value = NoHolder;
            RefuseGrabRpc(RpcTarget.Single(holder, RpcTargetUse.Temp));
        }

        void OnHolderValueChanged(ulong previous, ulong current) => OnHolderChanged?.Invoke();

        // ---------- pose conversion ----------

        PlotTablePose ReadLocalPose()
        {
            TableFrame frame = TableFrame.Instance;
            Transform t = m_Plot.transform;
            return new PlotTablePose
            {
                Position = frame.WorldToTable(t.position),
                Rotation = frame.WorldToTable(t.rotation),
                Scale = t.lossyScale.x / frame.Anchor.lossyScale.x,
            };
        }

        void ApplyLocalPose(PlotTablePose pose)
        {
            TableFrame frame = TableFrame.Instance;
            Transform t = m_Plot.transform;
            t.SetPositionAndRotation(frame.TableToWorld(pose.Position), frame.TableToWorld(pose.Rotation));

            // Scale relative to the current world scale, so it works docked (parented) or undocked.
            float worldScale = pose.Scale * frame.Anchor.lossyScale.x;
            float current = t.lossyScale.x;
            if (current > 1e-6f && Mathf.Abs(worldScale - current) > 1e-6f)
                t.localScale *= worldScale / current;
        }

        static bool Approximately(PlotTablePose a, PlotTablePose b)
        {
            return (a.Position - b.Position).sqrMagnitude < 1e-8f
                && Quaternion.Angle(a.Rotation, b.Rotation) < 0.05f
                && Mathf.Abs(a.Scale - b.Scale) < 1e-4f;
        }
    }
}
