using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace MedicalExam
{
    public class SessionEvidenceLogger
    {
        private readonly object _lock = new object();
        private readonly string _logFilePath;

#if UNITY_EDITOR
        private readonly string _editorMirrorPath;
#endif

        public string LogFilePath => _logFilePath;

        public SessionEvidenceLogger(string fileName = "medical_exam_evidence_log.txt")
        {
            var root = Application.persistentDataPath;
            _logFilePath = Path.Combine(root, fileName);

#if UNITY_EDITOR
            // In the Editor it's easy to miss persistentDataPath; mirror the log into Assets for convenience.
            _editorMirrorPath = Path.Combine(Application.dataPath, fileName);
#endif
        }

        /// <param name="schemaHint">
        /// Short description of the expected JSON output schema so the evaluator model
        /// knows which fields to produce. Pass the role-specific hint from the caller
        /// (D2D vs D2P schemas differ). Leave empty for a generic description.
        /// </param>
        public void StartSession(string title, string schemaHint = "")
        {
            // Clear the old log file on session start
            ClearLog();

            // Write a clean AI-ready header aligned with the actual evaluation schema.
            AppendLine("=== MEDICAL EXAM EVALUATION LOG ===");
            AppendLine("");
            AppendLine("SYSTEM ROLE:");
            AppendLine("You are a strict medical oral exam evaluator. Analyze the student's performance");
            AppendLine("in the recorded conversation and return structured JSON feedback.");
            AppendLine("");
            if (!string.IsNullOrWhiteSpace(schemaHint))
            {
                AppendLine("EXPECTED OUTPUT SCHEMA:");
                AppendLine(schemaHint.Trim());
            }
            else
            {
                AppendLine("EXPECTED OUTPUT SCHEMA:");
                AppendLine("D2D — criteria: {content, conversation, vocabulary, grammar, pronunciation}, finalVerdict, totalScore (/20), strengths, areasForImprovement, criticalErrors, overallFeedback");
                AppendLine("D2P — criteria: {Kommunikation, Verständnis, Struktur, Empathie, Vollständigkeit}, totalScore (/20), generalFeedback");
            }
            AppendLine("");
            AppendLine("EVALUATION DATE: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            AppendLine("SESSION: " + (string.IsNullOrWhiteSpace(title) ? "Unknown" : title.Trim()));
            AppendLine("");
            AppendLine("--- EVALUATION DATA ---");
            AppendLine("");
        }

        public void ClearLog()
        {
            lock (_lock)
            {
                try
                {
                    // Delete the persistent log file if it exists
                    if (File.Exists(_logFilePath))
                        File.Delete(_logFilePath);
                    Debug.Log($"[SessionEvidenceLogger] Cleared evidence log: {_logFilePath}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SessionEvidenceLogger] Failed to clear log: {ex.Message}");
                }

#if UNITY_EDITOR
                try
                {
                    // Also delete the editor mirror
                    if (File.Exists(_editorMirrorPath))
                        File.Delete(_editorMirrorPath);
                }
                catch { }
#endif
            }
        }

        public void AppendLine(string line)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath) ?? Application.persistentDataPath);
                    File.AppendAllText(_logFilePath, (line ?? string.Empty) + "\n", Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SessionEvidenceLogger] Failed to append log: {ex.Message}");
                }

#if UNITY_EDITOR
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_editorMirrorPath) ?? Application.dataPath);
                    File.AppendAllText(_editorMirrorPath, (line ?? string.Empty) + "\n", Encoding.UTF8);
                }
                catch { }
#endif
            }
        }

        public void AppendSection(string title, string content)
        {
            if (string.IsNullOrWhiteSpace(title))
                title = "SECTION";

            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath) ?? Application.persistentDataPath);
                    using (var sw = new StreamWriter(_logFilePath, append: true, Encoding.UTF8))
                    {
                        sw.WriteLine("--- " + title.Trim() + " ---");
                        if (!string.IsNullOrEmpty(content))
                            sw.WriteLine(content);
                        sw.WriteLine("--- END " + title.Trim() + " ---\n");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SessionEvidenceLogger] Failed to append section: {ex.Message}");
                }

#if UNITY_EDITOR
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_editorMirrorPath) ?? Application.dataPath);
                    using (var sw = new StreamWriter(_editorMirrorPath, append: true, Encoding.UTF8))
                    {
                        sw.WriteLine("--- " + title.Trim() + " ---");
                        if (!string.IsNullOrEmpty(content))
                            sw.WriteLine(content);
                        sw.WriteLine("--- END " + title.Trim() + " ---\n");
                    }
                }
                catch { }
#endif
            }
        }
    }
}
