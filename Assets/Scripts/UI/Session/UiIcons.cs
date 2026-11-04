using System;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// The single-colour line icons (Assets/UI/Icons/lobby_*.svg, imported as sprites) used by the
    /// lobby panel and the session strip. Assigned by their editor builders; any may be null
    /// (the widget then shows its text only).
    /// </summary>
    [Serializable]
    public class UiIcons
    {
        public Sprite Back, Host, Join, Leave, Mic, MicOff, Align, Presenter, Users, Wizard, Check, Warning;
    }
}
