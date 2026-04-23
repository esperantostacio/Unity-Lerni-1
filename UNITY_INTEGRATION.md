# Unity Integration Guide

## Quick Start: Add Cloud RAG to Medical Exam Evaluation

### Step 1: Add CloudRetrievalService Component

1. Open Unity project at `/Users/k1bfs/Downloads/lerini3`
2. Locate your `MedicalExamManager` GameObject in the scene
3. Add Component → Search for "Cloud Retrieval Service"
4. In the Inspector, set:
   - **API Base URL**: `https://your-app.vercel.app` (replace with your actual Vercel URL)
   - **Timeout Seconds**: `5`
   - **Default Top K**: `3`
   - **Default Threshold**: `0.5`
   - **Enable Logging**: ✓ (for debugging)

### Step 2: Modify MedicalExamManager.cs

Find the evaluation orchestration method and inject retrieval before GPT evaluation.

#### Location: `Assets/Scripts/MedicalExamManager.cs`

**Around line 4390** in the `WhisperThenEvaluate()` or `GenerateEvaluationFromTranscript()` method:

```csharp
// BEFORE (existing code):
private async void GenerateEvaluationFromTranscript(string fullTranscriptForGpt)
{
    Debug.Log($"[Evaluation] Sending transcript to GPT for evaluation...");
    
    // Existing evaluation call
    evaluationDisplayUI.ParseAndDisplayConversationAndRealtimeFeedback(fullTranscriptForGpt);
}
```

```csharp
// AFTER (with RAG injection):
private async void GenerateEvaluationFromTranscript(string fullTranscriptForGpt)
{
    Debug.Log($"[Evaluation] Sending transcript to GPT for evaluation...");
    
    // NEW: Retrieve relevant knowledge from uploaded PDFs
    var retrievalService = GetComponent<CloudRetrievalService>();
    if (retrievalService != null)
    {
        try
        {
            // Build query from scenario context + partial transcript
            string scenarioContext = GetScenarioContextString(); // Use existing method if available
            string transcriptPreview = fullTranscriptForGpt.Substring(0, Math.Min(300, fullTranscriptForGpt.Length));
            string query = $"{scenarioContext} {transcriptPreview}";
            
            Debug.Log($"[Evaluation] Retrieving relevant knowledge for scenario...");
            
            // Retrieve top 3 most relevant chunks with 0.5 similarity threshold
            var retrievalResponse = await retrievalService.RetrieveContextAsync(query, topK: 3, threshold: 0.5f);
            
            if (retrievalResponse != null && retrievalResponse.count > 0)
            {
                // Format retrieved knowledge for prompt injection
                string ragContext = retrievalService.FormatContextForPrompt(retrievalResponse, includeCitations: true);
                
                // Prepend knowledge to transcript
                fullTranscriptForGpt = ragContext + "\n\n[STUDENT CONVERSATION TRANSCRIPT]\n" + fullTranscriptForGpt;
                
                Debug.Log($"[Evaluation] Injected {retrievalResponse.count} knowledge chunks from guidelines");
                Debug.Log($"[Evaluation] Sources: {retrievalService.GetSourcesSummary(retrievalResponse)}");
            }
            else
            {
                Debug.Log("[Evaluation] No relevant knowledge found, proceeding with baseline evaluation");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Evaluation] RAG retrieval failed, continuing without context: {ex.Message}");
            // Continue with evaluation even if retrieval fails
        }
    }
    else
    {
        Debug.LogWarning("[Evaluation] CloudRetrievalService not found on GameObject");
    }
    
    // Existing evaluation call (now with enriched context if retrieval succeeded)
    evaluationDisplayUI.ParseAndDisplayConversationAndRealtimeFeedback(fullTranscriptForGpt);
}
```

### Step 3: Optional - Cache Scenario-Specific Context

For better performance, retrieve and cache scenario-specific knowledge at exam start:

```csharp
// Add field to MedicalExamManager class
private string cachedScenarioContext = null;

// In your exam initialization method (e.g., StartExam, OnScenarioLoaded):
private async void OnScenarioLoaded(string scenarioId)
{
    // Existing scenario setup...
    
    // NEW: Pre-fetch scenario guidelines
    var retrievalService = GetComponent<CloudRetrievalService>();
    if (retrievalService != null)
    {
        string scenarioQuery = $"Medical exam guidelines for {scenarioId} scenario";
        var response = await retrievalService.RetrieveContextAsync(scenarioQuery, topK: 5, threshold: 0.6f);
        
        if (response != null && response.count > 0)
        {
            cachedScenarioContext = retrievalService.FormatContextForPrompt(response, includeCitations: false);
            Debug.Log($"[Scenario] Cached {response.count} guideline chunks for {scenarioId}");
        }
    }
}

// Then use cached context in evaluation:
private async void GenerateEvaluationFromTranscript(string fullTranscriptForGpt)
{
    // Prepend cached scenario guidelines
    if (!string.IsNullOrEmpty(cachedScenarioContext))
    {
        fullTranscriptForGpt = cachedScenarioContext + "\n\n" + fullTranscriptForGpt;
        Debug.Log("[Evaluation] Using cached scenario guidelines");
    }
    
    // Continue with evaluation...
}
```

### Step 4: Test the Integration

1. **Upload PDFs via web interface**:
   - Deploy your Next.js app to Vercel
   - Go to `/learning` page
   - Upload medical exam guidelines, scenario protocols, etc.
   - Wait for status "Ready"

2. **Configure Unity**:
   - Set correct API Base URL in CloudRetrievalService component
   - Ensure internet connectivity

3. **Run medical exam in Unity**:
   - Start a VR exam session
   - Complete conversation with virtual patient
   - Wait for evaluation

4. **Verify RAG is working**:
   - Check Unity Console for logs:
     - `[CloudRetrieval] Successfully retrieved X chunks`
     - `[Evaluation] Injected X knowledge chunks from guidelines`
   - Compare evaluation scores before/after uploading relevant PDFs
   - Scores should be more accurate when relevant guidelines are available

### Step 5: Handle Realtime Conversation (Optional)

If you want to use RAG during the **realtime conversation** (not just evaluation):

**Location: `com.convai.openai/Runtime/Scripts/RealtimeConversationManager.cs`**

```csharp
// Before sending a critical turn to the realtime API:
private async Task<string> EnrichPromptWithGuidelines(string userPrompt)
{
    var retrievalService = FindObjectOfType<CloudRetrievalService>();
    if (retrievalService != null)
    {
        var response = await retrievalService.RetrieveContextAsync(userPrompt, topK: 1, threshold: 0.7f);
        if (response != null && response.count > 0)
        {
            string context = retrievalService.FormatContextForPrompt(response, includeCitations: false);
            return context + "\n\n" + userPrompt;
        }
    }
    return userPrompt;
}
```

**⚠️ Warning**: Be careful with realtime latency. Only use this for:
- High-value turns (e.g., when patient asks about critical procedure)
- With strict timeout (< 200ms)
- With fallback to proceed without context

### Troubleshooting

| Issue | Solution |
|-------|----------|
| "API Base URL not configured" error | Set the API URL in Unity Inspector |
| No logs appearing | Enable "Enable Logging" in CloudRetrievalService |
| Request timeout | Increase "Timeout Seconds" or check network |
| Empty retrieval results | Lower threshold (try 0.3), upload more relevant PDFs |
| Evaluation worse with RAG | PDFs may contain conflicting info - review uploaded content |

### Next Steps

- Upload domain-specific medical guidelines to `/learning` page
- Test with multiple scenarios to verify context relevance
- Monitor evaluation quality improvements
- Adjust `topK` and `threshold` for optimal balance
- Consider caching strategy for frequently used scenarios
