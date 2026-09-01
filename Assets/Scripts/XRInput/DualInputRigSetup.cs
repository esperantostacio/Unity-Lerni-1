using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MedicalExam.XRInput
{
    /// <summary>
    /// Makes the Meta Interaction rig usable with BOTH Touch controllers and bare hands.
    ///
    /// Why this exists: the scene's "[BuildingBlock] OVRInteractionComprehensive" prefab instance
    /// ships with hand AND controller interactor branches, but this project had ~20 of those child
    /// objects deactivated (the app used to be hands-only, see OculusProjectConfig.handTrackingSupport).
    /// Re-enabling them by hand inside a prefab instance is easy to get wrong, so this component
    /// does it deterministically at load time (and can also be applied in the Editor from
    /// Tools > Lerni > XR).
    ///
    /// It deliberately never enables locomotion: anything on the rig that looks like thumbstick
    /// movement / teleport / snap turn is switched OFF, so the joystick cannot move the player.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    [DisallowMultipleComponent]
    public class DualInputRigSetup : MonoBehaviour
    {
        [Header("Rig")]
        [Tooltip("Root of the Meta Interaction rig. Leave empty to auto-find '[BuildingBlock] OVRInteractionComprehensive' / 'OVRInteraction' in the scene.")]
        [SerializeField] private Transform interactionRigRoot;

        [Header("What to enable")]
        [Tooltip("Activate the controller branches (OVRControllers, ControllerInteractors, controller visuals...).")]
        [SerializeField] private bool enableControllerInput = true;
        [Tooltip("Activate the hand-tracking branches (OVRHands, HandInteractors...).")]
        [SerializeField] private bool enableHandInput = true;

        [Header("Which interactor kinds")]
        [Tooltip("Ray interactors: needed to point at the world-space UI from a distance. Keep this on.")]
        [SerializeField] private bool enableRayInteractors = true;
        [Tooltip("Poke interactors: touch the UI directly with a fingertip / controller tip.")]
        [SerializeField] private bool enablePokeInteractors = true;
        [Tooltip("Grab interactors: pick up physical objects. Off by default — this app is UI driven.")]
        [SerializeField] private bool enableGrabInteractors = false;
        [Tooltip("Distance-grab interactors. Off by default.")]
        [SerializeField] private bool enableDistanceGrabInteractors = false;

        [Header("Locomotion (kept off on purpose)")]
        [Tooltip("Disable every locomotion / teleport / turn object and component found on the rig, so the thumbstick cannot move the player.")]
        [SerializeField] private bool disableJoystickLocomotion = true;

        [Header("Overrides")]
        [Tooltip("Extra object names (exact, case-insensitive) to force ON.")]
        [SerializeField] private string[] extraNamesToEnable = new string[0];
        [Tooltip("Extra object names (exact, case-insensitive) to force OFF. Applied last, so it always wins.")]
        [SerializeField] private string[] extraNamesToDisable = new string[0];

        [Header("Diagnostics")]
        [Tooltip("Print the whole interaction rig hierarchy (with active state) to the console on startup. Useful when the rig's child names differ from the defaults.")]
        [SerializeField] private bool logRigTreeOnStart = true;
        [Tooltip("Report what would change without changing anything.")]
        [SerializeField] private bool dryRun = false;

        private const string LogPrefix = "[DualInputRigSetup]";

        // Names that mean "this branch belongs to controller input".
        private static readonly string[] ControllerKeywords = { "controller" };

        // Names that mean "this branch belongs to hand tracking".
        private static readonly string[] HandKeywords = { "hand", "finger", "pinch" };

        // Anything matching these is locomotion and stays off.
        private static readonly string[] LocomotionKeywords =
        {
            "locomot", "teleport", "turner", "snapturn", "smoothturn", "joystick", "thumbstick"
        };

        private void Awake()
        {
            Apply();
        }

        /// <summary>
        /// Applies the enable/disable rules. Safe to call from the Editor as well as at runtime.
        /// Returns a human readable report of what changed.
        /// </summary>
        public string Apply()
        {
            Transform root = ResolveRigRoot();
            var report = new StringBuilder();

            if (root == null)
            {
                Debug.LogWarning($"{LogPrefix} No Meta Interaction rig found. Assign 'interactionRigRoot' in the inspector " +
                                 "(it is the '[BuildingBlock] OVRInteractionComprehensive' object under the camera rig).");
                return "No interaction rig found.";
            }

            report.AppendLine($"{LogPrefix} Rig root: {GetPath(root)}");

            if (logRigTreeOnStart)
                Debug.Log($"{LogPrefix} Interaction rig before setup:\n{DumpTree(root)}");

            // Shallow-first, so a parent is activated before the children that live under it.
            var nodes = new List<Transform>(root.GetComponentsInChildren<Transform>(true));
            nodes.Sort((a, b) => Depth(a).CompareTo(Depth(b)));

            int enabledCount = 0, disabledCount = 0;

            foreach (Transform node in nodes)
            {
                if (node == root) continue;

                bool? desired = DecideActiveState(node.name);
                if (!desired.HasValue) continue;

                if (node.gameObject.activeSelf == desired.Value) continue;

                report.AppendLine($"  {(desired.Value ? "ENABLE " : "DISABLE")} {GetPath(node)}");
                if (!dryRun)
                    node.gameObject.SetActive(desired.Value);

                if (desired.Value) enabledCount++; else disabledCount++;
            }

            if (disableJoystickLocomotion)
                disabledCount += DisableLocomotionComponents(root, report);

            report.AppendLine($"{LogPrefix} Done. {enabledCount} object(s) enabled, {disabledCount} object(s)/component(s) disabled." +
                              (dryRun ? "  (DRY RUN — nothing was actually changed.)" : ""));

            if (logRigTreeOnStart)
                report.AppendLine("Interaction rig after setup:\n" + DumpTree(root));

            string text = report.ToString();
            Debug.Log(text);
            return text;
        }

        /// <summary>
        /// true = force on, false = force off, null = leave exactly as the scene author left it.
        /// </summary>
        private bool? DecideActiveState(string rawName)
        {
            string name = rawName.ToLowerInvariant();

            // Explicit overrides first (disable list is re-checked at the end so it always wins).
            if (MatchesExact(extraNamesToEnable, rawName)) return true;
            if (MatchesExact(extraNamesToDisable, rawName)) return false;

            if (disableJoystickLocomotion && ContainsAny(name, LocomotionKeywords))
                return false;

            bool controllerBranch = ContainsAny(name, ControllerKeywords);
            bool handBranch = ContainsAny(name, HandKeywords);

            // "OVRControllerHands" / "ControllerHandLeft" are the controller-driven hand poses:
            // they belong to the controller branch, not to hand tracking.
            if (controllerBranch) handBranch = false;

            if (controllerBranch && !enableControllerInput) return null;
            if (handBranch && !enableHandInput) return null;
            if (!controllerBranch && !handBranch) return null;

            // Interactor kind gating. A disabled toggle means "leave it as the scene author set it"
            // rather than "switch it off", so this component only ever adds capability.
            if (name.Contains("interactor"))
            {
                if (name.Contains("distance") && name.Contains("grab")) return enableDistanceGrabInteractors ? true : (bool?)null;
                if (name.Contains("grab")) return enableGrabInteractors ? true : (bool?)null;
                if (name.Contains("poke")) return enablePokeInteractors ? true : (bool?)null;
                if (name.Contains("ray")) return enableRayInteractors ? true : (bool?)null;
                return true;
            }

            return true;
        }

        /// <summary>
        /// Disables locomotion behaviours by type name, wherever they live on the rig.
        /// Component-level (rather than object-level) so we do not take out anything sharing the object.
        /// </summary>
        private int DisableLocomotionComponents(Transform root, StringBuilder report)
        {
            int count = 0;

            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null || !behaviour.enabled) continue;

                string typeName = behaviour.GetType().FullName ?? string.Empty;
                string lower = typeName.ToLowerInvariant();

                bool isLocomotion = ContainsAny(lower, LocomotionKeywords) || lower.EndsWith("ovrplayercontroller");
                if (!isLocomotion) continue;

                report.AppendLine($"  DISABLE component {typeName} on {GetPath(behaviour.transform)}");
                if (!dryRun)
                    behaviour.enabled = false;
                count++;
            }

            // OVRPlayerController drives a CharacterController from the thumbstick; kill it too.
            foreach (CharacterController cc in root.GetComponentsInChildren<CharacterController>(true))
            {
                if (cc == null || !cc.enabled) continue;
                report.AppendLine($"  DISABLE CharacterController on {GetPath(cc.transform)}");
                if (!dryRun)
                    cc.enabled = false;
                count++;
            }

            return count;
        }

        private Transform ResolveRigRoot()
        {
            if (interactionRigRoot != null)
                return interactionRigRoot;

            // Prefer the building block, then any object called OVRInteraction*.
            Transform byBuildingBlock = FindByNameContains("ovrinteractioncomprehensive");
            if (byBuildingBlock != null)
            {
                interactionRigRoot = byBuildingBlock;
                return interactionRigRoot;
            }

            interactionRigRoot = FindByNameContains("ovrinteraction");
            return interactionRigRoot;
        }

        private static Transform FindByNameContains(string lowerNeedle)
        {
#if UNITY_2023_1_OR_NEWER
            Transform[] all = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
#endif
            foreach (Transform t in all)
            {
                if (t == null || t.gameObject.scene.IsValid() == false) continue;
                if (t.name.ToLowerInvariant().Contains(lowerNeedle))
                    return t;
            }
            return null;
        }

        private static bool ContainsAny(string lowerName, string[] keywords)
        {
            foreach (string keyword in keywords)
            {
                if (lowerName.Contains(keyword))
                    return true;
            }
            return false;
        }

        private static bool MatchesExact(string[] names, string candidate)
        {
            if (names == null) return false;
            foreach (string name in names)
            {
                if (!string.IsNullOrWhiteSpace(name) &&
                    string.Equals(name.Trim(), candidate, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static int Depth(Transform t)
        {
            int depth = 0;
            while (t.parent != null) { depth++; t = t.parent; }
            return depth;
        }

        private static string GetPath(Transform t)
        {
            var parts = new List<string>();
            while (t != null) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string DumpTree(Transform root)
        {
            var sb = new StringBuilder();
            DumpTree(root, 0, sb);
            return sb.ToString();
        }

        private static void DumpTree(Transform node, int depth, StringBuilder sb)
        {
            sb.Append(' ', depth * 2)
              .Append("- ")
              .Append(node.name)
              .Append(node.gameObject.activeSelf ? string.Empty : "   [INACTIVE]")
              .AppendLine();

            for (int i = 0; i < node.childCount; i++)
                DumpTree(node.GetChild(i), depth + 1, sb);
        }
    }
}
