using DG.Tweening;
using UnityEngine;

namespace GestureTween
{
    /// <summary>
    /// 运行时播放器：将 GestureCurvePreset 还原成 DOTween Sequence。
    /// </summary>
    public class GestureTweenPlayer : MonoBehaviour
    {
        [SerializeField]
        private GestureCurvePreset preset;

        [SerializeField]
        private bool playOnEnable;

        [SerializeField]
        private bool useLocalSpace = true;

        [SerializeField]
        private bool includeScale = true;

        [SerializeField]
        private bool includeRotation = true;

        [SerializeField]
        private bool linkToGameObject = true;

        private Sequence _sequence;

        public GestureCurvePreset Preset
        {
            get => preset;
            set => preset = value;
        }

        public Sequence Play(bool restart = true)
        {
            if (preset == null)
            {
                Debug.LogWarning("[GestureTween] GestureTweenPlayer has no preset.", this);
                return null;
            }

            if (restart)
            {
                Stop();
            }

            _sequence = preset.CreateTransformSequence(transform, useLocalSpace, includeScale, includeRotation);
            if (_sequence == null)
            {
                Debug.LogWarning("[GestureTween] Preset does not contain playable channels.", this);
                return null;
            }

            if (linkToGameObject)
            {
                _sequence.SetLink(gameObject, LinkBehaviour.KillOnDestroy);
            }

            _sequence.Play();
            return _sequence;
        }

        public void Stop()
        {
            if (_sequence == null) return;
            if (_sequence.IsActive())
            {
                _sequence.Kill();
            }

            _sequence = null;
        }

        private void OnEnable()
        {
            if (playOnEnable)
            {
                Play(restart: true);
            }
        }

        private void OnDisable()
        {
            Stop();
        }
    }
}
