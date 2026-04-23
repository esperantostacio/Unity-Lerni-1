# Medical Knowledge Base - Data Preparation Guide

## Overview
This guide explains how to prepare and format medical books/PDFs for integration into the RAG (Retrieval Augmented Generation) system. The system will convert your medical content into embeddings and make it searchable and injectable into AI conversations.

---

## Phase 1: Data Collection & Format

### Option A: PDF Books (Recommended format)
When you provide medical PDF books, we will:
1. Extract text from PDFs using OCR/PDF parsing
2. Organize by chapters/sections
3. Clean and structure the content

**What we need from you:**
- Medical book PDFs (any format, any size)
- Optionally: List of chapters/sections to prioritize
- Subject matter (e.g., "Orthopedic Assessment", "Stroke Management")

### Option B: Structured Text Files
If you have medical content already digitized, format like `sample_medical_knowledge.txt`:

```
================================================================================
DOCUMENT ID: [UNIQUE_ID]
CATEGORY: [Medical Specialty]
SCENARIO_TAGS: [tag1, tag2, tag3]
CLINICAL_CONTEXT: [When to use this knowledge]

---
SECTION TITLE

1. SUBSECTION
   - Point 1
   - Point 2
   - Detailed explanation with bullets

2. ANOTHER SUBSECTION
   - Details...

================================================================================
```

### Option C: Excel/CSV Format
Prepare a spreadsheet with columns:

| Document_ID | Category | Scenario_Tags | Clinical_Context | Section_Title | Content |
|---|---|---|---|---|---|
| ORTHO_001 | Orthopedics | back_pain, musculoskeletal | Physiotherapy assessment | Lumbar Pain | Detailed content here... |
| NEURO_002 | Neurology | stroke, aphasia | Neurological exam | Stroke Timeline | Content... |

---

## Phase 2: Content Organization

### Recommended Structure by Medical Specialty

Each medical topic should include:
1. **Definition/Context**: What is this condition?
2. **Clinical Presentation**: How does it appear?
3. **Assessment Methods**: How do we evaluate it?
4. **Key Red Flags**: Critical warning signs
5. **Management Guidelines**: How to handle/treat
6. **Exam-Relevant Points**: What students should know

### Example Structure

```
DOCUMENT ID: CARDIO_101
CATEGORY: Cardiology
SCENARIO_TAGS: cardiovascular, emergency, vital_signs, paramedic
CLINICAL_CONTEXT: Used in cardiac exams and emergency scenarios

---
ACUTE CORONARY SYNDROME (ACS)

1. DEFINITION & PATHOPHYSIOLOGY
   - Spectrum of myocardial ischemia from angina to MI
   - Caused by coronary artery narrowing/occlusion
   - STEMI vs NSTEMI classification based on EKG findings

2. CLINICAL PRESENTATION (RED FLAGS)
   - Chest pain/pressure: Crushing, radiating to jaw/arm/back
   - Diaphoresis: Cold, clammy sweating
   - Dyspnea: Shortness of breath
   - Nausea/Vomiting
   - Time-sensitive: Symptoms typically >30 minutes

3. RAPID ASSESSMENT (FIRST 5 MINUTES)
   - Establish IV access
   - Continuous cardiac monitoring
   - 12-lead EKG within 10 minutes
   - Troponin, CBC, metabolic panel

4. EMERGENCY MEDICATIONS
   - Aspirin: 325mg chewed (antiplatelet)
   - Nitrates: SL nitroglycerin for chest pain
   - Opioids: Morphine for pain control
   - Beta-blockers: For HR/BP control

5. EXAM-RELEVANT QUESTIONS FOR STUDENT
   - "What does ST elevation indicate?"
   - "What's the time window for thrombolysis?"
   - "How would you recognize right-sided MI?"
   - "What's the difference between stable and unstable angina?"
```

---

## Phase 3: Data Cleaning & Preparation

### Before Submitting Content

1. **Remove Non-Essential Elements**
   - ❌ Page numbers, headers/footers
   - ❌ Irrelevant ads, publisher info
   - ❌ Duplicate content
   - ✅ Keep: Clinical guidance, diagnostic criteria, case examples

2. **Standardize Formatting**
   - Use consistent heading levels
   - Keep bullet points for lists
   - Use numbered lists for sequences
   - Maintain clear section breaks

3. **Handle Medical Terminology**
   - Keep Latin/medical terms accurate
   - Add common language equivalents in parentheses
   - Document acronyms on first use
   
   Example:
   ```
   STEMI (ST-Elevation Myocardial Infarction): Full thickness 
   heart muscle damage with ST segment elevation on EKG.
   ```

4. **Convert Tables to Text** (if needed)
   ```
   ❌ TABLE:
   | Symptom | Mild | Moderate | Severe |
   |---|---|---|---|
   | Pain | <5 | 5-7 | >8 |
   
   ✅ STRUCTURED TEXT:
   Pain Assessment Scale:
   - Mild: <5 (manageable with rest)
   - Moderate: 5-7 (impacts daily activities)
   - Severe: >8 (debilitating, requires immediate care)
   ```

5. **Clean Formatting Issues**
   - Remove multiple spaces between words
   - Fix OCR errors (if from PDF extraction)
   - Remove excessive special characters
   - Standardize hyphenation

### Cleaning Checklist

```
[ ] Content is accurate and current (medical guidelines dated)
[ ] Removed all non-medical metadata
[ ] Standardized formatting and structure
[ ] Medical terms are properly spelled and defined
[ ] No duplicate content across documents
[ ] Clear section breaks between topics
[ ] Appropriate level of detail (medical student level)
[ ] Clinical relevance is evident
[ ] No copyright content that can't be used
```

---

## Phase 4: Tagging Strategy

### Scenario Tags
Tags link medical knowledge to specific exam scenarios. Common tags:

```
ANATOMY:
- anatomy_drill
- skeletal_system
- nervous_system
- cardiovascular_system

CLINICAL SPECIALTIES:
- orthopedics
- neurology
- cardiology
- emergency_medicine
- speech_pathology
- midwifery
- physiotherapy

CONDITIONS:
- stroke
- aphasia
- back_pain
- neck_pain
- cardiac_event
- trauma
- pediatric

EXAM TYPES:
- oral_exam
- patient_simulation
- diagnostic_decision
- emergency_response
- handover_communication
```

### How Tags Work in RAG

When an exam starts with scenario: `{"patient": "Stroke case", "role": "Paramedic"}`

The system retrieves knowledge tagged with:
- `stroke`
- `emergency_medicine`
- `paramedic`
- `neurological_assessment`

---

## Phase 5: Document Submission Format

### For Submitting PDFs

```
SUBMISSION STRUCTURE:
/MedicalBooks
  ├── Orthopedics/
  │   ├── Assessment_Techniques.pdf
  │   ├── Common_Conditions.pdf
  │   └── README.txt (with chapter list)
  ├── Neurology/
  │   ├── Stroke_Management.pdf
  │   └── README.txt
  ├── Cardiology/
  │   ├── Acute_Syndromes.pdf
  │   └── README.txt
  └── DATA_MANIFEST.csv
```

### DATA_MANIFEST.csv Format
```csv
filename,specialty,priority,chapters_to_extract,clinical_focus,language
Orthopedics/Assessment_Techniques.pdf,Orthopedics,HIGH,Ch1-2-5,Assessment protocols,EN
Neurology/Stroke_Management.pdf,Neurology,HIGH,Ch3-4-8,Emergency management,EN
Cardiology/Acute_Syndromes.pdf,Cardiology,MEDIUM,Ch2-5,ACS recognition,EN
```

---

## Phase 6: Medical Content Best Practices

### What Makes Good RAG Content

✅ **GOOD:**
```
STROKE RECOGNITION - FAST TEST
- Face: Ask patient to smile, look for asymmetry/drooping
- Arm: Raise both arms, check for drift or weakness
- Speech: Repeat "The sky is blue", listen for slurring
- Time: Note exact time of symptom onset (critical for treatment window)

Clinical Interpretation:
If ANY component positive → IMMEDIATELY call emergency services
Time is critical: Every minute ~190,000 neurons die from brain ischemia
Door-to-needle time <60min significantly improves outcomes
```

❌ **POOR:**
```
Strokes are bad. They happen when blood doesn't reach the brain. 
You should recognize them. The FAST test is important for recognizing strokes. 
There are different types of strokes like ischemic and hemorrhagic.
```

### Why?
Good content:
- Specific and actionable
- Clinical and exam-relevant
- Properly structured
- Contains assessment criteria
- Has decision points

---

## Phase 7: Processing Pipeline (What We Do)

Once you provide the data, the system will:

```
1. EXTRACT TEXT
   ├─ PDF → Text extraction
   ├─ OCR correction
   └─ Format standardization

2. CHUNK CONTENT
   ├─ Break into 300-500 token chunks
   ├─ Maintain semantic boundaries
   └─ Preserve context

3. EMBED
   ├─ Use OpenAI embeddings API
   ├─ Generate vector representations
   └─ Store in vector database

4. INDEX & TAG
   ├─ Apply scenario tags
   ├─ Link to medical contexts
   └─ Create retrieval indexes

5. INTEGRATE
   ├─ Connect to exam scenarios
   ├─ Inject into system prompts
   └─ Enable RAG queries
```

---

## Phase 8: Feedback & Iteration

### How We'll Test Integration

1. **Sample Query Testing**
   ```
   Test: Scenario = "Stroke patient evaluation by paramedic"
   System retrieves:
   ✅ Stroke recognition criteria
   ✅ FAST test procedures
   ✅ Emergency timeline
   ✅ Paramedic protocols
   ```

2. **Relevance Scoring**
   ```
   We'll measure:
   - Precision: Are retrieved chunks relevant?
   - Recall: Did we get all relevant content?
   - Latency: How fast is retrieval?
   ```

3. **Exam Performance**
   ```
   We'll monitor:
   - Does AI give more accurate medical responses?
   - Are student evaluations more detailed?
   - Is knowledge contextually appropriate?
   ```

### Iteration Process
If content needs adjustment:
1. You flag problematic areas
2. We identify missing chunks
3. You provide additional content
4. We re-embed and re-integrate
5. Test again

---

## Summary Checklist for Your Medical Books

When you're ready to submit books:

- [ ] Books are in PDF or digital text format
- [ ] Content is medically accurate and current
- [ ] Organized by medical specialty/chapter
- [ ] Not copyright-restricted for your use
- [ ] Can be converted to text (if PDF)
- [ ] Include a manifest listing chapters/focus areas
- [ ] Identify priority topics for your exam scenarios
- [ ] Specify any specific clinical contexts (e.g., "Emergency protocols only", "Diagnostic guidelines only")

---

## Contact & Questions

As you prepare your medical content, note:
- What medical specialties does your app cover?
- What exam scenarios need knowledge support?
- Are books in English, German, or multilingual?
- What's your priority order for integration?

This information helps us structure the RAG system optimally.
