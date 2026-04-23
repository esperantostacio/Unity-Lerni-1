# ✅ Summary: Your Prompt Issues Fixed

## What You Asked

> "Why do I have 2 prompts on D2D and 2 prompts in D2P? Are we wrapping the payload with conversation? What are {THEME_FIRST_TERM}, {THEME_TERMS}? Grammar mistakes? What's this big prompt saying 'do evaluation'?"

## Quick Answers

### **1. Why 2 Prompts?**
✅ **This is correct & standard!** It's the **System + User Template pattern** used by all LLM APIs (Claude, GPT, etc.)

- **Prompt #1 (System)** = "You are an examiner. Here are the rules..."
- **Prompt #2 (User)** = "Here's the conversation. Evaluate it using this JSON schema: ..."

Both get sent together to Claude in one API call.

---

### **2. Wrapping with Conversation?**
✅ **Exactly right!** The payload wrapping happens here:

```
System Prompt: [Role + Rules]
   ↓
User Template: "Evaluate this:"
   ↓
{COMBINED_INPUT} ← Substituted with STUDENT'S ACTUAL TRANSCRIPT
   ↓
"JSON schema: {...}"
   ↓
Send to Claude API
```

---

### **3. Template Variables {THEME_FIRST_TERM}, etc.?**
These are **placeholders filled at runtime** from your case database:

```
{THEME_FIRST_TERM}   = "Tonsillitis"
{THEME_TERMS}        = "Pharyngitis, Dyspnoe, Antibiotika"
{CURRENT_THEME}      = Full theme description
{TERMS_JSON}         = {"terms": ["Pharyngitis", "Dyspnoe"]}
{COMBINED_INPUT}     = Student's conversation transcript ⭐
{CONTEXT}            = Full case background
...and more
```

---

### **4. Grammar Mistakes?**
✅ **Found and fixed!** Your current prompts use old German spelling:

```
❌ Old: Pruefer, fuer, Uebergabegespraech, ueberwiegend
✅ New: Prüfer, für, Übergabegespräch, überwiegend

❌ Old: 3D-D2D-Schema (redundant)
✅ New: 3-dimensionales Schema

❌ Old: sprachlicheAngemessenheit (camelCase - not German!)
✅ New: Sprachliche Angemessenheit (proper spacing)

❌ Old: "ACTIVE CASE REFERENCE" (undefined)
✅ New: "Fallkontext" (case context - defined)
```

---

## How It Works in Unity

```
┌──────────────────────────────────────────────┐
│ 1. Student/Candidate has conversation       │
│    (realtime.phase.* prompts guiding it)    │
└──────────────────────────────────────────────┘
                    ↓
┌──────────────────────────────────────────────┐
│ 2. Conversation ends, transcript captured   │
└──────────────────────────────────────────────┘
                    ↓
┌──────────────────────────────────────────────┐
│ 3. Load evaluation prompts from CSV:        │
│    • eval.one_call.d2d.system               │
│    • eval.one_call.d2d.user_template        │
└──────────────────────────────────────────────┘
                    ↓
┌──────────────────────────────────────────────┐
│ 4. Fill placeholders:                       │
│    {THEME_FIRST_TERM} → "Tonsillitis"      │
│    {COMBINED_INPUT} → Student's transcript  │
│    {THEME_TERMS} → Case words              │
│    ... etc                                   │
└──────────────────────────────────────────────┘
                    ↓
┌──────────────────────────────────────────────┐
│ 5. Send to Claude API:                       │
│    Message 1: [System Prompt]               │
│    Message 2: [User Template + Data]        │
└──────────────────────────────────────────────┘
                    ↓
┌──────────────────────────────────────────────┐
│ 6. Claude responds with JSON:               │
│    {                                         │
│      "sprachlicheAngemessenheit": 6,        │
│      "inhaltlicheAngemessenheit": 2,        │
│      "malusPatientensicherheit": 0,         │
│      "gesamt": 8,                           │
│      "overallFeedback": "..."                │
│    }                                         │
└──────────────────────────────────────────────┘
                    ↓
┌──────────────────────────────────────────────┐
│ 7. EvaluationDisplayUI parses & displays    │
│    scores on screen                         │
└──────────────────────────────────────────────┘
```

---

## D2D vs D2P

| | **D2D** (Doctor→Doctor) | **D2P** (Doctor→Patient) |
|---|---|---|
| **Prompts** | `eval.one_call.d2d.*` (2) | `eval.one_call.d2p.*` (2) |
| **Scoring** | 3 dimensions (0-10 total) | 5 dimensions (0-20 total) |
| **Type 1** | Sprachliche Angemessenheit | Kommunikation |
| **Type 2** | Inhaltliche Angemessenheit | Hörverstehen |
| **Type 3** | Malus Patientensicherheit | Gesprächsführung |
| **Type 4** | — | Empathie |
| **Type 5** | — | Vollständigkeit |

---

## Files Created for You

### 📋 **QUICK_REFERENCE.md** (5.9K)
- Visual diagrams of the 2-prompt system
- Quick lookup table for placeholders
- Grammar issues at a glance
- One-page cheat sheet

### 📚 **PROMPT_SYSTEM_COMPLETE_GUIDE.md** (9.0K)
- Full explanation with flow diagrams
- Answers to your questions
- Recommended prompt structure
- Next steps

### 🔧 **PROMPT_ANALYSIS_AND_FIXES.md** (7.9K)
- Before/After comparison for all 4 evaluation prompts
- Shows exactly what was fixed
- Full corrected prompt text ready to copy

### 🐍 **fix_prompt_grammar.py** (3.9K)
- Python script to automatically apply all fixes
- Run it and it generates `prompt_keys_runtime_only_FIXED.csv`
- Safe, non-destructive

---

## What to Do Now

### **Option A: Automatic (Recommended)**
```bash
cd /Users/k1bfs/Downloads/lerini3
python3 fix_prompt_grammar.py
# Creates prompt_keys_runtime_only_FIXED.csv
# Then move it:
mv Assets/Prompts/prompt_keys_runtime_only_FIXED.csv \
   Assets/Prompts/prompt_keys_runtime_only.csv
```

### **Option B: Manual**
1. Open [PROMPT_ANALYSIS_AND_FIXES.md](./PROMPT_ANALYSIS_AND_FIXES.md)
2. Copy the 4 corrected prompt sections
3. Paste into your CSV's corresponding rows:
   - `eval.one_call.d2d.system`
   - `eval.one_call.d2d.user_template`
   - `eval.one_call.d2p.system`
   - `eval.one_call.d2p.user_template`

### **Option C: Learn First, Fix Later**
1. Read [QUICK_REFERENCE.md](./QUICK_REFERENCE.md) (2 min)
2. Read [PROMPT_SYSTEM_COMPLETE_GUIDE.md](./PROMPT_SYSTEM_COMPLETE_GUIDE.md) (10 min)
3. Decide if you want fixes

---

## Key Takeaways

✅ **2 prompts = CORRECT design** (System + User Template)  
✅ **Conversation IS wrapped** in the user template  
✅ **Placeholders** substituted at runtime  
✅ **Grammar issues found** and fixed versions provided  
✅ **Process is standard** LLM architecture  

---

## Questions?

Refer to:
- **What's the overall flow?** → PROMPT_SYSTEM_COMPLETE_GUIDE.md
- **Quick lookup?** → QUICK_REFERENCE.md
- **Exact text fixes?** → PROMPT_ANALYSIS_AND_FIXES.md
- **Apply fixes automatically?** → Run fix_prompt_grammar.py

Your prompts are **well-designed**—they just need grammar corrections and minor clarity improvements.

Good luck! 🚀
