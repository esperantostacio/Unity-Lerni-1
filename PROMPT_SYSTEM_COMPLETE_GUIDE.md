# 🎯 Prompt System Explained & Fixed

## Your Questions Answered

### **Q: Why are there 2 prompts on D2D and 2 prompts on D2P?**

**A: System + User Template Pattern** (standard for all LLM APIs)

```
┌─────────────────────────────────────────────────────────────┐
│ How Claude API Calls Work                                   │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  Message #1: System Prompt                                  │
│  ├─ Role: "You are an experienced FSP examiner"             │
│  ├─ Rules: "Score using 3D schema"                          │
│  └─ Context: Theme, terms, case facts                       │
│                                                              │
│  Message #2: User Template + Payload                        │
│  ├─ "Evaluate this conversation:"                           │
│  ├─ [{COMBINED_INPUT} = actual transcript]                  │
│  ├─ "Return ONLY this JSON schema: {...}"                   │
│  └─ Wrapped with case context                               │
│                                                              │
│  Result: Claude responds with JSON                          │
│  └─ Scores + structured feedback                            │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

**Why separate?**
- **System Prompt** = Role & Rules (never changes per evaluation)
- **User Template** = Evaluation request + Transcript (changes per case)

This separation allows:
- Reusing the system prompt (efficiency)
- Different evaluation formats for different scenarios
- Clear separation of concerns

---

### **Q: Are we wrapping the payload with conversation?**

**A: YES—exactly right!** Here's the flow:

```
1. Student finishes conversation
   ↓
2. Transcript recorded (or transcribed via Whisper)
   ↓
3. System loads CSV prompts:
   • eval.one_call.d2d.system
   • eval.one_call.d2d.user_template
   ↓
4. Placeholder substitution:
   {THEME_FIRST_TERM} → "Tonsillitis"
   {THEME_TERMS} → "Dyspnoe, Koronarangiografier, Antibiotika"
   {TERMS_JSON} → {"terms": ["Dyspnoe", "Koronarangiografie"]}
   {COMBINED_INPUT} → "[Doctor]: Guten Tag... [Patient]: Hallo..."
   ↓
5. Send to Claude:
   [System] + [User Template with {COMBINED_INPUT}]
   ↓
6. Claude returns JSON with 3D scores
   ↓
7. Unity parses & displays on UI
```

**The "payload wrapping":**
```
Prompt Template:
─────────────────────────────────────────
Thema: {THEME_FIRST_TERM}
Begriffe: {THEME_TERMS}

Transkript:
{COMBINED_INPUT}  ← Your conversation goes here

Bewerte nach diesem Schema:
[scoring criteria]

Gib nur JSON zurück:
{...}
─────────────────────────────────────────
```

---

### **Q: What are {THEME_FIRST_TERM}, {THEME_TERMS}, {CURRENT_THEME}?**

These are **template placeholders** filled from your case database:

| Variable | Source | Example |
|----------|--------|---------|
| `{THEME_FIRST_TERM}` | Case primary topic | "Tonsillitis" |
| `{CURRENT_THEME}` | Full theme description | "Acute bacterial pharyngitis with fever..." |
| `{THEME_TERMS}` | Important case words | "Pharyngitis, Dyspnoe, Antibiotika-Resistenz" |
| `{TERMS_JSON}` | Structured version | `{"terms":["Pharyngitis","Dyspnoe"]}` |
| `{CONTEXT}` | Full case background | Complete patient/examiner scenario |
| `{COMBINED_INPUT}` | **The student's transcript** | Actual conversation |
| `{SCENARIO_NAME}` | Case ID | "Fallszenario_001" |
| `{CORE_FACTS}` | Must-know facts | "Patient: 25yo male, fever 38.5°C" |
| `{EXPECTED_QUESTIONS}` | Likely follow-ups | "What investigations were done?" |

---

## Issues Found in Your Current Prompts

### 1. **Grammar Issues** (German standards)
- ❌ `Pruefer` → ✅ `Prüfer`
- ❌ `fuer` → ✅ `für`
- ❌ `Uebergabegespraech` → ✅ `Übergabegespräch`
- ❌ `ueberwiegend` → ✅ `überwiegend`
- ❌ `Fluessigkeit` → ✅ `Flüssigkeit`

### 2. **Undefined Terms**
- ❌ `"ACTIVE CASE REFERENCE"` → Never explained
  - **FIX:** Define it in the system prompt
  - Use: "Fallkontext" (case context)

### 3. **Redundancy**
- ❌ `"3D-D2D-Schema"` → Says "3D" and "D2D" twice
  - **FIX:** Just say `"3-dimensionales Schema"`

### 4. **Clarity Issues**
- ❌ `"5-Begriffe-Terminologiephase"` → Confusing reference (only 3-5 terms typically)
  - **FIX:** Replace with "Fachliche Terminologie in der Gesprächsphase"
- ❌ `"sprachlicheAngemessenheit"` → CamelCase invalid in German
  - **FIX:** `"Sprachliche Angemessenheit"` with space

### 5. **Prompt Logic Issues**
- ❌ System prompt includes detailed scoring dimensions
  - **FIX:** System = role + rules, User Template = detailed structure
- ❌ References to scoring schema in system prompt before user template is shown
  - **FIX:** System prompt links to USER template for specifics

---

## How to Apply Fixes

### **Option 1: Python Script (Automated)**
```bash
cd /Users/k1bfs/Downloads/lerini3
python3 fix_prompt_grammar.py

# Then apply the fixed version:
mv Assets/Prompts/prompt_keys_runtime_only_FIXED.csv \
   Assets/Prompts/prompt_keys_runtime_only.csv
```

### **Option 2: Manual (if you prefer fine control)**
Edit these 4 keys in the CSV:
1. `eval.one_call.d2d.system`
2. `eval.one_call.d2d.user_template`
3. `eval.one_call.d2p.system`
4. `eval.one_call.d2p.user_template`

Use the corrected versions in `/PROMPT_ANALYSIS_AND_FIXES.md`

---

## Recommended Prompt Structure

### **System Prompt Job:**
✅ Set evaluator role  
✅ State evaluation rules  
✅ Define scoring principles  
✅ Link to user template for specifics  

**Example opening:**
```
Du bist ein erfahrener FSP-Prüfer für das Arzt-Arzt-Gespräch (AAG, Teil 3).

EVALUATIONSKONTEXT:
- Thema: {THEME_FIRST_TERM}
- Begriffe: {THEME_TERMS}
- Fallkontext (aus Fallreferenz): {CURRENT_THEME}

BEWERTUNGSPRINZIPIEN:
- Streng, fair, ausschließlich anhand von Transkriptbelegen
- Verwende Fallkontext als verbindliche Grundlage
- Folge exakt dem 3-dimensionalen Schema aus der User-Template

BEWERTUNGSREGELN:
[Rules...]

Antworte AUSSCHLIESSLICH mit gültigem JSON ohne Markdown.
```

### **User Template Job:**
✅ Wrap conversation with context  
✅ Specify exact JSON output schema  
✅ Provide evaluation instructions  

**Example structure:**
```
Bewerte dieses Arzt-Arzt-Gespräch für die FSP.

GESPRÄCHSKONTEXT:
- Thema: {THEME_FIRST_TERM}
- Fachbegriffe: {THEME_TERMS}

BEWERTUNGSSCHEMA (3-dimensional):
1. Sprachliche Angemessenheit (0–7)
2. Inhaltliche Angemessenheit (0–3)
3. Malus Patientensicherheit (0–5)

TRANSKRIPT ZUM BEWERTEN:
{COMBINED_INPUT}

AUSGABE (NUR JSON):
{...}
```

---

## Process Recap (Unity Flow)

```
┌─── REALTIME PHASE ───────────────────────────────┐
│ Student/Candidate speaks with AI                 │
│ • Lives with realtime.guardrails.* prompts       │
│ • Phases: greeting → presentation → discussion   │
│ • Transcript captured in real-time                │
└──────────────────────────────────────────────────┘
                      ↓
┌─── EVALUATION PHASE ─────────────────────────────┐
│ Conversation ends, evaluation triggered           │
│ 1. Load: eval.one_call.d2[d/p].system            │
│ 2. Load: eval.one_call.d2[d/p].user_template     │
│ 3. Fill placeholders from case DB                │
│ 4. Send [System] + [User + {COMBINED_INPUT}]     │
│    to Claude API                                  │
│ 5. Parse JSON response                           │
│ 6. Display scores on EvaluationDisplayUI         │
└──────────────────────────────────────────────────┘
```

---

## Files Provided

1. **`PROMPT_ANALYSIS_AND_FIXES.md`** - Detailed before/after prompts
2. **`fix_prompt_grammar.py`** - Automated grammar fixer script
3. This document - Complete explanation

---

## Next Steps

1. ✅ **Understand the 2-prompt pattern** (System + User Template)
2. ✅ **Review the fixed prompts** in `PROMPT_ANALYSIS_AND_FIXES.md`
3. Choose: Run script OR manually update the 4 keys
4. Test with actual conversations
5. Monitor evaluation outputs for quality

Questions? Issues? Let me know!
