using UnityEditor;
using UnityEngine;

namespace MedicalExamEditor
{
    /// <summary>
    /// Entry point for command-line Android builds (Unity -batchmode -executeMethod
    /// MedicalExamEditor.CommandLineBuild.BuildAndroid). Uses whatever scenes and player
    /// settings are already configured in this project — same output as a normal
    /// Build and Run from the Editor, just scriptable from a terminal.
    /// </summary>
    public static class CommandLineBuild
    {
        public static void BuildAndroid()
        {
            string[] scenes = System.Array.ConvertAll(
                System.Array.FindAll(EditorBuildSettings.scenes, s => s.enabled),
                s => s.path);

            string outputPath = "Builds/Android/MedicalExam.apk";
            System.IO.Directory.CreateDirectory("Builds/Android");

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            });

            var summary = report.summary;
            Debug.Log($"[CommandLineBuild] Result={summary.result} Errors={summary.totalErrors} Warnings={summary.totalWarnings} Size={summary.totalSize}");

            if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }
    }
}
