# RAG Integration - Quick Start for Your App

## Quick Summary

You now have a complete **RAG system** ready to use. Here's how it connects to your project:

### What Was Created

```
Assets/Scripts/RAG/
├── KnowledgeChunk.cs              ← Data structures
├── EmbeddingService.cs            ← Text → Vectors (OpenAI API)
├── VectorDatabase.cs              ← Store & retrieve vectors locally
├── MedicalKnowledgeLoader.cs      ← Parse medical texts
├── KnowledgeRetriever.cs          ← Main interface (MonoBehaviour)
├── RAGDemo.cs                     ← Testing & examples
├── RAG_SETUP_GUIDE.md             ← Full documentation
└── QUICK_START.md                 ← This file

Assets/Resources/MedicalKnowledge/
├── sample_medical_knowledge.txt   ← Sample medical content
└── README_DATA_FORMAT.md          ← How to prepare your books
```

## 3-Step Setup

### Step 1: Add to Your Scene (30 seconds)

In your exam scene, create a new empty GameObject:

```
GameObject → Create Empty → Name it "KnowledgeRetriever"
↓
Add Component → KnowledgeRetriever (from Assets/Scripts/RAG/)
↓
Inspector:
  - Auto Initialize With Sample Data: ✅ (checked)
  - Embedding Service: (Leave blank - auto-created)
```

**Done!** The system will auto-initialize with sample medical knowledge on play.

### Step 2: Test It (1 minute)

Create another GameObject:

```
GameObject → Create Empty → Name it "RAG_Demo"
↓
Add Component → RAGDemo (from Assets/Scripts/RAG/)
↓
Inspector:
  - Run Demo On Start: ✅ (checked)
```

Play the scene. Check Console for retrieval results showing it's working.

### Step 3: Connect to Your AI (5 minutes)

In your **MedicalExamManager.cs** (or the place you set up system prompts), add:

```csharp
using MedicalExam.RAG;  // Add this at top

// In your exam setup method:
private void SetupExamWithKnowledge(string scenarioName, List<string> scenarioTags)
{
    var retriever = KnowledgeRetriever.Instance;
    
    // Get relevant medical knowledge
    retriever.RetrieveByScenario(
        scenarioTags,
        topK: 8,
        onSuccess: (result) =>
        {
            // Format as context
            string medicalContext = retriever.FormatContextForPrompt(result);
            
            // Build enhanced system prompt
            string systemPrompt = $@"You are a medical examination AI assistant.

{medicalContext}

Be clinically accurate. Reference the provided medical knowledge in your responses.";
            
            // Send to your Realtime API (example)
            // realtimeClient.UpdateSystemPrompt(systemPrompt);
            // OR for standard GPT:
            // gptAndWhisper.ResetConversation(systemPrompt);
        }
    );
}
```

## How It Works (Simple Version)

```
1. LOAD PHASE (Done once on startup)
   Medical Books (TXT files)
   ↓
   Split into chunks (paragraphs/sections)
   ↓
   Convert each chunk to vector (using OpenAI embeddings)
   ↓
   Store in local database (saved to disk)

2. RETRIEVE PHASE (Done each exam)
   Exam Scenario Tags: [stroke, emergency, neuro]
   ↓
   Search for matching knowledge chunks
   ↓
   Return top 5-10 most relevant chunks
   ↓
   Format as readable text

3. INJECT PHASE
   Medical Context (formatted text)
   ↓
   Add to system prompt
   ↓
   Send to OpenAI Realtime API (or GPT)
   ↓
   AI now has access to medical knowledge
```

## Your Data: How to Provide Books

### When You Have Medical Books Ready

1. **Save as TEXT file** (or we convert from PDF)
   - One book = one `.txt` file
   - Organized by chapters/sections

2. **Format with metadata:**
   ```
   DOCUMENT ID: CARDIO_101
   CATEGORY: Cardiology
   SCENARIO_TAGS: cardiac, emergency, evaluation
   CLINICAL_CONTEXT: Prehospital cardiac assessment

   ---

   1. ACUTE CORONARY SYNDROME
      Definition: ...
      Symptoms: ...
   ```

3. **Place in:**
   ```
   Assets/Resources/MedicalKnowledge/your_book.txt
   ```

4. **Load in code:**
   ```csharp
   var retriever = KnowledgeRetriever.Instance;
   retriever.LoadFromFile("Assets/Resources/MedicalKnowledge/your_book.txt");
   ```

### Full Instructions

See: [README_DATA_FORMAT.md](../Resources/MedicalKnowledge/README_DATA_FORMAT.md)

## Test It Now

**Scenario:** Stroke patient evaluation by paramedic

```csharp
var retriever = KnowledgeRetriever.Instance;

yield return new WaitUntil(() => retriever.IsReady);

retriever.RetrieveByScenario(
    new List<string> { "stroke", "emergency", "paramedic" },
    topK: 5,
    onSuccess: (result) =>
    {
        Debug.Log("Retrieved knowledge for stroke scenario:");
        foreach (var chunk in result.Chunks)
        {
            Debug.Log($"- {chunk.SectionTitle}: {chunk.Content.Substring(0, 80)}...");
        }
        
        // This context is now ready to inject into system prompt
        string context = retriever.FormatContextForPrompt(result);
    }
);
```

## Full Documentation

Read these for complete details:

1. **[RAG_SETUP_GUIDE.md](./RAG_SETUP_GUIDE.md)** - Complete setup & integration
2. **[README_DATA_FORMAT.md](../Resources/MedicalKnowledge/README_DATA_FORMAT.md)** - How to prepare your books
3. **Code comments** - All classes have detailed documentation

## Key Features

✅ **Semantic Search** - "What are stroke symptoms?" matches relevant chunks
✅ **Scenario Filtering** - Tag-based retrieval (e.g., "stroke" + "emergency")
✅ **Local Persistence** - Vectors cached locally (fast, no API calls on retrieval)
✅ **Mock Mode** - Test without OpenAI API calls
✅ **Extensible** - Easy to add more medical content
✅ **Two APIs Supported** - Works with OpenAI Realtime API OR standard GPT-4

## Cost Analysis

**OpenAI Embeddings:**
- First-time: ~$0.02 per 1M tokens (cheap)
- Storage: Just JSON files on disk (free)
- Retrieval: Zero cost (local search only)

**No Ongoing API Costs** - Vector database is local!

## Troubleshooting

| Problem | Solution |
|---------|----------|
| "Not initialized" | Wait for `retriever.IsReady == true` |
| "Zero chunks retrieved" | Check scenario tags match your knowledge base |
| "Slow on first load" | Embedding first book takes time (2-5 min) - only once |
| "No data after load" | Check file path is correct and readable |

## Next Actions

1. ✅ **Test:** Run RAGDemo to verify it works
2. ⏳ **Prepare:** Format your medical books using the guide
3. ⏳ **Add:** Place books in `Assets/Resources/MedicalKnowledge/`
4. ⏳ **Integrate:** Connect `KnowledgeRetriever` to your exam flows
5. ⏳ **Monitor:** Check if AI responses improve

## Example: Complete Exam Flow

```csharp
// In your MedicalExamManager

public IEnumerator StartExamScenario(MedicalExamScenario scenario)
{
    // 1. Wait for RAG to be ready
    var retriever = KnowledgeRetriever.Instance;
    yield return new WaitUntil(() => retriever.IsReady);
    Debug.Log($"Knowledge base ready: {retriever.KnowledgeChunkCount} chunks loaded");
    
    // 2. Retrieve knowledge for this scenario
    bool retrievalComplete = false;
    KnowledgeRetrievalResult knowledgeResult = null;
    
    retriever.RetrieveByScenario(
        scenario.scenarioTags,
        topK: 8,
        onSuccess: (result) =>
        {
            knowledgeResult = result;
            retrievalComplete = true;
        }
    );
    
    yield return new WaitUntil(() => retrievalComplete);
    
    // 3. Build system prompt with knowledge
    string medicalKnowledge = retriever.FormatContextForPrompt(
        knowledgeResult,
        "RELEVANT MEDICAL KNOWLEDGE FOR THIS SCENARIO:"
    );
    
    string systemPrompt = $@"You are a medical examination AI acting as: {scenario.role}

{medicalKnowledge}

Instructions:
- Stay in character
- Be clinically accurate
- Reference provided medical knowledge when relevant
- Ask appropriate clinical assessment questions";
    
    // 4. Start conversation with enhanced prompt
    currentPhase = ExamPhase.Anamnesis;
    realtimeClient.UpdateSystemPrompt(systemPrompt);
    realtimeClient.CreateResponse();
    
    // 5. Begin exam conversation
    yield return new WaitUntil(() => conversationReady);
    // ... rest of exam flow ...
}
```

## Summary

You now have:

- ✅ RAG database system (local, fast, no ongoing costs)
- ✅ Medical knowledge loader & chunker
- ✅ Semantic search + tag filtering
- ✅ Ready to inject knowledge into your Realtime API
- ✅ Sample content to test
- ✅ Complete documentation

**Next:** When you have your medical books, place them in `Assets/Resources/MedicalKnowledge/` and load them using `KnowledgeRetriever.LoadFromFile()`.

Questions? Check the full docs in [RAG_SETUP_GUIDE.md](./RAG_SETUP_GUIDE.md)
