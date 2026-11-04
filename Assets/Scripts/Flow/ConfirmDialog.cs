using System;
using DataViz.Lobby;

namespace DataViz.Flow
{
    /// <summary>
    /// Small reusable world-space modal (title, message, two buttons) in front of the player.
    /// Cancel is the default; a ray click outside the panel cancels. Showing a new dialog cancels
    /// one that is still open. See <see cref="ConfirmDialogView"/>.
    /// </summary>
    public static class ConfirmDialog
    {
        public static bool IsOpen => ConfirmDialogView.IsOpen;

        public static void Show(string title, string message, string confirmLabel, string cancelLabel,
                                Action onConfirm, Action onCancel = null)
        {
            ConfirmDialogView.Open(title, message, confirmLabel, cancelLabel, onConfirm, onCancel);
        }
    }
}
