using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

namespace DataViz.Networking
{
    /// <summary>
    /// Keeps the table level while it is being placed: runs after the general grab transformer
    /// and strips pitch and roll from the target rotation, leaving yaw only.
    /// Registered explicitly through XRGrabInteractable.startingSingle/MultipleGrabTransformers.
    /// </summary>
    public class TableYawGrabTransformer : XRBaseGrabTransformer
    {
        protected override RegistrationMode registrationMode => RegistrationMode.None;

        public override void Process(XRGrabInteractable grabInteractable, XRInteractionUpdateOrder.UpdatePhase updatePhase, ref Pose targetPose, ref Vector3 localScale)
        {
            Vector3 forward = targetPose.rotation * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude > 1e-6f)
                targetPose.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }
    }
}
