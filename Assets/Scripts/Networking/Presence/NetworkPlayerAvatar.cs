using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>Head, hands and pointing rays of one player, all in the table's local frame.</summary>
    public struct AvatarPose : INetworkSerializeByMemcpy
    {
        public const byte LeftTracked = 1, RightTracked = 2, LeftRay = 4, RightRay = 8, Valid = 16;

        public Vector3 HeadPosition;
        public Quaternion HeadRotation;
        public Vector3 LeftPosition;
        public Quaternion LeftRotation;
        public Vector3 RightPosition;
        public Quaternion RightRotation;
        public Vector3 LeftRayEnd;
        public Vector3 RightRayEnd;
        public byte Flags;

        public bool Has(byte flag) => (Flags & flag) != 0;
    }

    /// <summary>
    /// Player prefab behaviour: simple head + two hands + pointing rays, name and colour.
    /// The owner samples its XR rig, converts to the table frame and writes owner-authoritative
    /// NetworkVariables at ~20 Hz; remote copies smooth towards them and convert back through
    /// their own table, so each player's table placement decides where the others appear.
    /// The owner's own copy is never shown. Player objects survive NGO scene loads, so the owner
    /// re-finds the rig whenever the scene (and its rig) changed.
    ///
    /// While no NetworkScatterplotState is spawned (the lobby), the avatars also feed
    /// SessionService.Players: every connected player has one, carrying name and colour.
    /// </summary>
    public class NetworkPlayerAvatar : NetworkBehaviour
    {
        [SerializeField] GameObject m_Visuals;
        [SerializeField] Transform m_Head;
        [SerializeField] Transform m_LeftHand;
        [SerializeField] Transform m_RightHand;
        [SerializeField] LineRenderer m_LeftRay;
        [SerializeField] LineRenderer m_RightRay;
        [Tooltip("Renderers tinted with the player's colour (via _BaseColor).")]
        [SerializeField] Renderer[] m_TintedRenderers;

        [Tooltip("Seconds between pose updates (0.05 = 20 Hz).")]
        [SerializeField] float m_SendInterval = 0.05f;
        [Tooltip("Exponential smoothing rate for remote poses (1/s).")]
        [SerializeField] float m_Smoothing = 20f;

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly List<NetworkPlayerAvatar> s_Spawned = new();

        readonly NetworkVariable<FixedString64Bytes> m_Name = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<Color> m_Colour = new(Color.white, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<AvatarPose> m_Pose = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        /// <summary>All spawned avatars (local one included).</summary>
        public static IReadOnlyList<NetworkPlayerAvatar> Spawned => s_Spawned;

        public string PlayerName => m_Name.Value.ToString();
        public Color PlayerColour => m_Colour.Value;

        /// <summary>True on remote copies once the first pose has arrived (head visible).</summary>
        public bool HasPose => m_Head.gameObject.activeSelf;

        /// <summary>World position of the (smoothed) head; valid on remote copies when <see cref="HasPose"/>.</summary>
        public Vector3 HeadWorldPosition => m_Head.position;

        /// <summary>Name or colour changed.</summary>
        public event Action OnProfileChanged;

        XRRigPoseSource m_Rig;
        float m_NextSendTime;
        AvatarPose m_Smoothed;
        bool m_HasSmoothed;
        MaterialPropertyBlock m_Block;

        public override void OnNetworkSpawn()
        {
            s_Spawned.Add(this);
            m_Name.OnValueChanged += OnNameChanged;
            m_Colour.OnValueChanged += OnColourChanged;

            if (IsOwner)
            {
                m_Visuals.SetActive(false);
                m_Rig = XRRigPoseSource.Find();
                PushProfile();
                PlayerProfile.OnChanged += PushProfile;
            }

            ApplyColour();
            OnProfileChanged?.Invoke(); // initial values don't raise OnValueChanged
            PublishRoster();
        }

        public override void OnNetworkDespawn()
        {
            s_Spawned.Remove(this);
            m_Name.OnValueChanged -= OnNameChanged;
            m_Colour.OnValueChanged -= OnColourChanged;
            PlayerProfile.OnChanged -= PushProfile;
            PublishRoster();
        }

        void LateUpdate()
        {
            if (!IsSpawned) return;
            TableFrame frame = TableFrame.Instance;
            if (frame == null) return;

            if (IsOwner) SampleAndSend(frame);
            else ApplyRemote(frame);
        }

        void SampleAndSend(TableFrame frame)
        {
            if (Time.unscaledTime < m_NextSendTime) return;
            m_NextSendTime = Time.unscaledTime + m_SendInterval;

            if (m_Rig == null || !m_Rig.IsValid) m_Rig = XRRigPoseSource.Find();
            if (m_Rig == null) return;

            var pose = new AvatarPose
            {
                Flags = AvatarPose.Valid,
                HeadPosition = frame.WorldToTable(m_Rig.Head.position),
                HeadRotation = frame.WorldToTable(m_Rig.Head.rotation),
            };
            SampleHand(frame, true, ref pose.LeftPosition, ref pose.LeftRotation, ref pose.LeftRayEnd, ref pose.Flags, AvatarPose.LeftTracked, AvatarPose.LeftRay);
            SampleHand(frame, false, ref pose.RightPosition, ref pose.RightRotation, ref pose.RightRayEnd, ref pose.Flags, AvatarPose.RightTracked, AvatarPose.RightRay);

            // NetworkVariable only sends a delta when the value actually changed.
            m_Pose.Value = pose;
        }

        void SampleHand(TableFrame frame, bool left, ref Vector3 position, ref Quaternion rotation, ref Vector3 rayEnd, ref byte flags, byte trackedFlag, byte rayFlag)
        {
            if (!m_Rig.TryGetHand(left, out Pose hand, out bool rayVisible, out Vector3 end)) return;
            position = frame.WorldToTable(hand.position);
            rotation = frame.WorldToTable(hand.rotation);
            flags |= trackedFlag;
            if (!rayVisible) return;
            rayEnd = frame.WorldToTable(end);
            flags |= rayFlag;
        }

        void ApplyRemote(TableFrame frame)
        {
            AvatarPose target = m_Pose.Value;
            m_Head.gameObject.SetActive(target.Has(AvatarPose.Valid));
            if (!target.Has(AvatarPose.Valid))
            {
                // Nothing received yet (default pose): keep everything hidden.
                m_LeftHand.gameObject.SetActive(false);
                m_RightHand.gameObject.SetActive(false);
                m_LeftRay.enabled = m_RightRay.enabled = false;
                return;
            }

            if (!m_HasSmoothed)
            {
                m_Smoothed = target;
                m_HasSmoothed = true;
            }
            else
            {
                float t = 1f - Mathf.Exp(-m_Smoothing * Time.deltaTime);
                m_Smoothed.HeadPosition = Vector3.Lerp(m_Smoothed.HeadPosition, target.HeadPosition, t);
                m_Smoothed.HeadRotation = Quaternion.Slerp(m_Smoothed.HeadRotation, target.HeadRotation, t);
                SmoothHand(ref m_Smoothed.LeftPosition, ref m_Smoothed.LeftRotation, ref m_Smoothed.LeftRayEnd,
                    target.LeftPosition, target.LeftRotation, target.LeftRayEnd, m_Smoothed.Has(AvatarPose.LeftTracked), m_Smoothed.Has(AvatarPose.LeftRay), t);
                SmoothHand(ref m_Smoothed.RightPosition, ref m_Smoothed.RightRotation, ref m_Smoothed.RightRayEnd,
                    target.RightPosition, target.RightRotation, target.RightRayEnd, m_Smoothed.Has(AvatarPose.RightTracked), m_Smoothed.Has(AvatarPose.RightRay), t);
                m_Smoothed.Flags = target.Flags;
            }

            m_Head.SetPositionAndRotation(frame.TableToWorld(m_Smoothed.HeadPosition), frame.TableToWorld(m_Smoothed.HeadRotation));
            ApplyHand(frame, m_LeftHand, m_LeftRay, m_Smoothed.LeftPosition, m_Smoothed.LeftRotation, m_Smoothed.LeftRayEnd,
                m_Smoothed.Has(AvatarPose.LeftTracked), m_Smoothed.Has(AvatarPose.LeftRay));
            ApplyHand(frame, m_RightHand, m_RightRay, m_Smoothed.RightPosition, m_Smoothed.RightRotation, m_Smoothed.RightRayEnd,
                m_Smoothed.Has(AvatarPose.RightTracked), m_Smoothed.Has(AvatarPose.RightRay));
        }

        // A hand or ray that just appeared snaps to its target instead of sliding in from its old spot.
        static void SmoothHand(ref Vector3 position, ref Quaternion rotation, ref Vector3 rayEnd,
            Vector3 targetPosition, Quaternion targetRotation, Vector3 targetRayEnd, bool wasTracked, bool hadRay, float t)
        {
            position = wasTracked ? Vector3.Lerp(position, targetPosition, t) : targetPosition;
            rotation = wasTracked ? Quaternion.Slerp(rotation, targetRotation, t) : targetRotation;
            rayEnd = hadRay ? Vector3.Lerp(rayEnd, targetRayEnd, t) : targetRayEnd;
        }

        static void ApplyHand(TableFrame frame, Transform hand, LineRenderer ray, Vector3 position, Quaternion rotation, Vector3 rayEnd, bool tracked, bool rayOn)
        {
            hand.gameObject.SetActive(tracked);
            ray.enabled = tracked && rayOn;
            if (!tracked) return;

            Vector3 world = frame.TableToWorld(position);
            hand.SetPositionAndRotation(world, frame.TableToWorld(rotation));
            if (ray.enabled)
            {
                ray.SetPosition(0, world);
                ray.SetPosition(1, frame.TableToWorld(rayEnd));
            }
        }

        void PushProfile()
        {
            m_Name.Value = new FixedString64Bytes(PlayerProfile.Name);
            m_Colour.Value = PlayerProfile.Colour;
        }

        void OnNameChanged(FixedString64Bytes previous, FixedString64Bytes current)
        {
            OnProfileChanged?.Invoke();
            PublishRoster();
        }

        void OnColourChanged(Color previous, Color current)
        {
            ApplyColour();
            OnProfileChanged?.Invoke();
            PublishRoster();
        }

        /// <summary>Lobby player list: host first, then by client id. NetworkScatterplotState owns the list once spawned.</summary>
        static void PublishRoster()
        {
            SessionService session = SessionService.Instance;
            NetworkScatterplotState state = NetworkScatterplotState.Instance;
            if (session == null || (state != null && state.IsSpawned)) return;

            var players = new List<PlayerInfo>(s_Spawned.Count);
            foreach (NetworkPlayerAvatar avatar in s_Spawned)
            {
                players.Add(new PlayerInfo
                {
                    ClientId = avatar.OwnerClientId,
                    Name = avatar.PlayerName,
                    Colour = avatar.PlayerColour,
                    IsHost = avatar.OwnerClientId == NetworkManager.ServerClientId,
                });
            }
            if (!session.IsInSession && players.Count > 0) return; // stale avatars while shutting down
            players.Sort((a, b) => a.IsHost != b.IsHost ? (a.IsHost ? -1 : 1) : a.ClientId.CompareTo(b.ClientId));
            session.SetPlayers(players);
        }

        void ApplyColour()
        {
            m_Block ??= new MaterialPropertyBlock();
            m_Block.SetColor(k_BaseColor, m_Colour.Value);
            foreach (Renderer r in m_TintedRenderers)
                if (r != null) r.SetPropertyBlock(m_Block);
        }
    }
}
