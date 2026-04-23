# RAG System - Visual Architecture & Flow

## System Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                         UNITY SCENE                                  │
├─────────────────────────────────────────────────────────────────────┤
│                                                                       │
│  ┌──────────────────┐         ┌──────────────────┐                 │
│  │ MedicalExamManager│◄───────►│ RAGIntegrationHelper │            │
│  └────────┬─────────┘         └────────┬─────────┘                 │
│           │                             │                            │
│           │                             │                            │
│           ├─────────────────────────────┤                            │
│           │                             │                            │
│  ┌────────▼─────────┐         ┌────────▼─────────┐                 │
│  │ OpenAIRealtimeClient │     │ KnowledgeRetriever │                │
│  └──────────────────┘         └────────┬─────────┘                 │
│           │                             │                            │
│           │                             │                            │
│           │                    ┌────────▼─────────┐                 │
│           │                    │  VectorDatabase   │                 │
│           │                    │  (Local JSON)     │                 │
│           │                    └──────────────────┘                 │
│           │                                                           │
│           │                                                           │
│  ┌────────▼────────────────────────────────────────────┐           │
│  │         OpenAI Realtime API (WebSocket)              │           │
│  │         (Enhanced with Medical Knowledge)            │           │
│  └──────────────────────────────────────────────────────┘           │
│                                                                       │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Data Flow: From Medical Book to AI Response

### Phase 1: Setup (One-time, at build/first run)

```
Medical Books (PDF/TXT)
    ↓
MedicalKnowledgeLoader.LoadFromFile()
    ↓
Parse & Chunk Text
 - Split into 300-500 token sections
 - Extract metadata (category, tags, clinical context)
    ↓
List<KnowledgeChunk>
    ↓
EmbeddingService.GetEmbeddingsBatch()
 - Send to OpenAI Embeddings API
 - OR use mock mode for testing
    ↓
float[] embeddings (1536 dimensions each)
    ↓
VectorDatabase.AddChunks()
 - Store chunks + embeddings
    ↓
VectorDatabase.Save()
 - Persist to disk:
   Application.persistentDataPath/RAG/medical_knowledge_db.json
    ↓
✅ Knowledge Base Ready
```

### Phase 2: Exam Startup (Every exam session)

```
Student clicks "Start Conversation"
    ↓
MedicalExamManager.StartConversation()
    ↓
Calls: StartRealtimeSessionWithRAG(baseSystemPrompt)
    ↓
Get scenario tags:
 - Example: ["stroke", "emergency_medicine", "neuro"]
    ↓
RAGIntegrationHelper.EnhanceSystemPrompt()
    ↓
KnowledgeRetriever.RetrieveByScenario(tags)
    ↓
VectorDatabase.RetrieveByScenario()
 - Filter chunks matching tags
 - Return top 8 most relevant
 - Retrieval time: ~5-10ms ⚡
    ↓
KnowledgeRetrievalResult:
 [
   {category: "Neurology", title: "Stroke Recognition", relevance: 98%},
   {category: "Emergency", title: "Stroke Timeline", relevance: 95%},
   {category: "Neurology", title: "Common Symptoms", relevance: 92%},
   ...
 ]
    ↓
Format as text context:
"MEDICAL KNOWLEDGE CONTEXT:

[Neurology] Stroke Recognition - FAST Test
Relevance: 98.2%
F (Face): Ask patient to smile, check for asymmetry...
[Full content of chunk]

[Emergency Medicine] Stroke Timeline
Relevance: 95.1%
Time = Brain: Every minute ~190,000 neurons die...
[Full content of chunk]

..."
    ↓
Combine with base system prompt:
enhancedPrompt = basePrompt + "\n\n" + medicalKnowledge + "\n\n" + instructions
    ↓
MedicalExamManager.StartRealtimeSession(enhancedPrompt)
    ↓
OpenAIRealtimeClient connects to WebSocket
 - Sends session.update with enhanced system prompt
    ↓
OpenAI Realtime API
 - AI model loads system prompt with medical knowledge
 - Now has access to your books! ✅
    ↓
Conversation begins with knowledge-aware AI
```

### Phase 3: During Conversation

```
Student speaks: "What is the FAST test?"
    ↓
Voice Activity Detection (OpenAI Realtime)
    ↓
Transcription + AI Processing
    ↓
AI references medical knowledge from system prompt:
 - Sees "FAST Test" in provided context
 - Generates detailed response using that information
    ↓
AI speaks: "According to the medical knowledge, the FAST test..."
    ↓
✅ Student receives accurate, book-based answer
```

---

## Message Flow Diagram

```
┌──────────┐                ┌──────────────┐               ┌──────────────┐
│ Student  │                │ MedicalExam  │               │     RAG      │
│          │                │   Manager    │               │   System     │
└────┬─────┘                └──────┬───────┘               └──────┬───────┘
     │                             │                              │
     │ Click "Start"               │                              │
     ├────────────────────────────►│                              │
     │                             │                              │
     │                             │ Get scenario tags            │
     │                             │ ["stroke", "emergency"]      │
     │                             │                              │
     │                             │ Retrieve knowledge           │
     │                             ├─────────────────────────────►│
     │                             │                              │
     │                             │                              │ Search DB
     │                             │                              │ (5-10ms)
     │                             │                              │
     │                             │ Return 8 chunks              │
     │                             │◄─────────────────────────────┤
     │                             │                              │
     │                             │ Format as context            │
     │                             │                              │
     │                             │ Combine with base prompt     │
     │                             │                              │
     │                             │ Enhanced Prompt              │
     │                             │                              │
     │                       ┌─────▼─────┐                        │
     │                       │  OpenAI   │                        │
     │                       │ Realtime  │                        │
     │                       │    API    │                        │
     │                       └─────┬─────┘                        │
     │                             │                              │
     │                             │ session.update               │
     │                             │ (with medical knowledge)     │
     │                             │                              │
     │ AI speaks with knowledge    │                              │
     │◄────────────────────────────┤                              │
     │                             │                              │
```

---

## File Structure After Integration

```
Assets/
├── Scripts/
│   ├── MedicalExamManager.cs (MODIFIED ✏️)
│   │   └── Added: StartRealtimeSessionWithRAG()
│   │   └── Added: GetCurrentScenarioTags()
│   │   └── Added: [SerializeField] RAGIntegrationHelper ragHelper
│   │
│   ├── OpenAIRealtimeClient.cs (UNCHANGED ✅)
│   │
│   └── RAG/
│       ├── KnowledgeChunk.cs (NEW 🆕)
│       ├── EmbeddingService.cs (NEW 🆕)
│       ├── VectorDatabase.cs (NEW 🆕)
│       ├── MedicalKnowledgeLoader.cs (NEW 🆕)
│       ├── KnowledgeRetriever.cs (NEW 🆕)
│       ├── RAGIntegrationHelper.cs (NEW 🆕)
│       ├── RAGExampleIntegration.cs (NEW 🆕)
│       ├── RAGDemo.cs (NEW 🆕)
│       └── INTEGRATION_GUIDE.md (NEW 🆕)
│
└── Resources/
    └── MedicalKnowledge/
        ├── sample_medical_knowledge.txt (NEW 🆕)
        ├── your_book_1.txt (YOU ADD 📚)
        ├── your_book_2.txt (YOU ADD 📚)
        └── README_DATA_FORMAT.md (NEW 🆕)
```

---

## Integration Checklist

### ✅ Pre-Integration (System Ready)

- [x] RAG system files created
- [x] Sample medical knowledge provided
- [x] Documentation written
- [x] Example integration code ready

### 📝 Your Integration Tasks (15-20 minutes)

- [ ] Add `KnowledgeRetriever` component to scene
- [ ] Add `RAGIntegrationHelper` component to MedicalExamManager
- [ ] Add `using MedicalExam.RAG;` to MedicalExamManager.cs
- [ ] Add `[SerializeField] private RAGIntegrationHelper ragHelper;` field
- [ ] Replace `StartRealtimeSession(prompt)` call with `StartCoroutine(StartRealtimeSessionWithRAG(prompt))`
- [ ] Add `StartRealtimeSessionWithRAG()` method
- [ ] Add `GetCurrentScenarioTags()` method
- [ ] Assign RAGIntegrationHelper in Inspector
- [ ] Test with sample data
- [ ] Verify AI responses use medical knowledge

### 🎯 Post-Integration (Optional)

- [ ] Add your medical books to Resources/MedicalKnowledge/
- [ ] Process and embed your books
- [ ] Tag exam scenarios properly
- [ ] Fine-tune topK and threshold settings
- [ ] Add mid-conversation retrieval (if needed)

---

## Performance Metrics

### Startup Time
```
Scene Load
  ↓
KnowledgeRetriever.Start() (100ms)
  ↓
VectorDatabase.Load() (500-1000ms)
  ↓
Ready for use (~1-2 seconds total)
```

### Retrieval Time (Per Exam Start)
```
Get scenario tags (0ms - instant)
  ↓
Search vector database (5-10ms)
  ↓
Format context (2-5ms)
  ↓
Total: ~10-20ms (negligible) ⚡
```

### Memory Footprint
```
Knowledge Base (60 chunks): ~7 MB
Vector Database: ~2-3 MB
Total: ~10 MB
```

### API Costs
```
Embedding (one-time): ~$0.02 per book
Retrieval: $0 (all local)
OpenAI Realtime API: ~$0.10/min (unchanged)
```

---

## What Happens in Production

### Scenario: Stroke Patient Exam

**Student starts exam:**
1. System retrieves 8 chunks about stroke (10ms)
2. Formats them as context (5ms)
3. Injects into system prompt
4. Sends to OpenAI Realtime API

**AI system prompt now contains:**
```
You are a paramedic examining a stroke patient.
Stay professional and systematic.

MEDICAL KNOWLEDGE CONTEXT:

[Neurology] Stroke Recognition - FAST Test
Relevance: 98.2%
F (Face): Ask patient to smile, check for facial asymmetry...
A (Arm): Have patient raise both arms, check for drift...
S (Speech): Ask patient to repeat phrase, listen for slurring...
T (Time): Note exact symptom onset - critical for treatment window...
Time is brain: ~190,000 neurons die per minute...

[Emergency Medicine] Stroke Timeline
Relevance: 95.1%
Thrombolytic window: 0-4.5 hours from onset
Door-to-needle target: <60 minutes
Mechanical thrombectomy: 0-24 hours with imaging...

[Neurology] Common Stroke Symptoms
Relevance: 92.5%
- Unilateral weakness/hemiparesis
- Facial droop
- Speech difficulty (dysarthria/aphasia)...

IMPORTANT: Reference this medical knowledge when responding.
```

**Student asks: "What should I check first?"**

**AI responds (using the injected knowledge):**
```
"Start with the FAST test immediately:

Face - Ask the patient to smile. Look for any asymmetry or drooping 
on one side, which indicates facial nerve involvement.

Arms - Have them raise both arms straight out. Watch for any drift 
downward on one side, which suggests motor weakness.

Speech - Ask them to repeat a simple phrase. Listen for slurring 
or difficulty finding words.

Time - Document the exact time symptoms started. This is critical 
because we have a narrow treatment window - every minute, approximately 
190,000 neurons are dying. Our goal is door-to-needle time under 
60 minutes for thrombolytic therapy.

Remember, if ANY component of the FAST test is positive, this is a 
medical emergency requiring immediate transport."
```

✅ **Notice:** The AI gives specific details from YOUR medical books, not just general knowledge!

---

## Summary: How It All Works Together

1. **Medical books** → Parsed → Chunked → Embedded → Stored locally
2. **Exam starts** → Tags identified → Knowledge retrieved (~10ms)
3. **Context formatted** → Injected into system prompt
4. **OpenAI Realtime API** → Receives enhanced prompt
5. **AI responds** → Using your medical knowledge ✅

**The key insight:** The local vector database doesn't talk directly to the AI. Instead:
- RAG system **retrieves** relevant knowledge
- **Formats** it as readable text
- **Injects** it into the system prompt
- OpenAI **reads** it like any other prompt text
- AI **uses** it when generating responses

It's like giving the AI a "cheat sheet" at the start of each exam!
