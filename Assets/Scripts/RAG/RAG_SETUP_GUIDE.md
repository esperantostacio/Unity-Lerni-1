# RAG System Setup & Integration Guide

## Overview

The **RAG (Retrieval Augmented Generation) System** has been implemented to enhance your medical exam AI with access to medical knowledge. This system allows the AI to reference accurate medical content during conversations and evaluations.

## Components Created

### Core Components

1. **KnowledgeChunk.cs** - Data structure representing a chunk of medical knowledge
   - Stores content, embeddings, metadata, and scenario tags
   - Calculates similarity scores between chunks

2. **EmbeddingService.cs** - Converts text to vector embeddings
   - Uses OpenAI Embeddings API (optional mock mode for testing)
   - Supports batch embedding requests for efficiency

3. **VectorDatabase.cs** - Stores and retrieves knowledge chunks
   - Local JSON-based persistence
   - Cosine similarity search
   - Tag-based filtering

4. **MedicalKnowledgeLoader.cs** - Loads and parses medical text files
   - Automatic chunking with configurable overlap
   - Metadata extraction (category, tags, clinical context)
   - Token count estimation

5. **KnowledgeRetriever.cs** - Main interface for RAG functionality (MonoBehaviour)
   - Coordinates all components
   - Handles initialization and caching
   - Provides semantic search and scenario-based retrieval

6. **RAGDemo.cs** - Test and demonstration script
   - Shows how to use the RAG system
   - Tests semantic search, scenario retrieval, and combined queries

## Resources Created

### Medical Knowledge Base
- **Assets/Resources/MedicalKnowledge/sample_medical_knowledge.txt**
  - Sample content covering:
    - Anatomy & anatomical directions
    - Cardiology assessment
    - Neurology & stroke evaluation
    - Orthopedics & physiotherapy
    - Speech-language pathology
    - Midwifery & postpartum care
    - Emergency medicine protocols

### Documentation
- **Assets/Resources/MedicalKnowledge/README_DATA_FORMAT.md**
  - Detailed guide on how to structure medical data
  - Data cleaning best practices
  - Tagging strategy
  - Integration workflow

## How It Works

### Phase 1: Knowledge Loading
```
Medical Text File (PDF or TXT)
    ↓
MedicalKnowledgeLoader
    ↓
Parse & Chunk Content (300-500 tokens each)
    ↓
List of KnowledgeChunk objects
```

### Phase 2: Embedding
```
KnowledgeChunk Content
    ↓
EmbeddingService (using OpenAI API)
    ↓
Dense Vector Representation (1536D)
    ↓
Store in VectorDatabase
```

### Phase 3: Retrieval & Injection
```
Exam Scenario + Query Text
    ↓
KnowledgeRetriever.RetrieveKnowledge()
    ↓
Semantic Search + Scenario Tag Filtering
    ↓
Top K Most Relevant Chunks
    ↓
Format as System Prompt Context
    ↓
Inject into OpenAI Realtime API
```

## Setup Instructions

### Step 1: Add KnowledgeRetriever to Your Scene

1. In your exam scene, create an empty GameObject: **KnowledgeRetriever**
2. Attach the **KnowledgeRetriever.cs** script
3. In the inspector:
   - Enable "Auto Initialize With Sample Data"
   - Create or assign an **EmbeddingService** component
   - Set Persistent Data Path (optional - defaults to Application.persistentDataPath)

### Step 2: Configure OpenAI API Key

The KnowledgeRetriever will create an EmbeddingService automatically. If you want to use the real OpenAI API:

```csharp
// In your initialization code:
var retriever = KnowledgeRetriever.Instance;
var embeddingService = retriever.GetComponent<EmbeddingService>();
embeddingService.SetAPIKey(YOUR_OPENAI_API_KEY);
embeddingService.SetMockMode(false); // Use real API instead of mock
```

**For testing:** Leave `useLocalMockEmbeddings = true` to avoid API calls.

### Step 3: Test the RAG System

1. Create an empty GameObject: **RAG_Demo**
2. Attach **RAGDemo.cs** script
3. Enable "Run Demo On Start"
4. Play the scene and check the console for retrieval results

## Integration with Your Exam AI

### Example: Inject Knowledge into Realtime API System Prompt

```csharp
// In MedicalExamManager.cs or your exam coordinator:

public void StartExamWithKnowledgeContext(string scenarioName, List<string> scenarioTags)
{
    var retriever = KnowledgeRetriever.Instance;
    
    retriever.RetrieveByScenario(
        scenarioTags,
        topK: 10,
        onSuccess: (result) =>
        {
            // Format knowledge as context
            string knowledgeContext = retriever.FormatContextForPrompt(
                result, 
                "AVAILABLE MEDICAL KNOWLEDGE:"
            );
            
            // Build enhanced system prompt
            string systemPrompt = $@"You are a medical examination AI assistant for {scenarioName}.

{knowledgeContext}

Instructions:
- Base your responses on the provided medical knowledge
- Be clinically accurate and thorough
- Mention relevant diagnostic criteria and assessment methods
- Use appropriate medical terminology";
            
            // Pass to Realtime API
            realtimeClient.UpdateSystemPrompt(systemPrompt);
            realtimeClient.CreateResponse();
        },
        onError: (error) =>
        {
            Debug.LogError($"Failed to retrieve knowledge: {error}");
        }
    );
}
```

### Example: Query-Based Knowledge for Mid-Exam

```csharp
// During conversation, retrieve knowledge for a specific question:

public void RetrieveKnowledgeForUserQuestion(string userQuestion, List<string> scenarioTags)
{
    var retriever = KnowledgeRetriever.Instance;
    
    retriever.RetrieveKnowledge(
        userQuestion,
        scenarioTags: scenarioTags,
        topK: 5,
        onSuccess: (result) =>
        {
            string context = retriever.FormatContextForPrompt(result);
            
            // Include context in your next API call to GPT
            // This helps the AI provide more informed responses
            string enhancedPrompt = $"{context}\n\nUser question: {userQuestion}";
            
            // Send to conversation API
            gptAndWhisper.SendConversationMessage(
                enhancedPrompt,
                onResult: (response) => { /* handle response */ }
            );
        }
    );
}
```

## Data Flow for Exam Scenarios

```
Exam Start (e.g., "Stroke Patient Evaluation")
    ↓ Scenario Tags: [stroke, emergency_medicine, neuro_exam]
    ↓
KnowledgeRetriever.RetrieveByScenario()
    ↓ Matches chunks tagged with scenario
    ↓
Returns: [Stroke Recognition, FAST Test, Timeline, Emergency Protocols...]
    ↓
Format as MEDICAL_CONTEXT string
    ↓
Inject into System Prompt for Realtime API
    ↓
AI now has access to relevant medical knowledge
    ↓
Conversation stays fast (no real-time DB lookups)
    ↓
AI responses are more accurate and informed
```

## Adding Your Medical Books

### Step 1: Prepare Your Books

Follow the guidelines in **README_DATA_FORMAT.md**:
- Convert PDFs to text (if needed)
- Organize by medical specialty/chapter
- Clean formatting and structure
- Tag with relevant scenario keywords

### Step 2: Place Files

```
Assets/Resources/MedicalKnowledge/
├── sample_medical_knowledge.txt
├── your_orthopedics_book.txt
├── your_cardiology_book.txt
└── your_emergency_protocols.txt
```

### Step 3: Load Programmatically

```csharp
var retriever = KnowledgeRetriever.Instance;

// Load additional knowledge files
retriever.LoadFromFile("Assets/Resources/MedicalKnowledge/your_book.txt");

// Wait for embedding and initialization
yield return new WaitUntil(() => retriever.IsReady);

// Now use as normal
retriever.RetrieveKnowledge(...);
```

## Performance Optimization

### Caching Strategy
- Embeddings are cached locally (JSON database)
- Subsequent retrievals use pre-computed vectors (very fast)
- Only first-time initialization requires embedding API calls

### Optimization Tips

1. **Batch Processing:** Add multiple books at once
   ```csharp
   var allChunks = new List<KnowledgeChunk>();
   foreach (var filePath in bookFiles)
   {
       var chunks = MedicalKnowledgeLoader.LoadFromTextFile(filePath);
       allChunks.AddRange(chunks);
   }
   vectorDatabase.AddChunks(allChunks);
   vectorDatabase.Save();
   ```

2. **Relevance Threshold:** Adjust similarity threshold
   ```csharp
   // Only return highly relevant chunks
   retrieverResult = vectorDatabase.RetrieveSimilar(
       query,
       similarityThreshold: 0.6f  // Higher = more strict
   );
   ```

3. **Scenario Pre-caching:** Pre-load common scenarios
   ```csharp
   // Cache scenario results at app startup
   List<string> commonScenarios = new List<string> { "stroke", "cardiac" };
   foreach (var scenario in commonScenarios)
   {
       retriever.RetrieveByScenario(new List<string> { scenario });
   }
   ```

## Troubleshooting

### "Knowledge base not initialized"
- Ensure KnowledgeRetriever is in scene
- Check "Auto Initialize With Sample Data" is enabled
- Wait for `retriever.IsReady == true`

### "No chunks retrieved"
- Check scenario tags match your knowledge base
- Verify similarity threshold isn't too high (>0.7)
- Ensure chunks were embedded successfully

### "Embedding API errors"
- Set `useLocalMockEmbeddings = true` for testing
- Check OpenAI API key is valid
- Monitor API rate limits

### "Database file not persisting"
- Check Application.persistentDataPath is writable
- Verify path is not in read-only location
- Look for file system permissions issues

## API Reference

### KnowledgeRetriever.RetrieveKnowledge()
```csharp
retriever.RetrieveKnowledge(
    queryText: string,
    scenarioTags: List<string> = null,
    topK: int = 5,
    onSuccess: Action<KnowledgeRetrievalResult> = null,
    onError: Action<string> = null
);
```

### KnowledgeRetriever.RetrieveByScenario()
```csharp
retriever.RetrieveByScenario(
    scenarioTags: List<string>,
    topK: int = 10,
    onSuccess: Action<KnowledgeRetrievalResult> = null,
    onError: Action<string> = null
);
```

### KnowledgeRetriever.FormatContextForPrompt()
```csharp
string context = retriever.FormatContextForPrompt(
    retrievalResult: KnowledgeRetrievalResult,
    header: string = "MEDICAL KNOWLEDGE CONTEXT:"
);
```

## Next Steps

1. **Test with Sample Data:** Run the scene with RAGDemo enabled
2. **Prepare Your Books:** Convert and structure your medical books using the format guide
3. **Integrate with Exam Manager:** Connect RAG retrieval to your MedicalExamManager
4. **Evaluate Results:** Monitor if AI responses become more accurate and informed
5. **Iterate:** Add more medical content as needed

## Support

For issues or questions:
1. Check console logs (RAG components log detailed information)
2. Review the sample knowledge structure
3. Verify OpenAI API configuration
4. Run the RAGDemo to validate setup
