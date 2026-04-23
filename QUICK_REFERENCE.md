# 🚀 Quick Reference: Prompt System

## The 2-Prompt Pattern (Why?)

```
                    UNITY APP
                        │
        ┌───────────────┼───────────────┐
        │               │               │
        ▼               ▼               ▼
    CSV File        Transcript    Case Database
        │            Captured      (Theme, Terms)
        │               │               │
        └───────────────┼───────────────┘
                        │
            ┌───────────▼────────────┐
            │  Load 2 Prompts       │
            │  from CSV             │
            └───────────┬────────────┘
                        │
        ┌───────────────┼───────────────┐
        │               │               │
    Prompt #1        Prompt #2      (Placeholders)
    (System)      (User Template)   (Substitution)
        │               │               │
        └───────────────┼───────────────┘
                        │
            ┌───────────▼────────────┐
            │   Claude API Request   │
            │  [System] + [User +    │
            │   {COMBINED_INPUT}]    │
            └───────────┬────────────┘
                        │
                ┌───────▼─────────┐
                │  Claude         │
                │  Evaluates      │
                │  Returns JSON   │
                └───────┬─────────┘
                        │
            ┌───────────▼────────────┐
            │  Parse JSON            │
            │  Display on UI         │
            │  (3D or 5D Scores)     │
            └────────────────────────┘
```

---

## Two-Prompt Architecture

### **Prompt #1: SYSTEM** 
*Sets context and rules (stable)*
```
KEY: eval.one_call.d2d.system
PURPOSE: Role definition + evaluation rules
CONTENT:
  • "You are an experienced FSP examiner"
  • Scoring principles
  • Quality rules
  • Link to JSON template
```

### **Prompt #2: USER TEMPLATE**
*Wraps conversation + structure (dynamic)*
```
KEY: eval.one_call.d2d.user_template
PURPOSE: Case context + evaluation structure
CONTENT:
  • Theme: {THEME_FIRST_TERM}
  • Terms: {THEME_TERMS}
  • Conversation: {COMBINED_INPUT}  ← STUDENT TRANSCRIPT
  • JSON schema: {...}
```

---

## D2D vs D2P

| Aspect | D2D (Doctor→Doctor) | D2P (Doctor→Patient) |
|--------|-------------------|-------------------|
| **Focus** | Medical precision, terminology | Communication, empathy |
| **Dimensions** | 3D (Language/Content/Safety) | 5D (Communication/Understanding/Structure/Empathy/Completeness) |
| **Scale** | 0–10 | 0–20 |
| **Tone** | Formal, technical, collegial | Patient-friendly, educational |
| **System Prompt** | `eval.one_call.d2d.system` | `eval.one_call.d2p.system` |
| **Template** | `eval.one_call.d2d.user_template` | `eval.one_call.d2p.user_template` |

---

## Placeholder Variables (@Runtime)

```
{THEME_FIRST_TERM}     → "Tonsillitis"
{CURRENT_THEME}        → Full theme description
{THEME_TERMS}          → "Pharyngitis, Dyspnoe, Koronariographie"
{TERMS_JSON}           → {"terms": ["Pharyngitis", "Dyspnoe"]}
{CONTEXT}              → Full case background
{COMBINED_INPUT}       ← **STUDENT'S ACTUAL TRANSCRIPT**
{SCENARIO_NAME}        → Case ID
{CORE_FACTS}           → Key facts for evaluation
{EXPECTED_QUESTIONS}   → Likely follow-ups
```

---

## Grammar Issues (Fixed)

| Old | New | Type |
|-----|-----|------|
| `Pruefer` | `Prüfer` | Umlaut |
| `fuer` | `für` | Umlaut |
| `Uebergabegespraech` | `Übergabegespräch` | Umlaut |
| `ueberwiegend` | `überwiegend` | Umlaut |
| `Fluessigkeit` | `Flüssigkeit` | Umlaut |
| `sprachlicheAngemessenheit` | `Sprachliche Angemessenheit` | Spacing |
| `3D-D2D-Schema` | `3-dimensionales Schema` | Redundancy |
| `ACTIVE CASE REFERENCE` | `Fallkontext` | Clarity |

---

## CSV Structure

```
key,locale,enabled,version,text,scenario_id,...
eval.one_call.d2d.system,*,TRUE,1,"[PROMPT TEXT]",...
eval.one_call.d2d.user_template,*,TRUE,1,"[PROMPT TEXT]",...
eval.one_call.d2p.system,*,TRUE,1,"[PROMPT TEXT]",...
eval.one_call.d2p.user_template,*,TRUE,1,"[PROMPT TEXT]",...
...
```

**Key fields:**
- `key` - Unique identifier
- `locale` - Language (`*` = all)
- `enabled` - TRUE/FALSE
- `text` - The actual prompt (multiline OK)

---

## Files for You

### 📄 `PROMPT_SYSTEM_COMPLETE_GUIDE.md`
Full explanation with flow diagrams

### 📄 `PROMPT_ANALYSIS_AND_FIXES.md`
Before/After prompts with all corrections

### 🐍 `fix_prompt_grammar.py`
Automated script to apply all fixes

---

## How to Use

### Quick Fix (Automated)
```bash
python3 fix_prompt_grammar.py
mv Assets/Prompts/prompt_keys_runtime_only_FIXED.csv \
   Assets/Prompts/prompt_keys_runtime_only.csv
```

### Manual Fix
1. Open `Assets/Prompts/prompt_keys_runtime_only.csv`
2. Edit these 4 rows: `eval.one_call.d2d.system/user_template` + `eval.one_call.d2p.system/user_template`
3. Use corrected text from `PROMPT_ANALYSIS_AND_FIXES.md`
4. Save & test

---

## Remember

✅ **2 prompts = System (rules) + User (input)**  
✅ **Placeholders filled at runtime** from case DB + transcript  
✅ **{COMBINED_INPUT} = student's actual conversation**  
✅ **D2D = 3-dimensional scoring** (Language/Content/Safety)  
✅ **D2P = 5-dimensional scoring** (Comm/Understanding/Structure/Empathy/Completeness)  

Questions? See `PROMPT_SYSTEM_COMPLETE_GUIDE.md` →
