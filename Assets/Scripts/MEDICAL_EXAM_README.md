# Medical Oral Exam VR Application

## Overview
This is a VR application for medical oral examination training in Germany. The application allows doctors to practice oral exams in two scenarios:
- **Doctor to Doctor**: Practice being examined by another doctor
- **Doctor to Patient**: Practice patient consultation skills

The app uses OpenAI's Realtime GPT API for natural conversation and provides detailed evaluation with scores and feedback.

## Features
- ✅ Two role-based scenarios (Doctor/Patient)
- ✅ Dynamic system prompts based on selected role
- ✅ Timed examinations (configurable duration)
- ✅ Real-time conversation with AI
- ✅ Automatic evaluation at end of exam
- ✅ Detailed scoring: Terminologie, Verständlichkeit, Aussprache
- ✅ Overall score out of 100
- ✅ Conversation history tracking
- ✅ Multi-language support (English/Deutsch)

## Scripts Created

### 1. MedicalExamScenario.cs
**Purpose**: ScriptableObject to define exam scenarios

**Properties**:
- `scenarioName`: Name of the medical topic
- `medicalTopic`: Description of what to discuss
- `examDurationMinutes`: Duration (3-10 minutes)
- `difficultyLevel`: Difficulty rating (1-5)
- `language`: English or Deutsch

**How to create**:
1. Right-click in Project window
2. Create → Medical Exam → Scenario
3. Configure the scenario parameters

### 2. MedicalExamManager.cs
**Purpose**: Main manager for exam flow, UI, and evaluation

**Key Features**:
- Role selection UI (Doctor/Patient buttons)
- Exam timer with color coding
- System prompt generation based on role
- Conversation tracking
- Automatic/manual evaluation triggering
- Integration with RealtimeConversationManager

**Inspector Setup Required**:
- Assign `currentScenario` (MedicalExamScenario asset)
- Assign UI elements (buttons, panels, texts)
- Assign `conversationManager` (RealtimeConversationManager)
- Assign avatar GameObjects (doctorAvatar, patientAvatar)
- Optional: Assign `examHistory` for tracking

### 3. ExamEvaluation.cs
**Purpose**: Evaluation data structure and parsing

**Features**:
- Parses AI evaluation responses using regex
- Extracts scores: Terminologie, Verständlichkeit, Aussprache
- Calculates overall score
- Stores conversation transcript
- Color-coded score visualization
- ExamHistory ScriptableObject for tracking multiple exams

### 4. EvaluationDisplayUI.cs
**Purpose**: Visual display of evaluation results

**Features**:
- Color-coded score display
- Optional progress bars
- Formatted feedback display
- Metadata display (date, scenario, role)

## Setup Instructions

### Step 1: Create Scenario Assets
1. Create a new MedicalExamScenario:
   - Assets → Right-click → Create → Medical Exam → Scenario
   - Name it (e.g., "Cardiology_Basics")
   - Set scenario name: "Cardiology Basics"
   - Set medical topic: "Basic cardiovascular examination and common cardiac conditions"
   - Set duration: 5 minutes
   - Set language: English (or Deutsch)

2. Create multiple scenarios for different topics

### Step 2: Create Exam History Asset (Optional)
1. Assets → Right-click → Create → Medical Exam → Exam History
2. Name it "ExamHistory"
3. This will store all past evaluations

### Step 3: Setup Scene

#### UI Structure
Create this UI hierarchy:

```
Canvas
├── RoleSelectionPanel
│   ├── Title (TextMeshPro)
│   ├── ScenarioTitle (TextMeshPro)
│   ├── DoctorButton (Button + TextMeshPro)
│   └── PatientButton (Button + TextMeshPro)
├── ExamHUD
│   └── TimerText (TextMeshPro)
└── EvaluationPanel
    ├── EvaluationText (TextMeshPro)
    └── RestartButton (Button)
```

#### GameObjects
```
Scene
├── MedicalExamManager (empty GameObject)
│   └── MedicalExamManager.cs component
├── RealtimeConversationManager (existing)
├── DoctorAvatar (3D model with NPCDoctor.cs)
├── PatientAvatar (3D model)
├── VR Rig (XR Origin, etc.)
└── UI Canvas
```

### Step 4: Configure MedicalExamManager

1. Select the MedicalExamManager GameObject
2. In Inspector, configure:

**Scenario Configuration**:
- Drag your MedicalExamScenario asset to `Current Scenario`

**UI References**:
- `Role Selection Panel`: Drag the RoleSelectionPanel GameObject
- `Doctor Button`: Drag the DoctorButton
- `Patient Button`: Drag the PatientButton
- `Scenario Title Text`: Drag the ScenarioTitle TextMeshPro
- `Timer Text`: Drag the TimerText
- `Evaluation Panel`: Drag the EvaluationPanel GameObject
- `Evaluation Text`: Drag the EvaluationText TextMeshPro

**Dependencies**:
- `Conversation Manager`: Drag your RealtimeConversationManager
- `Doctor Avatar`: Drag the doctor 3D model
- `Patient Avatar`: Drag the patient 3D model

**Evaluation Settings**:
- Check `Auto Evaluate On Time End` if you want automatic evaluation
- Drag ExamHistory asset to `Exam History` (optional)

### Step 5: Configure Buttons

1. Select DoctorButton
   - Should already be configured by MedicalExamManager
   - Text: "Doctor Scenario"

2. Select PatientButton
   - Should already be configured by MedicalExamManager
   - Text: "Patient Scenario"

3. Optional: Create "End Exam" button
   - Call `MedicalExamManager.ManualEvaluationTrigger()`

4. Optional: Create "Restart" button
   - Call `MedicalExamManager.RestartExam()`

### Step 6: Test

1. Enter Play Mode
2. You should see role selection UI
3. Click "Doctor" or "Patient" button
4. The appropriate avatar appears
5. AI starts the exam conversation
6. Timer counts down
7. Say "This is all for this conversation" to end early
8. Or wait for time to run out
9. AI provides evaluation with scores
10. Evaluation panel shows results

## How It Works

### Exam Flow

1. **Role Selection**
   - User clicks Doctor or Patient button
   - Selected avatar is shown
   - Opposite avatar is hidden

2. **System Prompt Generation**
   - MedicalExamManager generates role-specific prompt
   - Includes: topic, duration, language, instructions
   - Prompt is injected into RealtimeConversationManager

3. **Conversation**
   - AI starts conversation based on role
   - Doctor role: AI asks questions
   - Patient role: AI presents symptoms
   - All conversation is logged

4. **Evaluation Trigger**
   - Automatic: When timer reaches 0
   - Manual: User says "this is all" or clicks button
   - MedicalExamManager requests evaluation

5. **Evaluation**
   - AI responds with structured evaluation
   - ExamEvaluation parses the response
   - Scores extracted: Terminologie, Verständlichkeit, Aussprache
   - Overall score calculated
   - Saved to history

6. **Display**
   - Evaluation panel shows
   - Scores displayed with color coding
   - Feedback text shown
   - Can restart or review

## System Prompts

### Doctor to Doctor Scenario
```
You are an experienced doctor conducting an oral examination.
Topic: [Medical Topic]
Duration: [X] minutes
You start by asking questions about the topic.
Follow up naturally based on answers.
Stay focused on the topic.
After time/user ending: provide evaluation with scores.
```

### Doctor to Patient Scenario
```
You are a patient with symptoms related to [Medical Topic].
The user is your doctor.
Present your symptoms naturally.
Answer doctor's questions as a patient would.
Stay in character.
After consultation: evaluate doctor's performance.
```

## Evaluation Format

The AI is instructed to provide evaluation in this format:

```
Terminologie: [X]/5
Verständlichkeit: [X]/5
Aussprache: [X]/5
Overall Score: [X]/100
Feedback: [Detailed feedback text]
```

## Language Support

Currently supports:
- **English**: Full support
- **Deutsch**: Ready (set scenario.language = Deutsch)

To switch to German:
1. Set MedicalExamScenario.language = Deutsch
2. The system prompt automatically adjusts
3. AI will conduct conversation in German

## Customization

### Change Exam Duration
- Edit MedicalExamScenario asset
- Adjust `examDurationMinutes` (3-10 range)

### Add New Scenarios
- Create new MedicalExamScenario assets
- Configure different topics
- Can have multiple scenarios loaded

### Modify Scoring Criteria
- Edit prompt generation in `MedicalExamManager.GenerateSystemPrompt()`
- Adjust evaluation instructions

### Add More Roles
- Extend `RoleType` enum
- Add new prompt generation logic
- Create additional avatars

## Debugging

### Enable Logs
All scripts use Debug.Log with prefixes:
- `[MedicalExamManager]`: Exam flow logs
- `[ExamEvaluation]`: Evaluation parsing logs

### Check Console for:
- System prompt generated
- Conversation transcripts
- Evaluation scores parsed
- Any parsing errors

### Common Issues

**AI doesn't provide evaluation**:
- Check if prompt is correctly set
- Verify conversation duration
- Ensure trigger phrases are recognized

**Scores not parsed**:
- Check Console for parsing errors
- Verify AI response format
- Check ExamEvaluation regex patterns

**Timer not working**:
- Ensure MedicalExamScenario has valid duration
- Check if exam started correctly
- Verify Update() is being called

## Future Enhancements

Potential additions:
- [ ] Multiple scenarios selectable in-app
- [ ] Scenario difficulty levels affect AI behavior
- [ ] Progress tracking dashboard
- [ ] Export evaluation reports
- [ ] Voice feedback (TTS for evaluation)
- [ ] Multiplayer (real doctor peer review)
- [ ] German language UI
- [ ] More granular scoring categories
- [ ] AI tutor mode with hints

## Files Structure

```
Assets/
├── Scripts/
│   ├── MedicalExamScenario.cs          (ScriptableObject definition)
│   ├── MedicalExamManager.cs           (Main manager)
│   ├── ExamEvaluation.cs               (Evaluation data & parsing)
│   └── EvaluationDisplayUI.cs          (UI display helper)
├── Scenarios/
│   ├── Cardiology_Basics.asset
│   ├── Neurology_Exam.asset
│   └── ...
└── ExamHistory.asset                    (Optional history tracking)
```

## Notes

- The system uses reflection to set the `systemPrompt` field in RealtimeConversationManager
- Conversation logging happens through Unity Events
- Evaluation is triggered by AI detecting specific phrases or timer
- All data is serializable for saving/loading

## Support

For issues or questions:
1. Check Console logs for errors
2. Verify all Inspector references are assigned
3. Ensure OpenAI API key is configured
4. Test with simple scenarios first

---

**Ready to use!** Follow the setup instructions and you'll have a working medical oral exam training system.
