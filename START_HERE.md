# START HERE

This is the single entry point for AI prompt work in this project.

If you are a future AI assistant, read this file first, then follow the links in order.

## Purpose

- Reduce token usage in future sessions
- Avoid re-discovering the same architecture repeatedly
- Keep one running record of what was achieved
- Provide direct links between scripts, docs, and runtime prompt files

## Current Status (As Of 2026-04-05)

What has been achieved:
- Prompt architecture was mapped: D2D and D2P each use 2 prompts (system + user template)
- Runtime placeholder flow was documented ({COMBINED_INPUT}, {THEME_FIRST_TERM}, {THEME_TERMS}, etc.)
- Prompt evaluation flow Unity -> Claude -> JSON -> UI was documented
- Grammar/clarity improvement strategy was drafted
- Utility scripts and docs were created to speed future prompt editing

Important current state:
- Main runtime prompt file is currently in original form (not fully replaced with rewritten prompts yet)
- Use the docs below to apply controlled edits when ready

## Read Order (Fast Path)

1. [QUICK_REFERENCE.md](QUICK_REFERENCE.md)
2. [PROMPT_SYSTEM_COMPLETE_GUIDE.md](PROMPT_SYSTEM_COMPLETE_GUIDE.md)
3. [PROMPT_ANALYSIS_AND_FIXES.md](PROMPT_ANALYSIS_AND_FIXES.md)
4. [README_PROMPT_FIXES.md](README_PROMPT_FIXES.md)

## Core Runtime File

- [Assets/Prompts/prompt_keys_runtime_only.csv](Assets/Prompts/prompt_keys_runtime_only.csv)

This is the file used for runtime prompt keys and text.

## Script Index

### 1) fix_prompt_grammar.py
- File: [fix_prompt_grammar.py](fix_prompt_grammar.py)
- Purpose: Applies German grammar/clarity replacements to eval prompt text and writes a fixed CSV copy
- Input: Assets/Prompts/prompt_keys_runtime_only.csv
- Output: Assets/Prompts/prompt_keys_runtime_only_FIXED.csv
- Use when: You want a quick bulk cleanup pass
- Note: Review output before replacing production CSV

### 2) rewrite_prompts_csv.py
- File: [rewrite_prompts_csv.py](rewrite_prompts_csv.py)
- Purpose: Generates an example CSV containing prompt key templates and starter texts
- Output target: Assets/Prompts/example_prompts.csv
- Use when: You want a clean baseline or compare structure/key coverage
- Note: This is a generator script, not the runtime source itself

### 3) build_rag_db.py
- File: [build_rag_db.py](build_rag_db.py)
- Purpose: Builds/updates retrieval database context (separate from prompt text authoring)
- Use when: RAG context quality affects evaluation grounding

## Document Index

### QUICK_REFERENCE.md
- File: [QUICK_REFERENCE.md](QUICK_REFERENCE.md)
- Small resume: One-page architecture cheat sheet with flow and placeholders

### PROMPT_SYSTEM_COMPLETE_GUIDE.md
- File: [PROMPT_SYSTEM_COMPLETE_GUIDE.md](PROMPT_SYSTEM_COMPLETE_GUIDE.md)
- Small resume: Detailed end-to-end explanation of how evaluation prompts work in Unity and API calls

### PROMPT_ANALYSIS_AND_FIXES.md
- File: [PROMPT_ANALYSIS_AND_FIXES.md](PROMPT_ANALYSIS_AND_FIXES.md)
- Small resume: Problem list and corrected prompt proposals (D2D + D2P)

### README_PROMPT_FIXES.md
- File: [README_PROMPT_FIXES.md](README_PROMPT_FIXES.md)
- Small resume: Consolidated summary of what was investigated and proposed

## Link Between Scripts And Runtime Flow

- Runtime execution depends on: [Assets/Prompts/prompt_keys_runtime_only.csv](Assets/Prompts/prompt_keys_runtime_only.csv)
- Proposed text cleanup path:
  1) Run [fix_prompt_grammar.py](fix_prompt_grammar.py)
  2) Review output CSV
  3) Merge accepted changes into runtime CSV
- Structure/baseline comparison path:
  1) Generate [Assets/Prompts/example_prompts.csv](Assets/Prompts/example_prompts.csv) via [rewrite_prompts_csv.py](rewrite_prompts_csv.py)
  2) Compare key coverage and wording
  3) Backport selected improvements into runtime CSV

## Future AI Session Protocol (Mandatory)

For every future prompt-related request in this repo:
- Step 1: Read [START_HERE.md](START_HERE.md)
- Step 2: Add a new entry in Update Log below
- Step 3: Include in that entry:
  - What was achieved
  - Which files were edited
  - Script names used
  - Small resume of each script/doc touched
  - Next recommended action

## Update Log

### 2026-04-25 - Unified CEO 5-Criterion Grid for D2D and D2P
- Achieved:
  - Clarified that **both D2D and D2P use the same CEO 5-criterion grid**: Inhalt / Gesprächsfähigkeit / Wortschatz / Grammatik / Aussprache — each max 3.99, total /20, pass ≥12
  - Fixed D2P parsing: was looking for old legacy keys (`kommunikation`, `hoerverstehen`, etc.); now routes through `TryBuildD2DEvaluation` which reads CEO keys (`content`, `conversation`, `vocabulary`, `grammar`, `pronunciation`) — same parser as D2D
  - Fixed `hasExpectedKeys` for D2P to recognise `criteria`, `content`, `finalVerdict`, `totalScore` as valid structures (prevents spurious repair calls)
  - Fixed D2D display: was only showing 3 of 5 criteria; added `d2dGrammarScoreText` and `d2dPronunciationScoreText` inspector fields and display code for all 5
  - Fixed D2P display: now shows CEO labels (Inhalt / Gesprächsfähigkeit / Wortschatz / Grammatik / Aussprache) instead of legacy labels; uses `d2dContent`, `d2dConversation`, `d2dVocabulary`, `d2dGrammar`, `d2dPronunciation` fields
  - Updated secondary parser (`SendFeedbackToGPTForParsing`) prompt and parsing to use CEO schema
  - Updated `DisplayFeedbackDelayed` for D2P to use `BuildD2DFiveCriteriaFeedback` when `usesD2DFiveCriteria` is true
  - Legacy 5D fallback preserved for both paths (old `kommunikation`/`hoerverstehen` responses still display correctly)
- Files edited:
  - [Assets/Scripts/EvaluationDisplayUI.cs](Assets/Scripts/EvaluationDisplayUI.cs) — all display and parsing paths above
- Scripts used:
  - None
- Unity wiring needed:
  - In Inspector, assign two new fields on `EvaluationDisplayUI`: `d2dGrammarScoreText` and `d2dPronunciationScoreText` — point them at the Grammatik and Aussprache text objects in the D2D results panel
- Next best action:
  - Wire the two new Inspector fields (Grammatik + Aussprache) in the D2D panel
  - Run a test D2D and D2P session; verify Console shows `D2D CEO evaluation parsed` and `D2P CEO evaluation parsed` with non-zero scores for all 5 criteria

### 2026-04-13 - D2P Realtime Prompt Injection Hardening
- Achieved:
  - Added a defensive prompt-injection layer so D2P patient identity guardrails are enforced before every realtime `session.update`, not only during initial prompt generation
  - Refactored D2P identity text into reusable helper methods and applied it at session start, reconnect/resume, and phase transitions
  - This prevents fallback to generic assistant behavior when any upstream prompt source fails to include role identity text
- Files edited:
  - [Assets/Scripts/MedicalExamManager.cs](Assets/Scripts/MedicalExamManager.cs)
  - [START_HERE.md](START_HERE.md)
- Scripts used:
  - None
- Next best action:
  - Verify in Unity Console that D2P logs show identity guardrail injection before `session.update`
  - If generic responses persist, check scene for any active `RealtimeConversationManager` component with `speakFirstOnStart=true`

### 2026-04-05 - D2P Case 1 Enriched with CEO Case Details
- Achieved:
  - Updated d2p_case_1 in cases_json (prompt_keys_runtime_only.csv) with comprehensive patient and clinical details from CEO case prompt
  - Expanded core_facts from 5 to 19 items: patient demographics, occupation, complete symptomatology (chronic + acute), associated symptoms, negative symptoms, self-medication, prior history, vaccination status, medications, allergies, lifestyle (nicotine/alcohol, sports, travel, work stress), family history, likely diagnosis, differentials, and management steps
  - Expanded expected_questions from 5 to 10 items: symptom duration, fever measurement, respiratory/ENT symptoms, medications/allergies, nicotine/alcohol, fever escalation timeline, infectivity concerns (children), recovery timeframe, work/absence planning, follow-up criteria
  - Synced case structure: text now includes occupation, chronic fatigue, acute fever timeline, current asymptomatic status except sore throat
  - Verified case_id and title remain stable (d2p_case_1, "Frau Huber - Akute Pharyngotonsillitis")
- Files edited:
  - [Assets/Prompts/prompt_keys_runtime_only.csv](Assets/Prompts/prompt_keys_runtime_only.csv) — d2p_case_1 entry in cases_json
- Scripts used:
  - Python CSV + JSON parser: direct runtime update
  - [START_HERE.md](START_HERE.md)
  - Verify the enriched case data appears correctly in ACTIVE CASE REFERENCE during D2P anamnesis phase
  - Optionally sync any updates to [Assets/Prompts/d2p_case_1.txt](Assets/Prompts/d2p_case_1.txt) reference documentation
### 2026-04-05 - All Case Phases Enriched with CEO Guidance (D2D + D2P)
- Achieved:
  - Enriched phase-level guidance nested in d2d_case_1 and d2p_case_1 cases_json with comprehensive detail extracted from phase TXT files
  - **D2D Case 1 Phases:**
    - Greeting: professional greeting only, no patient content; tone guidance (professional-collegial, slightly distant, neutral); 4 expected responses
    - Presentation: structured presentation requirements (name/age/symptoms/history/diagnostics/meds/plan); 6 core facts; 6 follow-up questions
    - Discussion: diagnosis reasoning, differential diagnoses with clinical exclusion strategy; 6 core facts; 7 follow-up questions
    - Terms: terminology testing procedure, term examples, scoring basis (5 correct=1.5pts, 4=1pt, ≤3=0pts); 6 guidance facts; 6 term/response examples
  - **D2P Case 1 Phases:**
    - Greeting: patient introduces self, basic personal data, short symptom overview; 4 intro patterns
    - Anamnesis: core history collection (symptoms, timeline, associated/negative, prior history, meds/allergies, lifestyle, family); 7 content items; 8 follow-up questions
    - Summary: patient confirms/corrects summary, asks final practical questions; 6 behavior types; 8 Q/A patterns
  - All phases now serve as nested case-context overlays enriching phase behavior during runtime simulation
- Files edited:
  - [Assets/Prompts/prompt_keys_runtime_only.csv](Assets/Prompts/prompt_keys_runtime_only.csv) — d2d_case_1 and d2p_case_1 phases nested data
- Scripts used:
  - Python CSV + JSON enrichment: full phase guidance extraction and injection
- Docs updated:
  - [START_HERE.md](START_HERE.md)
- Next best action:
  - Verify runtime behavior: phase prompts now use nested phase guidance as context overlay alongside main prompt rows


### 2026-04-05 - D2D Case 1 Enriched with CEO Case Details
- Achieved:
  - Updated d2d_case_1 in cases_json (prompt_keys_runtime_only.csv) with comprehensive clinical details from CEO case prompt
  - Expanded core_facts from 5 to 16 items: patient demographics, full symptomatology, vital signs (ED + current), comorbidities, risk factors, medications, allergies, complete physical exam, lab findings, ECG details, differential diagnoses, and planned procedures
  - Expanded expected_questions from 5 to 10 items: full diagnostic reasoning, differential management, procedural justification, temporal dynamics, ECG interpretation, troponin context, vital sign interpretation, murmur significance, anticoagulation strategy, comorbidity management
  - Synced case structure: text now includes both acute presentation and current asymptomatic status
  - Verified case_id and title remain stable (d2d_case_1, "Herr Drescher - NSTEMI")
- Files edited:
  - [Assets/Prompts/prompt_keys_runtime_only.csv](Assets/Prompts/prompt_keys_runtime_only.csv) — d2d_case_1 entry in cases_json
- Scripts used:
  - Python CSV + JSON parser: direct runtime update
- Docs updated:
  - [START_HERE.md](START_HERE.md)
- Next best action:
  - Verify the enriched case data appears correctly in ACTIVE CASE REFERENCE during D2D evaluation
  - Optionally sync any updates to [Assets/Prompts/d2d_case_1.txt](Assets/Prompts/d2d_case_1.txt) reference documentation

### 2026-04-05 - D2D Evaluation Prompt Refactored (JSON + Tracked Mistakes)
- Achieved:
  - Reworked the D2D evaluation prompts to follow the CEO-style 5-criterion grid again instead of the interim 3D grid
  - Standardized the runtime JSON contract to: finalVerdict, totalScore, criteria.{content, conversation, vocabulary, grammar, pronunciation}, strengths, areasForImprovement, criticalErrors, overallFeedback
  - Kept the enriched payload design: ACTIVE CASE REFERENCE + FULL CONVERSATION TRANSCRIPT + PRONUNCIATION_TRACKING_NOTES_PER_TURN + REALTIME AI FEEDBACK
  - Ensured tracked realtime mistakes, especially severity=critical entries, are explicitly reflected in the final D2D evaluation output
  - Removed legacy theme-based wording from the D2D evaluation prompts and switched the evaluator language to explicit case-based wording
  - Added first-class case placeholder aliases in Unity ({CASE_TERMS}, {CASE_TERMS_JSON}, {CASE_PRIMARY_TERM}, {CASE_CONTEXT}) while keeping legacy theme placeholders as backward-compatible aliases
- Files edited:
  - [Assets/Prompts/d2d_evaluation.txt](Assets/Prompts/d2d_evaluation.txt)
  - [Assets/Prompts/prompt_keys_runtime_only.csv](Assets/Prompts/prompt_keys_runtime_only.csv)
  - [Assets/Scripts/MedicalExamManager.cs](Assets/Scripts/MedicalExamManager.cs)
  - [Assets/Scripts/EvaluationDisplayUI.cs](Assets/Scripts/EvaluationDisplayUI.cs)
- Scripts used:
  - None (direct prompt-file edits)
- Docs updated:
  - [START_HERE.md](START_HERE.md)
- Next best action:
  - Migrate remaining non-D2D prompt families from legacy theme placeholders to the new case aliases as a follow-up cleanup
  - Verify CSV parses cleanly in Unity and that the final D2D UI now shows /20 totals from the new schema

### 2026-04-05 - Prompt Folder Converted To English-Only (.txt files)
- Achieved:
  - Converted all text prompt files in [Assets/Prompts](Assets/Prompts) from German to English-only
  - Removed German source sections from evaluation prompts and standardized to English evaluation templates
  - Updated D2D and D2P phase prompts, case files, and legacy patient prompt to English
- Files edited:
  - [Assets/Prompts/d2d_greeting.txt](Assets/Prompts/d2d_greeting.txt)
  - [Assets/Prompts/d2d_presentation.txt](Assets/Prompts/d2d_presentation.txt)
  - [Assets/Prompts/d2d_discussion.txt](Assets/Prompts/d2d_discussion.txt)
  - [Assets/Prompts/d2d_terms.txt](Assets/Prompts/d2d_terms.txt)
  - [Assets/Prompts/d2p_greeting.txt](Assets/Prompts/d2p_greeting.txt)
  - [Assets/Prompts/d2p_anamnesis.txt](Assets/Prompts/d2p_anamnesis.txt)
  - [Assets/Prompts/d2p_summary.txt](Assets/Prompts/d2p_summary.txt)
  - [Assets/Prompts/d2d_case_1.txt](Assets/Prompts/d2d_case_1.txt)
  - [Assets/Prompts/d2p_case_1.txt](Assets/Prompts/d2p_case_1.txt)
  - [Assets/Prompts/d2d_evaluation.txt](Assets/Prompts/d2d_evaluation.txt)
  - [Assets/Prompts/d2p_evaluation.txt](Assets/Prompts/d2p_evaluation.txt)
  - [Assets/Prompts/patient_case_de.txt](Assets/Prompts/patient_case_de.txt)
- Scripts used:
  - No standalone script; direct in-file replacement updates
- Docs updated:
  - [START_HERE.md](START_HERE.md)
- Next best action:
  - Optionally align [Assets/Prompts/prompt_keys_runtime_only.csv](Assets/Prompts/prompt_keys_runtime_only.csv) to the same English wording so TXT and CSV sources are fully consistent

### 2026-04-05 - Bilingual Eval Prompts Added (DE + EN)
- Achieved:
  - Added full English reference translations under the original German text in both evaluation prompt files
  - Preserved original German content at the top as requested
- Files edited:
  - [Assets/Prompts/d2d_evaluation.txt](Assets/Prompts/d2d_evaluation.txt)
  - [Assets/Prompts/d2p_evaluation.txt](Assets/Prompts/d2p_evaluation.txt)
- Scripts used:
  - No script execution required (direct prompt-file edit)
- Docs updated:
  - [START_HERE.md](START_HERE.md) updated with this entry
- Next best action:
  - If desired, mirror the same DE+EN format into runtime CSV keys for eval prompts to keep all evaluation text sources consistent

### 2026-04-05 - Prompt Architecture Baseline
- Achieved:
  - Identified and explained 2-prompt pattern (system + user template) for D2D/D2P
  - Mapped placeholder substitution and evaluation data flow
  - Created fast-reference documentation set
- Files created:
  - [QUICK_REFERENCE.md](QUICK_REFERENCE.md)
  - [PROMPT_SYSTEM_COMPLETE_GUIDE.md](PROMPT_SYSTEM_COMPLETE_GUIDE.md)
  - [PROMPT_ANALYSIS_AND_FIXES.md](PROMPT_ANALYSIS_AND_FIXES.md)
  - [README_PROMPT_FIXES.md](README_PROMPT_FIXES.md)
  - [fix_prompt_grammar.py](fix_prompt_grammar.py)
- Scripts involved:
  - [fix_prompt_grammar.py](fix_prompt_grammar.py): bulk cleanup helper
  - [rewrite_prompts_csv.py](rewrite_prompts_csv.py): baseline/example generator
- Next best action:
  - Decide whether to apply grammar-only cleanup first or do a full prompt rewrite pass with manual review

### 2026-04-10 - D2P AI Identity Fix (No Context Bug)
- Achieved:
  - Diagnosed root cause: D2P AI said "I'm OpenAI Realtime" when greeted because the system prompt lacked a patient identity and "du bist keine KI" guardrail
  - The D2P base prompt only injected a short third-person case `text` (no patient name) via `{CONTEXT}`; the `core_facts` block (which has "Patient: Maria Huber, 46-jaehrig...") was never sent to the AI
  - Fixed `GenerateSystemPrompt()` in MedicalExamManager.cs: for D2P role, now always appends an explicit identity block containing "Du bist KEINE KI", the case title (patient name), and the full `core_facts` string
  - Fixed `BuildPhaseTransitionMessage()`: replaced hardcoded "Frau Müller" with actual patient name parsed from `_selectedCaseTitle`
- Files edited:
  - [Assets/Scripts/MedicalExamManager.cs](Assets/Scripts/MedicalExamManager.cs) — `GenerateSystemPrompt()` (added D2P identity injection block), `BuildPhaseTransitionMessage()` (replaced hardcoded patient name)
- Scripts used:
  - None
- Docs updated:
  - [START_HERE.md](START_HERE.md)
- Next best action:
  - Also update the remote Google Sheets CSV: add `Du bist KEINE KI...` + `{CORE_FACTS}` to the `whisper.start.doctor_to_patient` prompt text so it's self-contained without needing the code fallback
  - Also update the `text` field of `d2p_case_1` in `cases_json` to include the patient name (currently it only says "46-jaehrige Patientin, Lehrerin..." — no name)

### 2026-04-23 - Two-Step Audio Flow (OSCE Instructions + Welcome) + VAD -15%
- Achieved:
  - Split welcome audio into two separate steps with separate Inspector fields and button methods
  - **Step 1 (scenario card click):** `PlayD2DInstructions()` / `PlayD2PInstructions()` — sets role, speaks OSCE task instructions, does NOT start conversation
  - **Step 2 (Start button):** `PlayWelcomeForSelectedRole()` — speaks the welcome text already in editor, then starts conversation
  - OSCE instruction texts are in new fields `doctorToDoctorInstructionsText` / `doctorToPatientInstructionsText`
  - Welcome texts are in existing fields `doctorToDoctorWelcomeText` / `doctorToPatientWelcomeText` (user sets these in Inspector)
  - Legacy `PlayDoctorWelcome()` / `PlayPatientWelcome()` kept for backwards compat
  - Reduced `silence_duration_ms` 3000 → 2550 ms (–15%): AI responds slightly faster
- Files edited:
  - [Assets/Scripts/WelcomeAudioPlayer.cs](Assets/Scripts/WelcomeAudioPlayer.cs) — new fields, `PlayD2DInstructions()`, `PlayD2PInstructions()`, `PlayWelcomeForSelectedRole()`, `PlayInstructionsCoroutine()`
  - [Assets/Scripts/OpenAIRealtimeClient.cs](Assets/Scripts/OpenAIRealtimeClient.cs) — `silence_duration_ms`
- Unity wiring needed:
  - D2D card button → `WelcomeAudioPlayer.PlayD2DInstructions()`
  - D2P card button → `WelcomeAudioPlayer.PlayD2PInstructions()`
  - Start button → `WelcomeAudioPlayer.PlayWelcomeForSelectedRole()`
- Next best action:
  - Rewire buttons in Unity Inspector as described above
  - Fill `doctorToDoctorWelcomeText` / `doctorToPatientWelcomeText` in Inspector with the second-sound text if not already set

### 2026-04-23 - OSCE Instructions as Scenario Audio + VAD -15%
- Achieved:
  - Replaced scenario selection audio text with OSCE task instructions (no more full scenario description)
  - D2D text: "Stellen Sie der ärztlichen Leitung den vorliegenden Patientenfall vor..." (2 sentences)
  - D2P text: "Führen Sie ein strukturiertes Anamnesegespräch..." (3 paragraphs)
  - Reduced `silence_duration_ms` 3000 → 2550 ms (–15%): AI responds slightly faster
  - VAD tuning history: 900 ms → 3000 ms → 2550 ms
- Files edited:
  - [Assets/Scripts/WelcomeAudioPlayer.cs](Assets/Scripts/WelcomeAudioPlayer.cs) — `doctorToDoctorWelcomeText` and `doctorToPatientWelcomeText`
  - [Assets/Scripts/OpenAIRealtimeClient.cs](Assets/Scripts/OpenAIRealtimeClient.cs) — `silence_duration_ms`
- Next best action:
  - Test: press scenario button → realtime voice should read the OSCE instructions, then conversation starts
  - If AI still cuts in too fast/slow, tune `silence_duration_ms` in OpenAIRealtimeClient ~line 639 (range 2000–3000)

### 2026-04-23 - Realtime Voice Welcome Before Conversation Start
- Achieved:
  - Replaced TTS welcome with a one-shot realtime voice welcome (same voice as the conversation)
  - D2D welcome text: "Hallo, Sie können mit der Fallvorstellung..." (voice: shimmer)
  - D2P welcome text: "Hallo, Sie können mit dem Anamnesegespräch..." (voice: coral)
  - Added `PlayWelcomeAndDisconnect(text, voice, onDone)` coroutine to `OpenAIRealtimeClient`: connect → minimal narrator session (VAD disabled) → `response.create` → wait for audio + PcmAudioPlayer drain → disconnect → callback
  - Added `IsPlaybackFullyDone` public property to `OpenAIRealtimeClient` that checks both `_audioIsPlaying` and `PcmAudioPlayer.IsPlaying`
  - `WelcomeAudioPlayer` auto-finds and prefers `realtimeClient`; TTS remains as fallback if none found
  - `playWelcomeBeforeStart` default changed to `true` — welcome plays automatically
- Files edited:
  - [Assets/Scripts/OpenAIRealtimeClient.cs](Assets/Scripts/OpenAIRealtimeClient.cs) — `PlayWelcomeAndDisconnect()`, `WelcomeSpeakCoroutine()`, `IsPlaybackFullyDone`
  - [Assets/Scripts/WelcomeAudioPlayer.cs](Assets/Scripts/WelcomeAudioPlayer.cs) — new texts, `realtimeClient` field, updated `PlayWelcomeAndStartConversation` coroutine
- Scripts used:
  - None
- Next best action:
  - Test: click D2D button → should hear shimmer voice say the intro → conversation starts
  - Test: click D2P button → should hear coral voice say the intro → conversation starts
  - If welcome voice is wrong, check `GetRealtimeVoiceForRole()` in MedicalExamManager and match voices here

### 2026-04-22 - VAD Tuned for Patient Exam Listener (micro1.ai style)
- Achieved:
  - `threshold` lowered 0.6 → 0.4: quiet fillers (hmm, äh, also...) now reliably detected as speech and reset the silence counter
  - `prefix_padding_ms` raised 300 → 500 ms: prevents first syllable being clipped after a thinking pause
  - `silence_duration_ms` raised 900 → 3000 ms: AI waits 3 full seconds of true silence before responding
  - Net effect: student can pause, say "hmm", think mid-sentence — AI only responds after 3 s of complete silence
  - VAD logic: any detected sound resets counter; silence_duration_ms only counts down while microphone is truly quiet
- Files edited:
  - [Assets/Scripts/OpenAIRealtimeClient.cs](Assets/Scripts/OpenAIRealtimeClient.cs) — `turn_detection` block (threshold, prefix_padding_ms, silence_duration_ms)
- Scripts used:
  - None
- Tuning guide (in [Assets/Scripts/OpenAIRealtimeClient.cs](Assets/Scripts/OpenAIRealtimeClient.cs) ~line 634):
  - `threshold` 0.3–0.5: lower = catches quieter sounds (good); too low = background noise never lets counter start (bad)
  - `silence_duration_ms` 2000–4000: raise if AI still cuts in; lower if responses feel sluggish
  - `prefix_padding_ms` 300–600: raise if first word of each sentence is clipped
- Next best action:
  - Test: say a sentence, pause 1 s (hmm), continue — AI should NOT respond until 3 s after you fully stop
  - If background noise keeps resetting the counter (AI never responds), raise threshold back to 0.5

### 2026-04-21 - Claude Eval + Azure Pronunciation + Three Runtime Bug Fixes
- Achieved:
  - Replaced GPT-4o final evaluation with Claude Sonnet 4.6 (via toggle `useClaudeForEvaluation`)
  - Added Azure Cognitive Services Pronunciation Assessment per user turn (real audio, not text-only)
  - Fixed D2P Proactive Information Dumping: added reactive-only behavioral rule to `BuildD2PIdentityBlock()` — patient now waits for questions instead of volunteering all symptoms upfront
  - Fixed D2D Terms Phase Skipping: Terms transition message now injects actual case terms and enforces "test exactly 5 terms before calling finish_phase"
  - Fixed D2D Audio Lag: reduced `silence_duration_ms` from 500ms → 350ms and `prefix_padding_ms` from 300ms → 200ms in `OpenAIRealtimeClient.SendSessionUpdate`; added single-question brevity rule to Discussion phase transition
  - Fixed compile error in `FineTunedGPT4EvaluationService`: moved `handleAssistantMessage` lambda declaration above the Claude routing block (C# forward-reference violation)
- Files edited:
  - [Assets/Scripts/MedicalExamManager.cs](Assets/Scripts/MedicalExamManager.cs) — `BuildD2PIdentityBlock()` reactive rule, `BuildPhaseTransitionMessage()` Terms + Discussion messages
  - [Assets/Scripts/OpenAIRealtimeClient.cs](Assets/Scripts/OpenAIRealtimeClient.cs) — VAD silence/padding thresholds
  - [Assets/Scripts/FineTunedGPT4EvaluationService.cs](Assets/Scripts/FineTunedGPT4EvaluationService.cs) — Claude routing block reordered; compile error fixed
  - [Assets/Scripts/RealtimeMicrophone.cs](Assets/Scripts/RealtimeMicrophone.cs) — added 5 public accessors for Azure audio tap
  - [Assets/Scripts/ClaudeEvaluationService.cs](Assets/Scripts/ClaudeEvaluationService.cs) — NEW: Anthropic API wrapper
  - [Assets/Scripts/AzurePronunciationService.cs](Assets/Scripts/AzurePronunciationService.cs) — NEW: Azure Speech REST pronunciation service
- Scripts used:
  - None
- Next best action:
  - Assign Azure subscription key + region in Inspector (`AzurePronunciationService` component), tick `useAzurePronunciation`
  - Assign Claude API key in Inspector (`ClaudeEvaluationService` component), tick `useClaudeForEvaluation`
  - Test D2P greeting: patient should say only "Guten Tag, ich bin [Name]." and stop
  - Test D2D: verify Terms phase starts and AI presents 5 terms before finishing
  - Monitor Unity Console for `[Azure]` and `[Claude]` log lines to confirm both services are active

### 2026-04-25 - Smooth AI Talk UI + Phase Transition Safety
- Achieved:
  - **Smooth indicators**: `aiTalkingIndicator` and `recordingIndicator` now fade in/out (configurable `indicatorFadeDuration`, default 0.25s) instead of snapping on/off
  - **Pulse while talking**: `aiTalkingIndicator` pulses between `talkingIndicatorPulseMin` and 1.0 alpha while AI is speaking (configurable speed/range in Inspector)
  - **Transcript fade-in**: `aiLiveTranscriptCanvasGroup` fades from 0→1 each time AI begins a new response, reset to 0 on `response.created`
  - **Phase transition null guard**: `SendText(transitionMessage)` now has explicit null check on realtimeClient before calling (prevents silent crash if session drops mid-phase)
  - New Inspector fields: `aiTalkingIndicatorCanvasGroup`, `recordingIndicatorCanvasGroup`, `indicatorFadeDuration`, `talkingIndicatorPulseMin`, `talkingIndicatorPulseSpeed`
- Files edited:
  - [Assets/Scripts/MedicalExamManager.cs](Assets/Scripts/MedicalExamManager.cs) — `OnRealtimeAudioStarted`, `OnRealtimeAudioFinished`, `OnRealtimeResponseCreated`, `OnRealtimeSessionReady`, `AdvancePhase`, added `FadeCanvasGroup`, `FadeCanvasGroupThenDisable`, `PulseCanvasGroup`, `ShowIndicatorSmooth`, `HideIndicatorSmooth`
- Unity wiring needed:
  - Add a `CanvasGroup` component to your AI Talking indicator GameObject, then assign it to `aiTalkingIndicatorCanvasGroup`
  - Add a `CanvasGroup` component to your Recording indicator GameObject, then assign it to `recordingIndicatorCanvasGroup`
  - Both fields are optional — if not assigned, snapping behavior is preserved as fallback

### 2026-04-25 - Pre-Session Audit Fixes (3 Critical Bugs)
- Achieved:
  - **RemotePromptManager**: Network/parse failures no longer clear in-memory cache — app keeps working on last good CSV data instead of serving blank prompts
  - **AdvancePhase guard**: If `currentPhase` is not found in `phaseConfigs`, now logs error and returns instead of triggering premature exam completion
  - **Evaluation null fallback**: When evaluation JSON parse fails, user now sees German error message ("Auswertung konnte nicht geladen werden...") instead of being stuck on loading screen forever
- Files edited:
  - [Assets/Scripts/Prompts/RemotePromptManager.cs](Assets/Scripts/Prompts/RemotePromptManager.cs) — removed all `_selectedByKey.Clear()` calls from error branches; cache preserved on failure
  - [Assets/Scripts/MedicalExamManager.cs](Assets/Scripts/MedicalExamManager.cs) — `AdvancePhase()` early return guard + `OnEvaluationParsed()` null fallback with user-visible error
- Scripts used:
  - None
- Next best action:
  - Verify Console shows "Keeping N cached prompts as fallback" when simulating offline
  - Verify evaluation screen shows error text if Claude API key is missing/invalid

### 2026-04-25 - Frozen Timer + Student-First Start + D2D Identity Guardrail
- Achieved:
  - Timer now frozen at full duration on Start click; starts only when student begins speaking (VAD `speech_started` event)
  - Added `OnUserSpeechStarted` event to `OpenAIRealtimeClient`, fired on `input_audio_buffer.speech_started`
  - Added `OnRealtimeUserSpeechStarted()` handler in `MedicalExamManager` that triggers `_examTimerStarted` on first student speech
  - Both D2D and D2P now show recording indicator (awaiting student) after session.updated, instead of D2D doing nothing
  - Added `BuildD2DIdentityBlock()`: prevents AI from saying "Was kann ich für Sie tun?" — D2D AI now waits silently and responds in character when student speaks
  - `EnsureRoleIdentityInInstructions()` now injects D2D identity block for D2D (was D2P only)
  - Updated welcome texts: "Die Zeit startet, sobald Sie zu sprechen beginnen" (was "Die Zeit geht jetzt los")
- Files edited:
  - [Assets/Scripts/OpenAIRealtimeClient.cs](Assets/Scripts/OpenAIRealtimeClient.cs) — `OnUserSpeechStarted` event + fire on `speech_started`
  - [Assets/Scripts/MedicalExamManager.cs](Assets/Scripts/MedicalExamManager.cs) — frozen timer, `OnRealtimeUserSpeechStarted`, `BuildD2DIdentityBlock`, extended `EnsureRoleIdentityInInstructions`, fixed `OnRealtimeSessionReady` for D2D
  - [Assets/Scripts/WelcomeAudioPlayer.cs](Assets/Scripts/WelcomeAudioPlayer.cs) — updated welcome text defaults
- Scripts used:
  - None
- Next best action:
  - In Unity Console verify `⏱️ Exam timer started — student began speaking.` appears only after first speech
  - Verify D2D AI stays silent until student speaks, then responds as Chefärztin (no generic greeting)
  - If welcome texts were overridden in Inspector, update them manually to match the new default text

## Entry Template (Copy For Next Update)

### YYYY-MM-DD - Short Title
- Achieved:
  - 
- Files edited:
  - 
- Scripts used:
  - name: short resume
- Docs updated:
  - name: short resume
- Next best action:
  - 
