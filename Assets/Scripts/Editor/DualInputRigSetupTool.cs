using MedicalExam.XRInput;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MedicalExam.EditorTools
{
    /// <summary>
    /// Editor entry points for <see cref="DualInputRigSetup"/>.
    ///
    /// The runtime component fixes the rig every time the scene loads, but applying the same rules
    /// in the Editor bakes the result into the scene file, which is easier to review in a diff.
    /// </summary>
    public static class DualInputRigSetupTool
    {
        private const string MenuRoot = "Tools/Lerni/XR/";

        [MenuItem(MenuRoot + "Log Interaction Rig Tree", false, 0)]
        public static void LogRigTree()
        {
            DualInputRigSetup setup = GetOrCreateSetup(createIfMissing: false);
            if (setup == null)
            {
                Debug.LogWarning("No DualInputRigSetup in the scene. Run 'Enable Controllers + Hands' first, " +
                                 "or add the component to the camera rig manually.");
                return;
            }

            SetPrivateBool(setup, "dryRun", true);
            setup.Apply();
            SetPrivateBool(setup, "dryRun", false);
        }

        [MenuItem(MenuRoot + "Enable Controllers + Hands (apply to open scene)", false, 1)]
        public static void ApplyToOpenScene()
        {
            DualInputRigSetup setup = GetOrCreateSetup(createIfMissing: true);
            if (setup == null)
            {
                Debug.LogError("Could not find a Meta Interaction rig in the open scene. Open the scene that contains " +
                               "'[BuildingBlock] OVRInteractionComprehensive' and try again.");
                return;
            }

            Undo.RegisterFullObjectHierarchyUndo(setup.gameObject, "Enable Controllers + Hands");
            setup.Apply();

            EditorUtility.SetDirty(setup);
            EditorSceneManager.MarkSceneDirty(setup.gameObject.scene);
            Debug.Log("Applied. Save the scene to keep the change.");
        }

        private static DualInputRigSetup GetOrCreateSetup(bool createIfMissing)
        {
            DualInputRigSetup existing = Object.FindAnyObjectByType<DualInputRigSetup>(FindObjectsInactive.Include);
            if (existing != null || !createIfMissing)
                return existing;

            Transform rig = FindInteractionRig();
            if (rig == null)
                return null;

            // Put the component on the rig's parent (the camera rig) so it survives prefab re-imports.
            GameObject host = rig.parent != null ? rig.parent.gameObject : rig.gameObject;
            var added = Undo.AddComponent<DualInputRigSetup>(host);
            Debug.Log($"Added DualInputRigSetup to '{host.name}'.");
            return added;
        }

        private static Transform FindInteractionRig()
        {
            foreach (Transform t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string name = t.name.ToLowerInvariant();
                if (name.Contains("ovrinteractioncomprehensive"))
                    return t;
            }

            foreach (Transform t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t.name.ToLowerInvariant().Contains("ovrinteraction"))
                    return t;
            }

            return null;
        }

        private static void SetPrivateBool(DualInputRigSetup setup, string fieldName, bool value)
        {
            var so = new SerializedObject(setup);
            SerializedProperty prop = so.FindProperty(fieldName);
            if (prop == null) return;
            prop.boolValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
