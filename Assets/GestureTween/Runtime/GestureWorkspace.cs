using Dott;
using UnityEngine;

namespace GestureTween
{
    [DisallowMultipleComponent]
    public class GestureWorkspace : MonoBehaviour
    {
        [SerializeField] private Transform rootTarget;
        [SerializeField] private DOTweenTimeline timeline;
        [SerializeField] private GesturePathTrack pathTrack;
        [SerializeField] private GestureScaleTrack scaleTrack;
        [SerializeField] private GestureRotationTrack rotationTrack;

        public Transform RootTarget
        {
            get => rootTarget;
            set
            {
                rootTarget = value;
                BindRootToTracks();
            }
        }

        public Transform ResolvedRootTarget => rootTarget != null ? rootTarget : transform.parent;
        public DOTweenTimeline Timeline => timeline;
        public GesturePathTrack PathTrack => pathTrack;
        public GestureScaleTrack ScaleTrack => scaleTrack;
        public GestureRotationTrack RotationTrack => rotationTrack;

        public void SetReferences(
            DOTweenTimeline timelineComponent,
            GesturePathTrack path,
            GestureScaleTrack scale,
            GestureRotationTrack rotation)
        {
            timeline = timelineComponent;
            pathTrack = path;
            scaleTrack = scale;
            rotationTrack = rotation;
            BindRootToTracks();
        }

        public void BindRootToTracks()
        {
            Transform resolved = ResolvedRootTarget;
            if (resolved == null)
            {
                return;
            }

            pathTrack?.SetTargetIfAuto(resolved);
            scaleTrack?.SetTargetIfAuto(resolved);
            rotationTrack?.SetTargetIfAuto(resolved);
        }

        private void Reset()
        {
            OnValidate();
        }

        private void OnValidate()
        {
            timeline = timeline != null ? timeline : GetComponent<DOTweenTimeline>();
            pathTrack = pathTrack != null ? pathTrack : GetComponent<GesturePathTrack>();
            scaleTrack = scaleTrack != null ? scaleTrack : GetComponent<GestureScaleTrack>();
            rotationTrack = rotationTrack != null ? rotationTrack : GetComponent<GestureRotationTrack>();

            if (rootTarget == null && transform.parent != null)
            {
                rootTarget = transform.parent;
            }

            BindRootToTracks();
        }
    }
}
