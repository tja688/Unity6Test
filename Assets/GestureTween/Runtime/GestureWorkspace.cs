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

        public void SetReferences(DOTweenTimeline timelineComponent, GesturePathTrack path)
        {
            timeline = timelineComponent;
            pathTrack = path;
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
        }

        private void Reset()
        {
            OnValidate();
        }

        private void OnValidate()
        {
            timeline = timeline != null ? timeline : GetComponent<DOTweenTimeline>();
            pathTrack = pathTrack != null ? pathTrack : GetComponent<GesturePathTrack>();

            if (rootTarget == null && transform.parent != null)
            {
                rootTarget = transform.parent;
            }

            BindRootToTracks();
        }
    }
}
