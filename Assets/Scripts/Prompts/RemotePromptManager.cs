using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace MedicalExam
{
    /// <summary>
    /// Loads prompt text from a remote CSV (Google Sheets, Excel Online, etc.) at runtime.
    /// Falls back to built-in/local prompts when remote is unavailable.
    /// 
    /// Sheet schema (recommended columns):
    /// key, locale, enabled, version, text
    /// - locale: "de", "en", or "*" (fallback)
    /// - enabled: TRUE/FALSE (or 1/0)
    /// - version: integer preferred (higher wins)
    /// - text: the prompt text (can contain newlines)
    /// </summary>
    public class RemotePromptManager : MonoBehaviour
    {
        [Serializable]
        private sealed class PromptRow
        {
            public string key;
            public string locale;
            public bool enabled;
            public string version;
            public string text;
            public string scenarioId;
        }

        [Serializable]
        private sealed class PromptCache
        {
            public string sourceUrl;
            public string locale;
            public string fetchedAtIso;
            public Dictionary<string, PromptRow> selected;
        }

        private static RemotePromptManager _instance;

        [Header("Runtime State")]
        [SerializeField] private bool remoteEnabled;
        [SerializeField] private string csvUrl;
        [SerializeField] private string locale = "*";
        [SerializeField] private bool strictOnlineOnly;

        private readonly Dictionary<string, PromptRow> _selectedByKey = new Dictionary<string, PromptRow>(StringComparer.OrdinalIgnoreCase);
        // Scenario-specific prompts: key -> (scenarioId -> PromptRow)
        private readonly Dictionary<string, Dictionary<string, PromptRow>> _selectedByKeyAndScenario = new Dictionary<string, Dictionary<string, PromptRow>>(StringComparer.OrdinalIgnoreCase);
        private bool _initializedFromCache;
        // True once the remote fetch has completed (success or failure). Used to gate conversation start.
        private bool _remoteLoadCompleted = false;

        private const int DEFAULT_DUMP_MAX_KEYS = 25;
        private const int DEFAULT_DUMP_MAX_CHARS = 140;

        public static bool IsReady => _instance != null;
        /// <summary>True once the remote CSV fetch has finished (success or failure). Safe to read prompts after this.</summary>
        public static bool IsLoaded => _instance != null && _instance._remoteLoadCompleted;

        public static void EnsureStarted(bool enabled, string sheetCsvUrl, string promptLocale, bool strictOnlineOnly = false)
        {
            if (_instance == null)
            {
                var go = new GameObject("RemotePromptManager");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<RemotePromptManager>();
            }

            _instance.remoteEnabled = enabled;
            _instance.csvUrl = sheetCsvUrl;
            _instance.locale = string.IsNullOrWhiteSpace(promptLocale) ? "*" : promptLocale.Trim();
            _instance.strictOnlineOnly = strictOnlineOnly;

            if (!_instance.strictOnlineOnly)
                _instance.TryLoadCache();
            else
            {
                _instance._selectedByKey.Clear();
                _instance._selectedByKeyAndScenario.Clear();
            }

            if (_instance.remoteEnabled && !string.IsNullOrWhiteSpace(_instance.csvUrl))
            {
                _instance._remoteLoadCompleted = false;
                _instance.StopAllCoroutines();
                _instance.StartCoroutine(_instance.RefreshFromRemote());
            }
            else
            {
                // No remote fetch — mark loaded immediately so callers don't wait forever.
                _instance._remoteLoadCompleted = true;
            }
        }

        public static string Get(string key, string fallback)
        {
            if (string.IsNullOrWhiteSpace(key)) return fallback;
            if (_instance == null) return fallback;

            if (_instance._selectedByKey.TryGetValue(key.Trim(), out var row) && row != null && row.enabled)
            {
                return string.IsNullOrWhiteSpace(row.text) ? fallback : row.text;
            }

            return fallback;
        }

        /// <summary>
        /// Like Get(), but allows an intentionally empty string to override the fallback.
        /// Useful when you want to disable a greeting by setting the prompt text to "".
        /// </summary>
        public static string GetAllowEmpty(string key, string fallback)
        {
            if (string.IsNullOrWhiteSpace(key)) return fallback;
            if (_instance == null) return fallback;

            if (_instance._selectedByKey.TryGetValue(key.Trim(), out var row) && row != null && row.enabled)
            {
                return row.text ?? "";
            }

            return fallback;
        }

        /// <summary>
        /// Get a prompt with scenario-specific override. Tries the scenario-specific version first,
        /// then falls back to the generic prompt for this key.
        /// </summary>
        public static string GetForScenario(string key, string scenarioId, string fallback)
        {
            if (string.IsNullOrWhiteSpace(key)) return fallback;
            if (_instance == null) return fallback;

            string k = key.Trim();

            // Prefer explicit theme-suffixed keys first (e.g., realtime.phase.greeting.d2d.cardio)
            // so a single base key can route to cardio/nephro/neuro variants.
            string themedKey = BuildThemedKeyIfAny(k, scenarioId);
            if (!string.IsNullOrWhiteSpace(themedKey))
            {
                string themed = GetScenarioThenGeneric(themedKey, scenarioId, null);
                if (!string.IsNullOrWhiteSpace(themed))
                    return themed;
            }

            string baseResolved = GetScenarioThenGeneric(k, scenarioId, null);
            if (!string.IsNullOrWhiteSpace(baseResolved))
                return baseResolved;

            return fallback;
        }

        private static string GetScenarioThenGeneric(string key, string scenarioId, string fallback)
        {
            if (string.IsNullOrWhiteSpace(key)) return fallback;
            if (_instance == null) return fallback;

            string k = key.Trim();

            // Try scenario-specific prompt first
            if (!string.IsNullOrWhiteSpace(scenarioId))
            {
                string sid = scenarioId.Trim();
                if (_instance._selectedByKeyAndScenario.TryGetValue(k, out var scenarioDict)
                    && scenarioDict != null
                    && scenarioDict.TryGetValue(sid, out var scenarioRow)
                    && scenarioRow != null && scenarioRow.enabled
                    && !string.IsNullOrWhiteSpace(scenarioRow.text))
                {
                    return scenarioRow.text;
                }
            }

            // Fall back to generic prompt
            return Get(k, fallback);
        }

        private static string BuildThemedKeyIfAny(string baseKey, string scenarioId)
        {
            if (string.IsNullOrWhiteSpace(baseKey) || string.IsNullOrWhiteSpace(scenarioId))
                return null;

            string suffix = ResolveThemeSuffix(scenarioId);
            if (string.IsNullOrWhiteSpace(suffix))
                return null;

            return baseKey.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)
                ? baseKey
                : (baseKey + "." + suffix);
        }

        private static string ResolveThemeSuffix(string scenarioId)
        {
            if (string.IsNullOrWhiteSpace(scenarioId)) return null;

            string id = scenarioId.Trim().ToLowerInvariant();

            if (id == "cardio" || id == "cardiology" || id.Contains("cardio"))
                return "cardio";
            if (id == "nephro" || id == "nephrology" || id.Contains("nephro"))
                return "nephro";
            if (id == "neuro" || id == "neurology" || id.Contains("neuro"))
                return "neuro";

            return null;
        }

        /// <summary>
        /// Debug helper: prints the currently selected prompts (from cache and/or remote)
        /// so you can verify the online sheet is being used.
        /// </summary>
        public static void DumpToConsole(int maxKeys = DEFAULT_DUMP_MAX_KEYS, int maxChars = DEFAULT_DUMP_MAX_CHARS)
        {
            if (_instance == null)
            {
                Debug.Log("[RemotePromptManager] Dump requested but instance is null (not started yet)." );
                return;
            }

            _instance.DumpSelectedToConsole("Manual dump", maxKeys, maxChars);
        }

        private string CachePath => Path.Combine(Application.persistentDataPath, "remote_prompts_cache.json");

        private void TryLoadCache()
        {
            if (_initializedFromCache) return;

            try
            {
                if (!File.Exists(CachePath)) return;
                var json = File.ReadAllText(CachePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return;

                var cache = JsonConvert.DeserializeObject<PromptCache>(json);
                if (cache?.selected == null) return;

                _selectedByKey.Clear();
                foreach (var kvp in cache.selected)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null) continue;
                    _selectedByKey[kvp.Key] = kvp.Value;
                }

                _initializedFromCache = _selectedByKey.Count > 0;
                if (_initializedFromCache)
                {
                    Debug.Log($"[RemotePromptManager] Loaded {_selectedByKey.Count} prompts from cache.");
                    DumpSelectedToConsole("Cache load", DEFAULT_DUMP_MAX_KEYS, DEFAULT_DUMP_MAX_CHARS);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemotePromptManager] Failed to load cache: {ex.Message}");
            }
        }

        private void SaveCache()
        {
            try
            {
                var cache = new PromptCache
                {
                    sourceUrl = csvUrl,
                    locale = locale,
                    fetchedAtIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    selected = new Dictionary<string, PromptRow>(_selectedByKey, StringComparer.OrdinalIgnoreCase)
                };

                var json = JsonConvert.SerializeObject(cache, Formatting.Indented);
                File.WriteAllText(CachePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemotePromptManager] Failed to save cache: {ex.Message}");
            }
        }

        private IEnumerator RefreshFromRemote()
        {
            if (string.IsNullOrWhiteSpace(csvUrl)) { _remoteLoadCompleted = true; yield break; }

            Debug.Log($"[RemotePromptManager] Fetching prompts from remote CSV: {csvUrl}");

            using (var request = UnityWebRequest.Get(csvUrl))
            {
                request.timeout = 10;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    // Do NOT clear existing cache — keep whatever was loaded from disk so the app still works offline.
                    _remoteLoadCompleted = true;
                    Debug.LogError($"[RemotePromptManager] Remote prompt fetch failed: {request.error} (HTTP {request.responseCode}). " +
                                   $"Keeping {_selectedByKey.Count} cached prompts as fallback. strictOnlineOnly={strictOnlineOnly}.");
                    yield break;
                }

                var csv = request.downloadHandler.text;
                if (string.IsNullOrWhiteSpace(csv))
                {
                    _remoteLoadCompleted = true;
                    Debug.LogError($"[RemotePromptManager] Remote prompt CSV was empty. " +
                                   $"Keeping {_selectedByKey.Count} cached prompts as fallback. strictOnlineOnly={strictOnlineOnly}.");
                    yield break;
                }

                // Common misconfiguration: using the published HTML page (pubhtml) instead of a CSV export.
                // In that case parsing may succeed but produce 0 usable rows/headers.
                var trimmed = csv.TrimStart();
                if (trimmed.StartsWith("<", StringComparison.Ordinal) && (trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)))
                {
                    string preview = trimmed.Length > 260 ? trimmed.Substring(0, 260) + "..." : trimmed;
                    _remoteLoadCompleted = true;
                    Debug.LogError("[RemotePromptManager] Remote URL returned HTML, not CSV. Keeping cached prompts as fallback.\n" +
                                     "Fix: use a URL that returns the CSV bytes directly (no auth prompt).\n" +
                                     "Examples:\n" +
                                     "- Google Sheets CSV export: https://docs.google.com/spreadsheets/d/<SHEET_ID>/gviz/tq?tqx=out:csv&sheet=Prompts\n" +
                                     "- Google Sheets published CSV: https://docs.google.com/spreadsheets/d/e/<PUBLISHED_ID>/pub?output=csv\n" +
                                     "- Excel/OneDrive/SharePoint: create a direct download/export-to-CSV link that returns text/csv (not an HTML page).\n" +
                                     $"Response preview: {preview}");
                    yield break;
                }

                if (!TryParseCsv(csv, out var rows, out var parseError))
                {
                    _remoteLoadCompleted = true;
                    Debug.LogError($"[RemotePromptManager] Failed to parse CSV: {parseError}. " +
                                   $"Keeping {_selectedByKey.Count} cached prompts as fallback. strictOnlineOnly={strictOnlineOnly}.");
                    yield break;
                }

                var parsed = ParseRows(rows);
                if (parsed.Count == 0)
                {
                    // Print header info to help debug column names / sheet format.
                    string header = (rows != null && rows.Count > 0 && rows[0] != null)
                        ? string.Join("|", rows[0])
                        : "<no header row>";
                    string firstDataRow = (rows != null && rows.Count > 1 && rows[1] != null)
                        ? string.Join("|", rows[1])
                        : "<no data rows>";
                    _remoteLoadCompleted = true;
                    Debug.LogError("[RemotePromptManager] Parsed 0 prompts from sheet. Keeping cached prompts as fallback. " +
                                     $"HeaderRow='{header}' FirstDataRow='{firstDataRow}'. " +
                                     "Expected columns include: key, locale, enabled, version, text.");
                    yield break;
                }

                _selectedByKey.Clear();
                _selectedByKeyAndScenario.Clear();
                foreach (var kvp in SelectBestByKey(parsed, locale))
                {
                    var promptRow = kvp.Value;
                    if (!string.IsNullOrWhiteSpace(promptRow.scenarioId))
                    {
                        // Scenario-specific prompt → store in the scenario dictionary under original key
                        if (!_selectedByKeyAndScenario.TryGetValue(promptRow.key, out var scenarioDict))
                        {
                            scenarioDict = new Dictionary<string, PromptRow>(StringComparer.OrdinalIgnoreCase);
                            _selectedByKeyAndScenario[promptRow.key] = scenarioDict;
                        }
                        scenarioDict[promptRow.scenarioId] = promptRow;
                    }
                    else
                    {
                        // Generic prompt → store normally under original key
                        _selectedByKey[promptRow.key] = promptRow;
                    }
                }

                _remoteLoadCompleted = true;
                Debug.Log($"[RemotePromptManager] Loaded {_selectedByKey.Count} generic prompts + {_selectedByKeyAndScenario.Count} scenario-scoped prompt keys from remote.");
                DumpSelectedToConsole("Remote load", DEFAULT_DUMP_MAX_KEYS, DEFAULT_DUMP_MAX_CHARS);
                SaveCache();
            }
        }

        private void DumpSelectedToConsole(string reason, int maxKeys, int maxChars)
        {
            try
            {
                int total = _selectedByKey.Count;
                string src = string.IsNullOrWhiteSpace(csvUrl) ? "<none>" : csvUrl;
                Debug.Log($"[RemotePromptManager] === PROMPTS DUMP ({reason}) === locale='{locale}' remoteEnabled={remoteEnabled} url={src} totalKeys={total}");

                if (total == 0)
                {
                    Debug.Log("[RemotePromptManager] (no prompts loaded)");
                    Debug.Log("[RemotePromptManager] === END PROMPTS DUMP ===");
                    return;
                }

                int limit = Mathf.Clamp(maxKeys, 1, 200);
                int count = 0;
                foreach (var kvp in _selectedByKey)
                {
                    if (count >= limit) break;
                    var row = kvp.Value;
                    if (row == null) continue;

                    string txt = row.text ?? "";
                    txt = txt.Replace("\r", "").Replace("\n", " ");
                    if (txt.Length > maxChars) txt = txt.Substring(0, maxChars) + "...";

                    Debug.Log($"[RemotePromptManager] key='{row.key}' locale='{row.locale}' enabled={row.enabled} version='{row.version}' textPreview='{txt}'");
                    count++;
                }

                if (total > limit)
                    Debug.Log($"[RemotePromptManager] ... plus {total - limit} more keys.");

                Debug.Log("[RemotePromptManager] === END PROMPTS DUMP ===");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemotePromptManager] Dump failed: {ex.Message}");
            }
        }

        private static Dictionary<string, List<PromptRow>> ParseRows(List<string[]> table)
        {
            var byKey = new Dictionary<string, List<PromptRow>>(StringComparer.OrdinalIgnoreCase);
            if (table == null || table.Count < 2) return byKey;

            var header = table[0];
            int keyIdx = IndexOf(header, "key");
            int localeIdx = IndexOf(header, "locale");
            int enabledIdx = IndexOf(header, "enabled");
            int versionIdx = IndexOf(header, "version");
            int textIdx = IndexOf(header, "text");
            int scenarioIdIdx = IndexOf(header, "scenario_id");

            if (keyIdx < 0 || textIdx < 0)
                return byKey;

            for (int i = 1; i < table.Count; i++)
            {
                var row = table[i];
                if (row == null || row.Length == 0) continue;

                string key = GetCell(row, keyIdx);
                if (string.IsNullOrWhiteSpace(key)) continue;

                var pr = new PromptRow
                {
                    key = key.Trim(),
                    locale = (localeIdx >= 0 ? GetCell(row, localeIdx) : "*")?.Trim(),
                    enabled = enabledIdx < 0 || ParseBool(GetCell(row, enabledIdx)),
                    version = versionIdx >= 0 ? GetCell(row, versionIdx) : "0",
                    text = NormalizePromptText(GetCell(row, textIdx)),
                    scenarioId = scenarioIdIdx >= 0 ? GetCell(row, scenarioIdIdx)?.Trim() : ""
                };

                // Use compound key "key::scenarioId" for scenario-specific rows so they don't
                // compete with generic rows during locale/version selection.
                string groupKey = string.IsNullOrWhiteSpace(pr.scenarioId) ? pr.key : $"{pr.key}::{pr.scenarioId}";

                if (!byKey.TryGetValue(groupKey, out var list))
                {
                    list = new List<PromptRow>();
                    byKey[groupKey] = list;
                }
                list.Add(pr);
            }

            return byKey;
        }

        private static Dictionary<string, PromptRow> SelectBestByKey(Dictionary<string, List<PromptRow>> parsed, string desiredLocale)
        {
            var selected = new Dictionary<string, PromptRow>(StringComparer.OrdinalIgnoreCase);
            string loc = string.IsNullOrWhiteSpace(desiredLocale) ? "*" : desiredLocale.Trim();

            foreach (var kvp in parsed)
            {
                var list = kvp.Value;
                if (list == null || list.Count == 0) continue;

                PromptRow best = null;
                foreach (var row in list)
                {
                    if (row == null) continue;
                    if (!row.enabled) continue;

                    bool exact = string.Equals(row.locale ?? "*", loc, StringComparison.OrdinalIgnoreCase);
                    bool wildcard = string.IsNullOrWhiteSpace(row.locale) || string.Equals(row.locale, "*", StringComparison.OrdinalIgnoreCase);

                    // scoring: exact locale > wildcard; then higher version wins
                    int rowScore = (exact ? 2 : wildcard ? 1 : 0);
                    if (rowScore == 0) continue;

                    if (best == null)
                    {
                        best = row;
                        continue;
                    }

                    int bestScore = (string.Equals(best.locale ?? "*", loc, StringComparison.OrdinalIgnoreCase) ? 2 : (string.IsNullOrWhiteSpace(best.locale) || string.Equals(best.locale, "*", StringComparison.OrdinalIgnoreCase) ? 1 : 0));
                    if (rowScore > bestScore)
                    {
                        best = row;
                        continue;
                    }

                    if (rowScore == bestScore && CompareVersion(row.version, best.version) > 0)
                        best = row;
                }

                if (best != null)
                    selected[kvp.Key] = best;
            }

            return selected;
        }

        internal static int CompareVersion(string a, string b)
        {
            if (int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ai) &&
                int.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bi))
            {
                return ai.CompareTo(bi);
            }

            return string.Compare(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        }

        internal static int IndexOf(string[] header, string name)
        {
            if (header == null) return -1;
            for (int i = 0; i < header.Length; i++)
            {
                if (string.Equals(header[i]?.Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        internal static string GetCell(string[] row, int idx)
        {
            if (row == null || idx < 0 || idx >= row.Length) return "";
            return row[idx] ?? "";
        }

        internal static bool ParseBool(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v != 0;
            return false;
        }

        /// <summary>
        /// RFC4180-ish CSV parser that supports quoted fields and newlines inside quotes.
        /// Returns all rows as string arrays.
        /// </summary>
        internal static bool TryParseCsv(string csv, out List<string[]> table, out string error)
        {
            table = new List<string[]>();
            error = null;

            try
            {
                char delimiter = DetectDelimiter(csv);
                var row = new List<string>();
                var cell = new StringBuilder();
                bool inQuotes = false;

                for (int i = 0; i < csv.Length; i++)
                {
                    char c = csv[i];

                    if (inQuotes)
                    {
                        if (c == '"')
                        {
                            // Escaped quote
                            if (i + 1 < csv.Length && csv[i + 1] == '"')
                            {
                                cell.Append('"');
                                i++;
                            }
                            else
                            {
                                inQuotes = false;
                            }
                        }
                        else
                        {
                            cell.Append(c);
                        }
                        continue;
                    }

                    if (c == '"')
                    {
                        inQuotes = true;
                        continue;
                    }

                    if (c == delimiter)
                    {
                        row.Add(cell.ToString());
                        cell.Length = 0;
                        continue;
                    }

                    if (c == '\r')
                    {
                        // ignore; handle on \n
                        continue;
                    }

                    if (c == '\n')
                    {
                        row.Add(cell.ToString());
                        cell.Length = 0;
                        table.Add(row.ToArray());
                        row = new List<string>();
                        continue;
                    }

                    cell.Append(c);
                }

                // finalize last cell/row
                row.Add(cell.ToString());
                if (row.Count > 1 || (row.Count == 1 && !string.IsNullOrEmpty(row[0])))
                    table.Add(row.ToArray());

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Excel exports sometimes use ';' as the delimiter (regional settings), while others use ','.
        /// Auto-detect the delimiter by analyzing the header row (outside of quotes).
        /// </summary>
        private static char DetectDelimiter(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return ',';

            int commas = 0;
            int semicolons = 0;
            int tabs = 0;
            bool inQuotes = false;

            for (int i = 0; i < csv.Length; i++)
            {
                char c = csv[i];

                if (c == '"')
                {
                    // Handle escaped quotes inside quoted fields.
                    if (inQuotes && i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        i++;
                        continue;
                    }
                    inQuotes = !inQuotes;
                    continue;
                }

                if (!inQuotes)
                {
                    if (c == ',') commas++;
                    else if (c == ';') semicolons++;
                    else if (c == '\t') tabs++;
                    else if (c == '\n') break;
                }
            }

            if (tabs > commas && tabs > semicolons) return '\t';
            if (semicolons > commas) return ';';
            return ',';
        }

        private static string NormalizePromptText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Many Google Sheets setups store prompts with literal escape sequences like "\n" and "\"".
            // Our CSV parser already supports true newlines inside quotes; this is just a compatibility layer.
            if (text.IndexOf('\\') < 0) return text;

            return text
                .Replace("\\r\\n", "\n")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t")
                .Replace("\\\"", "\"");
        }
    }

    public class RemoteScenarioManager : MonoBehaviour
    {
        [Serializable]
        public sealed class ScenarioRow
        {
            public string scenarioId;
            public string locale;
            public bool enabled;
            public string version;
            public string scenarioName;
            public string scenarioContext;
            public string topicD2D;
            public string topicD2P;
            public string ragTags;
            public int durationMinutes;
            public string theme;
            public string termsJson;
            public string coreFactsJson;
            public string expectedQuestionsJson;
            public string evaluationFocusJson;
            public string casesJson;
            public string voiceWelcomeD2D;
            public string voiceWelcomeD2P;
            public string voiceRealtimeD2D;
            public string voiceRealtimeD2P;
            public string voiceEvalFemale;
            public string voiceEvalMale;
        }

        [Serializable]
        private sealed class ScenarioCache
        {
            public string sourceUrl;
            public string locale;
            public string fetchedAtIso;
            public Dictionary<string, ScenarioRow> selected;
        }

        private static RemoteScenarioManager _instance;

        [SerializeField] private bool remoteEnabled;
        [SerializeField] private string csvUrl;
        [SerializeField] private string locale = "*";
        [SerializeField] private bool strictOnlineOnly;

        private readonly Dictionary<string, ScenarioRow> _selectedByScenarioId = new Dictionary<string, ScenarioRow>(StringComparer.OrdinalIgnoreCase);
        private bool _initializedFromCache;

        public static bool IsReady => _instance != null;

        public static void EnsureStarted(bool enabled, string sameCsvUrl, string desiredLocale, bool strictOnlineOnly = false)
        {
            if (_instance == null)
            {
                var go = new GameObject("RemoteScenarioManager");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<RemoteScenarioManager>();
            }

            _instance.remoteEnabled = enabled;
            _instance.csvUrl = sameCsvUrl;
            _instance.locale = string.IsNullOrWhiteSpace(desiredLocale) ? "*" : desiredLocale.Trim();
            _instance.strictOnlineOnly = strictOnlineOnly;

            if (!_instance.strictOnlineOnly)
                _instance.TryLoadCache();
            else
                _instance._selectedByScenarioId.Clear();

            if (_instance.remoteEnabled && !string.IsNullOrWhiteSpace(_instance.csvUrl))
            {
                _instance.StopAllCoroutines();
                _instance.StartCoroutine(_instance.RefreshFromRemote());
            }
        }

        public static bool TryGetScenario(string scenarioId, out ScenarioRow row)
        {
            row = null;
            if (_instance == null) return false;

            if (!string.IsNullOrWhiteSpace(scenarioId) && _instance._selectedByScenarioId.TryGetValue(scenarioId.Trim(), out row) && row != null)
                return true;

            if (_instance._selectedByScenarioId.TryGetValue("default", out row) && row != null)
                return true;

            foreach (var kv in _instance._selectedByScenarioId)
            {
                if (kv.Value != null)
                {
                    row = kv.Value;
                    return true;
                }
            }

            return false;
        }

        public static string ResolveRagTags(string scenarioId)
        {
            if (TryGetScenario(scenarioId, out var row) && row != null)
                return row.ragTags ?? "";
            return "";
        }

        /// <summary>
        /// Returns all available scenario IDs (excluding "default") for random selection.
        /// </summary>
        public static List<string> GetAllScenarioIds(bool excludeDefault = true)
        {
            var ids = new List<string>();
            if (_instance == null) return ids;

            foreach (var kvp in _instance._selectedByScenarioId)
            {
                if (kvp.Value == null || !kvp.Value.enabled) continue;
                if (excludeDefault && string.Equals(kvp.Key, "default", StringComparison.OrdinalIgnoreCase)) continue;
                ids.Add(kvp.Key);
            }
            return ids;
        }

        private string CachePath => Path.Combine(Application.persistentDataPath, "remote_scenarios_cache.json");

        private void TryLoadCache()
        {
            if (_initializedFromCache) return;

            try
            {
                if (!File.Exists(CachePath)) return;
                var json = File.ReadAllText(CachePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return;

                var cache = JsonConvert.DeserializeObject<ScenarioCache>(json);
                if (cache?.selected == null) return;

                _selectedByScenarioId.Clear();
                foreach (var kvp in cache.selected)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null) continue;
                    _selectedByScenarioId[kvp.Key] = kvp.Value;
                }

                _initializedFromCache = _selectedByScenarioId.Count > 0;
                if (_initializedFromCache)
                    Debug.Log($"[RemoteScenarioManager] Loaded {_selectedByScenarioId.Count} scenarios from cache.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemoteScenarioManager] Failed to load cache: {ex.Message}");
            }
        }

        private void SaveCache()
        {
            try
            {
                var cache = new ScenarioCache
                {
                    sourceUrl = csvUrl,
                    locale = locale,
                    fetchedAtIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    selected = new Dictionary<string, ScenarioRow>(_selectedByScenarioId, StringComparer.OrdinalIgnoreCase)
                };

                var json = JsonConvert.SerializeObject(cache, Formatting.Indented);
                File.WriteAllText(CachePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemoteScenarioManager] Failed to save cache: {ex.Message}");
            }
        }

        private IEnumerator RefreshFromRemote()
        {
            if (string.IsNullOrWhiteSpace(csvUrl)) yield break;

            using (var request = UnityWebRequest.Get(csvUrl))
            {
                request.timeout = 10;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    _selectedByScenarioId.Clear();
                    Debug.LogError($"[RemoteScenarioManager] Scenario fetch failed: {request.error} (HTTP {request.responseCode}). strictOnlineOnly={strictOnlineOnly}. No fallback will be used.");
                    yield break;
                }

                var csv = request.downloadHandler.text;
                if (string.IsNullOrWhiteSpace(csv))
                {
                    _selectedByScenarioId.Clear();
                    Debug.LogError($"[RemoteScenarioManager] Scenario CSV response was empty. strictOnlineOnly={strictOnlineOnly}. No fallback will be used.");
                    yield break;
                }

                if (!RemotePromptManager.TryParseCsv(csv, out var table, out var parseError))
                {
                    _selectedByScenarioId.Clear();
                    Debug.LogError($"[RemoteScenarioManager] Failed to parse CSV: {parseError}. strictOnlineOnly={strictOnlineOnly}. No fallback will be used.");
                    yield break;
                }

                var parsed = ParseScenarioRows(table);
                if (parsed.Count == 0)
                {
                    _selectedByScenarioId.Clear();
                    Debug.LogError("[RemoteScenarioManager] No scenario rows found (expected scenario_id/topic_d2d/topic_d2p columns). No fallback will be used.");
                    yield break;
                }

                _selectedByScenarioId.Clear();
                foreach (var kvp in SelectBestByScenarioId(parsed, locale))
                    _selectedByScenarioId[kvp.Key] = kvp.Value;

                SaveCache();
                Debug.Log($"[RemoteScenarioManager] Loaded {_selectedByScenarioId.Count} scenarios from remote CSV.");
            }
        }

        private static Dictionary<string, List<ScenarioRow>> ParseScenarioRows(List<string[]> table)
        {
            var byScenarioId = new Dictionary<string, List<ScenarioRow>>(StringComparer.OrdinalIgnoreCase);
            if (table == null || table.Count < 2) return byScenarioId;

            var header = table[0];
            int idIdx = RemotePromptManager.IndexOf(header, "scenario_id");
            int localeIdx = RemotePromptManager.IndexOf(header, "locale");
            int enabledIdx = RemotePromptManager.IndexOf(header, "enabled");
            int versionIdx = RemotePromptManager.IndexOf(header, "version");
            int nameIdx = IndexOfAny(header, "scenario_name", "name");
            int contextIdx = IndexOfAny(header, "scenario_context", "context");
            int d2dIdx = RemotePromptManager.IndexOf(header, "topic_d2d");
            int d2pIdx = RemotePromptManager.IndexOf(header, "topic_d2p");
            int ragTagsIdx = RemotePromptManager.IndexOf(header, "rag_tags");
            int durationMinutesIdx = RemotePromptManager.IndexOf(header, "duration_minutes");
            int themeIdx = RemotePromptManager.IndexOf(header, "theme");
            int termsIdx = RemotePromptManager.IndexOf(header, "terms");
            int coreFactsIdx = RemotePromptManager.IndexOf(header, "core_facts");
            int expectedQuestionsIdx = RemotePromptManager.IndexOf(header, "expected_questions");
            int evaluationFocusIdx = RemotePromptManager.IndexOf(header, "evaluation_focus");
            int casesJsonIdx = IndexOfAny(header, "cases_json", "cases", "case_library_json", "case_library");
            int voiceWelcomeD2DIdx = RemotePromptManager.IndexOf(header, "voice_welcome_d2d");
            int voiceWelcomeD2PIdx = RemotePromptManager.IndexOf(header, "voice_welcome_d2p");
            int voiceRealtimeD2DIdx = RemotePromptManager.IndexOf(header, "voice_realtime_d2d");
            int voiceRealtimeD2PIdx = RemotePromptManager.IndexOf(header, "voice_realtime_d2p");
            int voiceEvalFemaleIdx = RemotePromptManager.IndexOf(header, "voice_eval_female");
            int voiceEvalMaleIdx = RemotePromptManager.IndexOf(header, "voice_eval_male");

            if (idIdx < 0) return byScenarioId;

            for (int i = 1; i < table.Count; i++)
            {
                var row = table[i];
                if (row == null || row.Length == 0) continue;

                string scenarioId = RemotePromptManager.GetCell(row, idIdx)?.Trim();
                if (string.IsNullOrWhiteSpace(scenarioId))
                    continue;

                var sr = new ScenarioRow
                {
                    scenarioId = scenarioId,
                    locale = (localeIdx >= 0 ? RemotePromptManager.GetCell(row, localeIdx) : "*")?.Trim(),
                    enabled = enabledIdx < 0 || RemotePromptManager.ParseBool(RemotePromptManager.GetCell(row, enabledIdx)),
                    version = versionIdx >= 0 ? RemotePromptManager.GetCell(row, versionIdx) : "0",
                    scenarioName = nameIdx >= 0 ? RemotePromptManager.GetCell(row, nameIdx) : "",
                    scenarioContext = contextIdx >= 0 ? RemotePromptManager.GetCell(row, contextIdx) : "",
                    topicD2D = d2dIdx >= 0 ? RemotePromptManager.GetCell(row, d2dIdx) : "",
                    topicD2P = d2pIdx >= 0 ? RemotePromptManager.GetCell(row, d2pIdx) : "",
                    ragTags = ragTagsIdx >= 0 ? RemotePromptManager.GetCell(row, ragTagsIdx) : "",
                    durationMinutes = ParsePositiveInt(RemotePromptManager.GetCell(row, durationMinutesIdx)),
                    theme = themeIdx >= 0 ? RemotePromptManager.GetCell(row, themeIdx) : "",
                    termsJson = termsIdx >= 0 ? RemotePromptManager.GetCell(row, termsIdx) : "",
                    coreFactsJson = coreFactsIdx >= 0 ? RemotePromptManager.GetCell(row, coreFactsIdx) : "",
                    expectedQuestionsJson = expectedQuestionsIdx >= 0 ? RemotePromptManager.GetCell(row, expectedQuestionsIdx) : "",
                    evaluationFocusJson = evaluationFocusIdx >= 0 ? RemotePromptManager.GetCell(row, evaluationFocusIdx) : "",
                    casesJson = casesJsonIdx >= 0 ? RemotePromptManager.GetCell(row, casesJsonIdx) : "",
                    voiceWelcomeD2D = voiceWelcomeD2DIdx >= 0 ? RemotePromptManager.GetCell(row, voiceWelcomeD2DIdx) : "nova",
                    voiceWelcomeD2P = voiceWelcomeD2PIdx >= 0 ? RemotePromptManager.GetCell(row, voiceWelcomeD2PIdx) : "onyx",
                    voiceRealtimeD2D = voiceRealtimeD2DIdx >= 0 ? RemotePromptManager.GetCell(row, voiceRealtimeD2DIdx) : "shimmer",
                    voiceRealtimeD2P = voiceRealtimeD2PIdx >= 0 ? RemotePromptManager.GetCell(row, voiceRealtimeD2PIdx) : "echo",
                    voiceEvalFemale = voiceEvalFemaleIdx >= 0 ? RemotePromptManager.GetCell(row, voiceEvalFemaleIdx) : "nova",
                    voiceEvalMale = voiceEvalMaleIdx >= 0 ? RemotePromptManager.GetCell(row, voiceEvalMaleIdx) : "onyx"
                };

                if (!byScenarioId.TryGetValue(sr.scenarioId, out var list))
                {
                    list = new List<ScenarioRow>();
                    byScenarioId[sr.scenarioId] = list;
                }
                list.Add(sr);
            }

            return byScenarioId;
        }

        private static Dictionary<string, ScenarioRow> SelectBestByScenarioId(Dictionary<string, List<ScenarioRow>> parsed, string desiredLocale)
        {
            var selected = new Dictionary<string, ScenarioRow>(StringComparer.OrdinalIgnoreCase);
            string loc = string.IsNullOrWhiteSpace(desiredLocale) ? "*" : desiredLocale.Trim();

            foreach (var kvp in parsed)
            {
                var list = kvp.Value;
                if (list == null || list.Count == 0) continue;

                ScenarioRow best = null;
                foreach (var row in list)
                {
                    if (row == null || !row.enabled) continue;

                    bool exact = string.Equals(row.locale ?? "*", loc, StringComparison.OrdinalIgnoreCase);
                    bool wildcard = string.IsNullOrWhiteSpace(row.locale) || string.Equals(row.locale, "*", StringComparison.OrdinalIgnoreCase);
                    int rowScore = exact ? 2 : wildcard ? 1 : 0;
                    if (rowScore == 0) continue;

                    if (best == null)
                    {
                        best = row;
                        continue;
                    }

                    int bestScore = string.Equals(best.locale ?? "*", loc, StringComparison.OrdinalIgnoreCase)
                        ? 2
                        : (string.IsNullOrWhiteSpace(best.locale) || string.Equals(best.locale, "*", StringComparison.OrdinalIgnoreCase) ? 1 : 0);

                    if (rowScore > bestScore)
                    {
                        best = row;
                        continue;
                    }

                    if (rowScore == bestScore && RemotePromptManager.CompareVersion(row.version, best.version) > 0)
                        best = row;
                }

                if (best != null)
                    selected[kvp.Key] = best;
            }

            return selected;
        }

        private static int ParsePositiveInt(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0
                ? value
                : 0;
        }

        private static int IndexOfAny(string[] header, params string[] names)
        {
            if (header == null || names == null) return -1;
            for (int i = 0; i < header.Length; i++)
            {
                var h = header[i]?.Trim();
                if (string.IsNullOrEmpty(h)) continue;
                for (int n = 0; n < names.Length; n++)
                {
                    if (string.Equals(h, names[n], StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            return -1;
        }
    }
}
