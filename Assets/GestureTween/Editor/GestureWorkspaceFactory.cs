using System.IO;
using System.Linq;
using Dott;
using UnityEditor;
using UnityEngine;

namespace GestureTween.Editor
{
    public static class GestureWorkspaceFactory
    {
        private const string WorkspaceSuffix = "__PerfWorkspace";

        public static GestureWorkspace FindWorkspace(Transform root)
        {
            if (root == null)
            {
                return null;
            }

            return root.GetComponentsInChildren<GestureWorkspace>(true)
                .FirstOrDefault(workspace => workspace != null && workspace.transform.parent == root);
        }

        public static GestureWorkspace EnsureWorkspace(Transform root, bool selectWorkspace = false)
        {
            if (root == null)
            {
                return null;
            }

            GestureWorkspace workspace = FindWorkspace(root);
            if (workspace == null)
            {
                string workspaceName = BuildWorkspaceName(root.name);
                var workspaceObject = new GameObject(workspaceName);
                Undo.RegisterCreatedObjectUndo(workspaceObject, "Create Gesture Workspace");

                Transform workspaceTransform = workspaceObject.transform;
                workspaceTransform.SetParent(root, false);
                workspaceTransform.localPosition = Vector3.zero;
                workspaceTransform.localRotation = Quaternion.identity;
                workspaceTransform.localScale = Vector3.one;

                workspace = Undo.AddComponent<GestureWorkspace>(workspaceObject);
            }

            DOTweenTimeline timeline = workspace.GetComponent<DOTweenTimeline>() ?? Undo.AddComponent<DOTweenTimeline>(workspace.gameObject);
            GesturePathTrack pathTrack = workspace.GetComponent<GesturePathTrack>() ?? Undo.AddComponent<GesturePathTrack>(workspace.gameObject);
            GestureScaleTrack scaleTrack = workspace.GetComponent<GestureScaleTrack>() ?? Undo.AddComponent<GestureScaleTrack>(workspace.gameObject);
            GestureRotationTrack rotationTrack = workspace.GetComponent<GestureRotationTrack>() ?? Undo.AddComponent<GestureRotationTrack>(workspace.gameObject);

            Undo.RecordObject(workspace, "Configure Gesture Workspace");
            workspace.RootTarget = root;
            workspace.SetReferences(timeline, pathTrack, scaleTrack, rotationTrack);
            workspace.BindRootToTracks();

            EditorUtility.SetDirty(workspace);
            EditorUtility.SetDirty(timeline);
            EditorUtility.SetDirty(pathTrack);
            EditorUtility.SetDirty(scaleTrack);
            EditorUtility.SetDirty(rotationTrack);
            PrefabUtility.RecordPrefabInstancePropertyModifications(workspace);
            PrefabUtility.RecordPrefabInstancePropertyModifications(timeline);
            PrefabUtility.RecordPrefabInstancePropertyModifications(pathTrack);
            PrefabUtility.RecordPrefabInstancePropertyModifications(scaleTrack);
            PrefabUtility.RecordPrefabInstancePropertyModifications(rotationTrack);

            if (selectWorkspace)
            {
                Selection.activeTransform = workspace.transform;
            }

            return workspace;
        }

        public static string BuildWorkspaceName(string rootName)
        {
            if (string.IsNullOrWhiteSpace(rootName))
            {
                rootName = "Gesture";
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                rootName = rootName.Replace(invalid, '_');
            }

            return rootName + WorkspaceSuffix;
        }
    }
}
