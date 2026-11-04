using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// Marks a subtree that <see cref="PresenterModeUIGate"/> leaves usable in presenter mode:
    /// navigation (menu tabs), the session strip and local-only preferences (bookmarks, blacklist,
    /// shuffle locks, tooltips, import wizard, Multiplayer).
    /// </summary>
    [DisallowMultipleComponent]
    public class PresenterModeExempt : MonoBehaviour
    {
    }
}
