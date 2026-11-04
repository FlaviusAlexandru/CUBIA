using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.Networking
{
    /// <summary>
    /// Greys out a menu's plot-editing controls while SharedPlotAuthority.LocalPlayerCanEdit is false
    /// (a non-host client during presenter mode) and shows m_ViewOnlyNote ("Presenter mode — view
    /// only"); everything is restored as soon as editing is allowed again.
    ///
    /// Sits on a menu canvas root (SummonedMenuCanvas, WristUIHolderCanvas). It never touches a
    /// control's own Selectable.interactable (ScatterplotUI, GlyphRangeEditor and MirroredUIControl
    /// already drive that); instead, while locked, it adds its own CanvasGroup (interactable off,
    /// faded) to each control "unit" - the ancestor m_UnitDepth levels below this root, i.e. a row
    /// under an island - and removes it again on unlock. MappingDropSlots (bookmark drops) in gated
    /// units are disabled while locked, so dropped blocks are refused.
    ///
    /// Anything under m_Excluded or under a <see cref="PresenterModeExempt"/> stays usable: menu
    /// navigation (tabs), the session strip and local-only preferences (bookmarks, blacklist,
    /// Shuffle locks, tooltips, import wizard). Offline nothing is ever changed.
    ///
    /// Controls in inactive panels (other tabs) are locked too; while locked the gate rescans every
    /// m_RescanInterval seconds, so controls created later (rows added at runtime) are locked as well.
    /// </summary>
    public class PresenterModeUIGate : MonoBehaviour
    {
        [Tooltip("Subtrees that stay usable in presenter mode (local-only prefs), besides any PresenterModeExempt.")]
        public List<GameObject> m_Excluded = new();

        [Tooltip("Shown only while editing is locked, e.g. \"Presenter mode — view only\".")]
        public GameObject m_ViewOnlyNote;

        [Tooltip("Depth below this root of the object that gets the CanvasGroup (2 = canvas > island > row).")]
        public int m_UnitDepth = 2;

        [Range(0f, 1f)]
        public float m_LockedAlpha = 0.45f;

        [Tooltip("While locked, seconds between scans for controls created since the lock.")]
        public float m_RescanInterval = 0.5f;

        private readonly Dictionary<GameObject, CanvasGroup> m_Groups = new();
        private readonly List<Behaviour> m_DisabledBehaviours = new();
        private readonly HashSet<Transform> m_ExcludedSet = new();
        private bool m_Locked;
        private bool m_Applied;
        private float m_NextRescan;

        /// <summary>True while this gate is currently locking its controls.</summary>
        public bool IsLocked => m_Locked;

        private void OnEnable()
        {
            SharedPlotAuthority.OnPresenterModeChanged += Refresh;
            Refresh();
        }

        private void OnDisable()
        {
            SharedPlotAuthority.OnPresenterModeChanged -= Refresh;
        }

        private void OnDestroy()
        {
            if (m_Locked)
                Unlock();
        }

        // Safety net: LocalPlayerCanEdit can also flip without OnPresenterModeChanged (joining or
        // leaving a session while presenter mode is on), so compare once a frame. One bool read.
        private void Update()
        {
            if (SharedPlotAuthority.LocalPlayerCanEdit == m_Locked)
                Refresh();
            else if (m_Locked && Time.unscaledTime >= m_NextRescan)
                Lock(); // idempotent for units already locked
        }

        public void Refresh()
        {
            bool locked = !SharedPlotAuthority.LocalPlayerCanEdit;
            if (m_Applied && locked == m_Locked)
                return;

            m_Applied = true;
            m_Locked = locked;

            if (locked) Lock();
            else Unlock();

            if (m_ViewOnlyNote != null && m_ViewOnlyNote.activeSelf != locked)
                m_ViewOnlyNote.SetActive(locked);
        }

        private void Lock()
        {
            m_NextRescan = Time.unscaledTime + m_RescanInterval;
            BuildExcludedSet();

            foreach (Selectable selectable in GetComponentsInChildren<Selectable>(true))
            {
                if (IsExcluded(selectable.transform))
                    continue;

                GameObject unit = UnitFor(selectable.transform);
                if (unit == null)
                    continue;

                LockUnit(unit);

                if (selectable is TMP_Dropdown tmpDropdown && tmpDropdown.IsExpanded)
                    tmpDropdown.Hide();
                else if (selectable is Dropdown dropdown)
                    dropdown.Hide();
            }

            foreach (MappingDropSlot slot in GetComponentsInChildren<MappingDropSlot>(true))
            {
                if (IsExcluded(slot.transform))
                    continue;

                GameObject unit = UnitFor(slot.transform);
                if (unit != null)
                    LockUnit(unit);

                if (slot.enabled)
                {
                    slot.enabled = false;
                    m_DisabledBehaviours.Add(slot);
                }
            }
        }

        private void LockUnit(GameObject unit)
        {
            if (!m_Groups.TryGetValue(unit, out CanvasGroup group) || group == null)
            {
                group = unit.AddComponent<CanvasGroup>();
                m_Groups[unit] = group;
            }

            group.interactable = false;
            group.blocksRaycasts = true; // keep blocking rays so nothing behind the menu is hit
            group.alpha = m_LockedAlpha;
        }

        private void Unlock()
        {
            foreach (KeyValuePair<GameObject, CanvasGroup> pair in m_Groups)
            {
                if (pair.Value == null)
                    continue;
                // Restore first so Selectables re-evaluate now, then drop the component entirely.
                pair.Value.interactable = true;
                pair.Value.alpha = 1f;
                Destroy(pair.Value);
            }
            m_Groups.Clear();

            foreach (Behaviour behaviour in m_DisabledBehaviours)
            {
                if (behaviour != null)
                    behaviour.enabled = true;
            }
            m_DisabledBehaviours.Clear();
        }

        private void BuildExcludedSet()
        {
            m_ExcludedSet.Clear();
            foreach (GameObject go in m_Excluded)
                if (go != null)
                    m_ExcludedSet.Add(go.transform);
            foreach (PresenterModeExempt exempt in GetComponentsInChildren<PresenterModeExempt>(true))
                m_ExcludedSet.Add(exempt.transform);
            if (m_ViewOnlyNote != null)
                m_ExcludedSet.Add(m_ViewOnlyNote.transform);
        }

        private bool IsExcluded(Transform t)
        {
            for (Transform p = t; p != null && p != transform; p = p.parent)
            {
                if (m_ExcludedSet.Contains(p))
                    return true;
                // Runtime dropdown lists/blockers are spawned under the canvas root.
                if (p.parent == transform && (p.name == "Dropdown List" || p.name == "Blocker"))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The ancestor m_UnitDepth levels below this root (or the shallowest ancestor if the
        /// control sits higher). Falls back to the control itself if that unit would also cover an
        /// excluded object, so exclusions deeper than the unit level still work.
        /// </summary>
        private GameObject UnitFor(Transform t)
        {
            var chain = new List<Transform>();
            for (Transform p = t; p != null && p != transform; p = p.parent)
                chain.Add(p);

            if (chain.Count == 0)
                return null; // the root itself

            int depth = Mathf.Clamp(m_UnitDepth, 1, chain.Count);
            Transform unit = chain[chain.Count - depth];

            if (ContainsExcluded(unit))
                unit = t;

            return unit.gameObject;
        }

        private bool ContainsExcluded(Transform unit)
        {
            foreach (Transform ex in m_ExcludedSet)
            {
                if (ex != unit && ex.IsChildOf(unit))
                    return true;
            }
            return false;
        }
    }
}
