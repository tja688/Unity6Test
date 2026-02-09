using UnityEditor;
using UnityEngine;
using DG.Tweening;

namespace Dott.Editor
{
    [CustomEditor(typeof(DOTweenPathTrack))]
    public class DOTweenPathTrackEditor : UnityEditor.Editor
    {
        private const string AUTO_PLAY_NOTICE_KEY_PREFIX = "Dott.PathTrack.AutoPlayDisabled.";

        private DOTweenPathTrack track;

        private void OnEnable()
        {
            track = (DOTweenPathTrack)target;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(DOTweenPathTrack.id)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(DOTweenPathTrack.delay)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(DOTweenPathTrack.isActive)));

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(DOTweenPathTrack.autoFindOnSameObject)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(DOTweenPathTrack.targetPath)));

            serializedObject.ApplyModifiedProperties();

            DOTweenPath resolvedPath = track.ResolvePath();
            bool reflectionReady = DOTweenPathTweenFactory.IsReflectionReady;
            bool hasWaypointCount = DOTweenPathTweenFactory.TryGetWaypointCount(resolvedPath, out int waypointCount);
            bool isValid = DOTweenPathTweenFactory.IsValid(resolvedPath);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Path Track Status", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Resolved Path", resolvedPath, typeof(DOTweenPath), true);
            }

            EditorGUILayout.LabelField("Factory Reflection", reflectionReady ? "Ready" : "Unavailable");
            EditorGUILayout.LabelField("Waypoints", hasWaypointCount ? waypointCount.ToString() : "Unknown");
            EditorGUILayout.LabelField("Path Valid", isValid ? "Yes" : "No");

            if (resolvedPath == null)
            {
                EditorGUILayout.HelpBox("No DOTweenPath found. Add DOTweenPath on the same object or assign targetPath manually.", MessageType.Warning);
                return;
            }

            if (!reflectionReady)
            {
                EditorGUILayout.HelpBox("DOTweenPath reflection data is unavailable. Check DOTween Pro assembly/import state.", MessageType.Error);
                return;
            }

            if (hasWaypointCount && waypointCount < 2)
            {
                EditorGUILayout.HelpBox("DOTweenPath needs at least 2 waypoints.", MessageType.Warning);
            }

            if (DOTweenPathTweenFactory.TryDisableAutoPlay(resolvedPath))
            {
                EditorUtility.SetDirty(resolvedPath);

                string key = AUTO_PLAY_NOTICE_KEY_PREFIX + resolvedPath.GetInstanceID();
                if (!SessionState.GetBool(key, false))
                {
                    SessionState.SetBool(key, true);
                    EditorGUILayout.HelpBox("Detected DOTweenPath AutoPlay=true and turned it off to avoid double playback with DOTweenPathTrack.", MessageType.Info);
                }
            }
        }
    }
}
