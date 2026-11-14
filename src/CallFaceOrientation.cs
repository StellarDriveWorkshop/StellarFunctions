using UnityEngine;

namespace StellarFunctions;

// Functions-owned replica of the vanilla Players.Utils.LookAtPlayerFlat4Sides
// behaviour, applied only to the compact one-input Call face. Snaps the
// display to the nearest of four cardinal orientations so the function name
// reads upright like the Signal Processor's Toggle screen.
//
// Presentation only: no state, save, network or signal involvement. Without a
// camera (dedicated server, headless, holder preview) the current orientation
// is preserved and no work is done. All math is allocation-free; the rotation
// is only reassigned when it actually changes.
internal sealed class CallFaceOrientation : MonoBehaviour
{
    private Transform _surface;
    private Transform _camera;
    private Quaternion _applied;
    private bool _ready;

    private void Awake()
    {
        _surface = transform.parent;
        _applied = transform.rotation;
        _ready = _surface != null;
    }

    private void Update()
    {
        if (!_ready || transform.root == Support.PartFactory.Holder) return;
        if (_camera == null)
        {
            var main = Camera.main;
            _camera = main != null ? main.transform : null;
            if (_camera == null) return;
        }
        Vector3 up = _surface.up;
        Vector3 local = Quaternion.Inverse(_surface.rotation)
            * Vector3.ProjectOnPlane(-_camera.up, up).normalized;
        float z = Vector3.Dot(local, Vector3.forward);
        float x = Vector3.Dot(local, Vector3.right);
        Vector3 cardinal = CallFaceText.Cardinal(z, x) switch
        {
            0 => Vector3.forward,
            1 => Vector3.right,
            2 => Vector3.back,
            _ => Vector3.left,
        };
        var rotation = Quaternion.LookRotation(-up, _surface.rotation * cardinal);
        // The snapped frame reads upside down on this screen plane; roll the
        // text 180 degrees within the screen plane (view-axis rotation).
        rotation *= Quaternion.AngleAxis(CallFaceText.FaceOrientationRoll, Vector3.forward);
        if (Quaternion.Angle(rotation, _applied) > .01f)
        {
            transform.rotation = rotation;
            _applied = rotation;
        }
    }
}
