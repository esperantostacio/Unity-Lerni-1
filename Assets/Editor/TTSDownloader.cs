using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Editor window that generates TTS audio files using the OpenAI TTS API (alloy voice)
/// and saves them to Assets/Voices/.
/// </summary>
public class TTSDownloader : EditorWindow
{
    private const string TTS_ENDPOINT = "https://api.openai.com/v1/audio/speech";
    private const string OUTPUT_FOLDER = "Assets/Voices";
    private const int TEXT_FIELD_COUNT = 6;

    [SerializeField] private string apiKey = "";
    [SerializeField] private string voice = "alloy";
    [SerializeField] private string model = "tts-1";
    [SerializeField] private AudioFormat format = AudioFormat.mp3;
    [SerializeField] private float speed = 1f;
    [SerializeField] private string[] texts = new string[TEXT_FIELD_COUNT];
    [SerializeField] private string[] fileNames = new string[TEXT_FIELD_COUNT];

    private Vector2 _scrollPos;
    private bool _isGenerating;
    private int _currentIndex;
    private string _statusMessage = "";

    private enum AudioFormat { mp3, wav, opus, aac, flac }

    [MenuItem("Tools/OpenAI TTS Downloader")]
    public static void ShowWindow()
    {
        var win = GetWindow<TTSDownloader>("TTS Downloader");
        win.minSize = new Vector2(450, 500);
    }

    private void OnEnable()
    {
        // Initialize arrays if needed
        if (texts == null || texts.Length != TEXT_FIELD_COUNT)
            texts = new string[TEXT_FIELD_COUNT];
        if (fileNames == null || fileNames.Length != TEXT_FIELD_COUNT)
            fileNames = new string[TEXT_FIELD_COUNT];

        for (int i = 0; i < TEXT_FIELD_COUNT; i++)
        {
            if (string.IsNullOrEmpty(fileNames[i]))
                fileNames[i] = $"voice_{i + 1}";
        }
    }

    private void OnGUI()
    {
        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        EditorGUILayout.LabelField("OpenAI TTS Voice Downloader", EditorStyles.boldLabel);
        EditorGUILayout.Space(5);

        // --- Settings ---
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        apiKey = EditorGUILayout.PasswordField("API Key", apiKey);
        voice = EditorGUILayout.TextField("Voice", voice);
        model = EditorGUILayout.TextField("Model", model);
        format = (AudioFormat)EditorGUILayout.EnumPopup("Format", format);
        speed = EditorGUILayout.Slider("Speed", speed, 0.25f, 4f);

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField($"Output Folder: {OUTPUT_FOLDER}/", EditorStyles.miniLabel);

        EditorGUILayout.Space(10);

        // --- Text fields ---
        EditorGUILayout.LabelField("Text Entries", EditorStyles.boldLabel);
        for (int i = 0; i < TEXT_FIELD_COUNT; i++)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Entry {i + 1}", EditorStyles.miniBoldLabel);
            fileNames[i] = EditorGUILayout.TextField("File Name", fileNames[i]);
            EditorGUILayout.LabelField("Text:");
            texts[i] = EditorGUILayout.TextArea(texts[i], GUILayout.MinHeight(40));
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        EditorGUILayout.Space(10);

        // --- Buttons ---
        GUI.enabled = !_isGenerating;

        if (GUILayout.Button("Generate All", GUILayout.Height(35)))
        {
            GenerateAll();
        }

        EditorGUILayout.BeginHorizontal();
        for (int i = 0; i < TEXT_FIELD_COUNT; i++)
        {
            int idx = i; // capture
            if (GUILayout.Button($"#{i + 1}"))
            {
                GenerateSingle(idx);
            }
        }
        EditorGUILayout.EndHorizontal();

        GUI.enabled = true;

        // --- Status ---
        if (!string.IsNullOrEmpty(_statusMessage))
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.HelpBox(_statusMessage, MessageType.Info);
        }

        EditorGUILayout.EndScrollView();
    }

    private void GenerateAll()
    {
        if (!ValidateApiKey()) return;

        // Queue all non-empty entries
        _currentIndex = 0;
        _isGenerating = true;
        GenerateNext();
    }

    private void GenerateNext()
    {
        // Skip empty entries
        while (_currentIndex < TEXT_FIELD_COUNT && string.IsNullOrWhiteSpace(texts[_currentIndex]))
        {
            _currentIndex++;
        }

        if (_currentIndex >= TEXT_FIELD_COUNT)
        {
            _isGenerating = false;
            _statusMessage = "All done!";
            AssetDatabase.Refresh();
            Repaint();
            return;
        }

        GenerateSingle(_currentIndex, isPartOfBatch: true);
    }

    private void GenerateSingle(int index, bool isPartOfBatch = false)
    {
        if (!ValidateApiKey()) return;

        string text = texts[index];
        if (string.IsNullOrWhiteSpace(text))
        {
            _statusMessage = $"Entry {index + 1} is empty, skipping.";
            Repaint();
            return;
        }

        string fileName = string.IsNullOrWhiteSpace(fileNames[index]) ? $"voice_{index + 1}" : fileNames[index];
        string ext = format.ToString();
        string outputPath = Path.Combine(OUTPUT_FOLDER, $"{fileName}.{ext}");

        _statusMessage = $"Generating {index + 1}/{TEXT_FIELD_COUNT}: \"{fileName}.{ext}\" ...";
        _isGenerating = true;
        Repaint();

        // Build JSON body
        string jsonBody = JsonUtility.ToJson(new TTSRequest
        {
            model = this.model,
            input = text,
            voice = this.voice,
            response_format = ext,
            speed = this.speed
        });

        var request = new UnityWebRequest(TTS_ENDPOINT, "POST");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

        var op = request.SendWebRequest();
        int capturedIndex = index;
        bool batch = isPartOfBatch;

        op.completed += _ =>
        {
            try
            {
                if (request.result != UnityWebRequest.Result.Success)
                {
                    string error = request.downloadHandler?.text ?? request.error;
                    Debug.LogError($"[TTSDownloader] Failed entry {capturedIndex + 1}: {error}");
                    _statusMessage = $"Error on entry {capturedIndex + 1}: {error}";
                }
                else
                {
                    // Ensure directory exists
                    string dir = Path.GetDirectoryName(outputPath);
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    File.WriteAllBytes(outputPath, request.downloadHandler.data);
                    Debug.Log($"[TTSDownloader] Saved: {outputPath} ({request.downloadHandler.data.Length} bytes)");
                    _statusMessage = $"Saved: {outputPath}";
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TTSDownloader] Exception: {ex.Message}");
                _statusMessage = $"Exception: {ex.Message}";
            }
            finally
            {
                request.Dispose();

                if (batch)
                {
                    _currentIndex = capturedIndex + 1;
                    EditorApplication.delayCall += GenerateNext;
                }
                else
                {
                    _isGenerating = false;
                    AssetDatabase.Refresh();
                }

                Repaint();
            }
        };
    }

    private bool ValidateApiKey()
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _statusMessage = "Please enter your OpenAI API key.";
            Repaint();
            return false;
        }
        return true;
    }

    [Serializable]
    private struct TTSRequest
    {
        public string model;
        public string input;
        public string voice;
        public string response_format;
        public float speed;
    }
}
