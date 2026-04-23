# RAG Integration with Your Medical Exam App

## Step-by-Step Integration Guide

This guide shows you **exactly** how to integrate the RAG system with your existing `MedicalExamManager.cs` and OpenAI Realtime API.

---

## Step 1: Add Components to Your Scene (2 minutes)

### A. Add KnowledgeRetriever

1. In your exam scene, create an empty GameObject: **`RAG_KnowledgeRetriever`**
2. Add component: **`KnowledgeRetriever.cs`**
3. In the Inspector:
   - ✅ Check "Auto Initialize With Sample Data"
   - Leave other settings as default

### B. Add RAGIntegrationHelper

1. Find your **MedicalExamManager** GameObject in the scene
2. Add component: **`RAGIntegrationHelper.cs`**
3. In the Inspector:
   - ✅ Check "Enable RAG"
   - Top K Chunks: **8** (default)
   - ✅ Check "Add Reference Instructions"
   - ✅ Check "Log Retrieved Knowledge" (for debugging)

**Done!** The components are now in your scene.

---

## Step 2: Modify Your MedicalExamManager.cs (10 minutes)

You need to modify the `StartConversation()` method to use RAG.

### Find This Code (Around Line 149-200):

```csharp
public void StartConversation()
{
    // ... existing code ...
    
    if (useOpenAIRealtime && realtimeClient != null)
    {
        Debug.Log("[MedicalExamManager] Starting OpenAI Realtime Session...");
        
        string realtimePrompt = GenerateSystemPrompt();
        if (string.IsNullOrWhiteSpace(realtimePrompt))
        {
            realtimePrompt = scenarioPrompt;
        }
        StartRealtimeSession(realtimePrompt);  // ← Current code
        return;
    }
    
    // ... rest of code ...
}
```

### Replace With This:

```csharp
public void StartConversation()
{
    // ... existing code stays the same until ...
    
    if (useOpenAIRealtime && realtimeClient != null)
    {
        Debug.Log("[MedicalExamManager] Starting OpenAI Realtime Session...");
        
        string realtimePrompt = GenerateSystemPrompt();
        if (string.IsNullOrWhiteSpace(realtimePrompt))
        {
            realtimePrompt = scenarioPrompt;
        }
        
        // === RAG INTEGRATION ===
        StartCoroutine(StartRealtimeSessionWithRAG(realtimePrompt));
        return;
        // === END RAG ===
    }
    
    // ... rest of code stays the same ...
}
```

### Add This New Method to MedicalExamManager.cs:

```csharp
using MedicalExam.RAG;  // Add this at the top of the file with other using statements

// ... inside your MedicalExamManager class ...

[Header("RAG Integration")]
[SerializeField] private RAGIntegrationHelper ragHelper;

/// <summary>
/// Starts the Realtime session with RAG-enhanced system prompt.
/// </summary>
private IEnumerator StartRealtimeSessionWithRAG(string baseSystemPrompt)
{
    // Check if RAG is available
    if (ragHelper == null || !ragHelper.IsReady)
    {
        Debug.LogWarning("[MedicalExamManager] RAG not ready, starting without enhancement");
        StartRealtimeSession(baseSystemPrompt);
        yield break;
    }

    // Get scenario tags for knowledge retrieval
    List<string> scenarioTags = GetCurrentScenarioTags();
    
    Debug.Log($"[MedicalExamManager] Retrieving medical knowledge for tags: {string.Join(", ", scenarioTags)}");

    bool promptReady = false;
    string enhancedPrompt = baseSystemPrompt;

    // Enhance prompt with medical knowledge from RAG
    ragHelper.EnhanceSystemPrompt(
        baseSystemPrompt,
        scenarioTags,
        onEnhancedPromptReady: (enhanced) =>
        {
            enhancedPrompt = enhanced;
            promptReady = true;
            Debug.Log($"[MedicalExamManager] System prompt enhanced with RAG (length: {enhanced.Length})");
        },
        onError: (error) =>
        {
            Debug.LogWarning($"[MedicalExamManager] RAG enhancement failed: {error}, using base prompt");
            promptReady = true;
        }
    );

    // Wait for enhancement to complete
    yield return new WaitUntil(() => promptReady);

    // Start session with enhanced prompt
    StartRealtimeSession(enhancedPrompt);
}

/// <summary>
/// Gets scenario tags for the current exam scenario.
/// Customize this based on your scenario structure.
/// </summary>
private List<string> GetCurrentScenarioTags()
{
    var tags = new List<string>();

    // Option 1: If your MedicalExamScenario has tags field
    if (currentScenario != null && currentScenario.scenarioTags != null)
    {
        tags.AddRange(currentScenario.scenarioTags);
    }

    // Option 2: Derive from role type
    if (ragHelper != null)
    {
        tags.AddRange(ragHelper.GetScenarioTagsFromRole(selectedRole));
    }

    // Option 3: Parse from scenario name/description
    if (currentScenario != null)
    {
        string name = currentScenario.scenarioName?.ToLower() ?? "";
        
        if (name.Contains("stroke")) tags.Add("stroke");
        if (name.Contains("cardiac") || name.Contains("heart")) tags.Add("cardiology");
        if (name.Contains("back") || name.Contains("pain")) tags.Add("back_pain");
        if (name.Contains("emergency")) tags.Add("emergency_medicine");
        if (name.Contains("trauma")) tags.Add("trauma");
        if (name.Contains("aphasia") || name.Contains("speech")) tags.Add("speech_pathology");
        if (name.Contains("anatomy")) tags.Add("anatomy_drill");
    }

    // Fallback: general medical
    if (tags.Count == 0)
    {
        tags.Add("general_medical");
    }

    return tags;
}
```

---

## Step 3: Assign Reference in Inspector (1 minute)

1. Select your **MedicalExamManager** GameObject
2. In the Inspector, find the new **RAG Integration** section
3. Drag the **RAGIntegrationHelper** component (from same GameObject) into the **Rag Helper** field

**Done!** RAG is now integrated.

---

## Step 4: Test It (5 minutes)

### A. Check Console Logs

Play the scene and start an exam. You should see logs like:

```
[KnowledgeRetriever] Loaded existing knowledge base
[KnowledgeRetriever] Ready! Knowledge base has 63 chunks
[RAGIntegrationHelper] Ready! Knowledge base has 63 chunks loaded
[MedicalExamManager] Retrieving medical knowledge for tags: stroke, emergency_medicine, neuro
[RAGIntegrationHelper] Retrieved 8 knowledge chunks:
  - [Neurology] Stroke Recognition - FAST Test (Relevance: 98.2%)
  - [Emergency Medicine] Stroke Timeline (Relevance: 95.1%)
  - ...
[MedicalExamManager] System prompt enhanced with RAG (length: 2847)
[OpenAIRealtimeClient] Starting Realtime Session...
```

### B. Check AI Responses

Ask the AI questions about medical topics. It should now reference the knowledge from your books:

**Without RAG:**
```
Student: "What is the FAST test?"
AI: "FAST is a test for stroke. It stands for Face, Arms, Speech, Time."
```

**With RAG:**
```
Student: "What is the FAST test?"
AI: "The FAST test is a critical stroke assessment tool. Let me explain each component:

F - Face: Ask the patient to smile and check for asymmetry or drooping
A - Arm: Have them raise both arms and check for drift or weakness
S - Speech: Ask them to repeat a phrase like 'The sky is blue' and listen for slurring
T - Time: Note the exact time symptoms started - this is critical because every minute of delay means approximately 190,000 neurons die from brain ischemia.

If ANY component is positive, emergency services should be called immediately. The door-to-needle time target is less than 60 minutes for optimal outcomes."
```

**Notice:** The AI now provides **specific clinical details** from your medical knowledge!

---

## Step 5: Add Your Medical Books (Later)

Once RAG is working with sample data:

1. Format your medical books using [README_DATA_FORMAT.md](../Resources/MedicalKnowledge/README_DATA_FORMAT.md)
2. Place them in `Assets/Resources/MedicalKnowledge/your_book.txt`
3. Run this code to load them:

```csharp
// In Unity Editor or on first run:
var retriever = KnowledgeRetriever.Instance;
retriever.LoadFromFile("Assets/Resources/MedicalKnowledge/your_cardiology_book.txt");
retriever.LoadFromFile("Assets/Resources/MedicalKnowledge/your_neurology_book.txt");
// etc...

// The database will be saved and loaded automatically next time
```

---

## Complete Code Example

### File: MedicalExamManager.cs (Modified sections)

```csharp
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MedicalExam.RAG;  // Add this

namespace MedicalExam
{
    public class MedicalExamManager : MonoBehaviour
    {
        // ... existing fields ...

        [Header("RAG Integration")]
        [SerializeField] private RAGIntegrationHelper ragHelper;

        // ... existing code ...

        public void StartConversation()
        {
            // ... existing code until Realtime branch ...

            if (useOpenAIRealtime && realtimeClient != null)
            {
                Debug.Log("[MedicalExamManager] Starting OpenAI Realtime Session...");
                if (startConversationPanel != null) startConversationPanel.SetActive(true);
                
                EnsureAutoMicSegmentsRunning(reason: "StartConversation-Realtime");
                
                string realtimePrompt = GenerateSystemPrompt();
                if (string.IsNullOrWhiteSpace(realtimePrompt))
                {
                    realtimePrompt = scenarioPrompt;
                }
                
                // RAG INTEGRATION: Use RAG-enhanced prompt
                StartCoroutine(StartRealtimeSessionWithRAG(realtimePrompt));
                return;
            }

            // ... rest of existing code ...
        }

        private IEnumerator StartRealtimeSessionWithRAG(string baseSystemPrompt)
        {
            if (ragHelper == null || !ragHelper.IsReady)
            {
                Debug.LogWarning("[MedicalExamManager] RAG not ready, starting without enhancement");
                StartRealtimeSession(baseSystemPrompt);
                yield break;
            }

            List<string> scenarioTags = GetCurrentScenarioTags();
            Debug.Log($"[MedicalExamManager] Retrieving knowledge for: {string.Join(", ", scenarioTags)}");

            bool promptReady = false;
            string enhancedPrompt = baseSystemPrompt;

            ragHelper.EnhanceSystemPrompt(
                baseSystemPrompt,
                scenarioTags,
                onEnhancedPromptReady: (enhanced) =>
                {
                    enhancedPrompt = enhanced;
                    promptReady = true;
                },
                onError: (error) =>
                {
                    Debug.LogWarning($"[MedicalExamManager] RAG error: {error}");
                    promptReady = true;
                }
            );

            yield return new WaitUntil(() => promptReady);
            StartRealtimeSession(enhancedPrompt);
        }

        private List<string> GetCurrentScenarioTags()
        {
            var tags = new List<string>();

            // Get from scenario if available
            if (currentScenario?.scenarioTags != null)
                tags.AddRange(currentScenario.scenarioTags);

            // Get from role type
            if (ragHelper != null)
                tags.AddRange(ragHelper.GetScenarioTagsFromRole(selectedRole));

            // Parse from scenario name
            if (currentScenario != null)
            {
                string name = currentScenario.scenarioName?.ToLower() ?? "";
                if (name.Contains("stroke")) tags.Add("stroke");
                if (name.Contains("cardiac")) tags.Add("cardiology");
                if (name.Contains("back") || name.Contains("pain")) tags.Add("back_pain");
                if (name.Contains("emergency")) tags.Add("emergency_medicine");
            }

            if (tags.Count == 0)
                tags.Add("general_medical");

            return tags;
        }
    }
}
```

---

## Troubleshooting

### "RAG not ready" Warning
- **Cause:** Knowledge base not initialized yet
- **Fix:** Wait a few seconds after scene load, or check console for initialization errors

### "No knowledge retrieved"
- **Cause:** Scenario tags don't match any knowledge chunks
- **Fix:** 
  1. Check what tags are being used: Look for log `"Retrieving knowledge for: ..."`
  2. Verify your sample data has matching tags
  3. Try generic tags like `"general_medical"` or `"anatomy"`

### System prompt too long
- **Cause:** Too many knowledge chunks retrieved
- **Fix:** Lower `topKChunks` in RAGIntegrationHelper (try 5 instead of 8)

### AI doesn't reference the knowledge
- **Cause:** `addReferenceInstructions` is disabled
- **Fix:** Enable it in RAGIntegrationHelper inspector

---

## Next Steps

1. ✅ Test with sample medical knowledge
2. ⏳ Prepare your medical books ([see format guide](../Resources/MedicalKnowledge/README_DATA_FORMAT.md))
3. ⏳ Load your books into the knowledge base
4. ⏳ Tag your exam scenarios properly
5. ⏳ Monitor AI responses and adjust top K / threshold as needed

---

## Advanced: Mid-Conversation Knowledge Retrieval

If you want to retrieve additional knowledge during the conversation (e.g., when student asks a specific question):

```csharp
// In your user input handler:
public void OnUserAskedQuestion(string question)
{
    List<string> tags = GetCurrentScenarioTags();
    
    ragHelper.RetrieveForQuery(
        question,
        tags,
        onContextReady: (context) =>
        {
            if (!string.IsNullOrEmpty(context))
            {
                // Option 1: Send as hidden user message
                realtimeClient.SendText($"[Internal Context]: {context}");
                
                // Option 2: Update session with additional context
                // (requires modifying OpenAIRealtimeClient to support session updates)
            }
        }
    );
}
```

---

## Summary

✅ **What You Did:**
1. Added `KnowledgeRetriever` to scene (handles vector DB)
2. Added `RAGIntegrationHelper` to MedicalExamManager (handles integration)
3. Modified `StartConversation()` to use RAG enhancement
4. Added `GetCurrentScenarioTags()` to map scenarios to knowledge

✅ **What Happens Now:**
1. Exam starts → RAG retrieves relevant medical knowledge
2. Knowledge formatted and injected into system prompt
3. OpenAI Realtime API receives enhanced prompt
4. AI responds using your medical books! 🎉

**Total Integration Time:** ~15-20 minutes

**Questions?** Check the other docs or run the RAGDemo to test retrieval independently.
