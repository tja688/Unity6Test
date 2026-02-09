using DG.Tweening;
using UnityEngine;

namespace Dott
{
    [AddComponentMenu("DOTween/DOTween Timeline Player")]
    [RequireComponent(typeof(DOTweenTimeline))]
    public class DOTweenTimelinePlayer : MonoBehaviour
    {
        [SerializeField] private bool playOnEnable = true;
        [SerializeField] private int loops = 1;

        private DOTweenTimeline timeline;

        private void Awake() => timeline = GetComponent<DOTweenTimeline>();

        private void OnEnable()
        {
            if (playOnEnable)
            {
                timeline.Play().SetLoops(loops);
            }
        }
    }
}