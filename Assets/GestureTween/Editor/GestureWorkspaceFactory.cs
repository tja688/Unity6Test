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

            // Clean up legacy missing components left by removed scale/rotation tracks.
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(workspace.gameObject);

            DOTweenTimeline timeline = workspace.GetComponent<DOTweenTimeline>() ?? Undo.AddComponent<DOTweenTimeline>(workspace.gameObject);
            GesturePathTrack pathTrack = workspace.GetComponent<GesturePathTrack>() ?? Undo.AddComponent<GesturePathTrack>(workspace.gameObject);

            Undo.RecordObject(workspace, "Configure Gesture Workspace");
            workspace.RootTarget = root;
            workspace.SetReferences(timeline, pathTrack);
            workspace.BindRootToTracks();

            EditorUtility.SetDirty(workspace);
            EditorUtility.SetDirty(timeline);
            EditorUtility.SetDirty(pathTrack);
            PrefabUtility.RecordPrefabInstancePropertyModifications(workspace);
            PrefabUtility.RecordPrefabInstancePropertyModifications(timeline);
            PrefabUtility.RecordPrefabInstancePropertyModifications(pathTrack);

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
