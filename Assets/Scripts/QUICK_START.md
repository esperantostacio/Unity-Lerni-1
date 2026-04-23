# Quick Start Guide - Medical Oral Exam VR

## 🚀 Quick Setup (5 Minutes)

### Step 1: Create Your First Scenario (1 min)
1. In Unity Project window, navigate to `Assets/Scenarios/` (create folder if needed)
2. Right-click → **Create → Medical Exam → Scenario**
3. Name it: `Cardiology_Basics`
4. Select it and configure in Inspector:
   - Scenario Name: `Cardiology Basics`
   - Medical Topic: `Basic cardiovascular examination and common cardiac conditions`
   - Exam Duration: `5` minutes
   - Language: `English`

### Step 2: Create Exam History (30 sec)
1. In Unity Project window, navigate to `Assets/`
2. Right-click → **Create → Medical Exam → Exam History**
3. Name it: `ExamHistory`

### Step 3: Setup Scene Objects (2 min)

#### Create MedicalExamManager GameObject:
1. Hierarchy → Right-click → **Create Empty**
2. Name it: `MedicalExamManager`
3. Add Component → Search for `MedicalExamManager` script
4. Configure in Inspector (drag & drop):
   - **Current Scenario**: Drag `Cardiology_Basics` asset
   - **Conversation Manager**: Drag your existing `RealtimeConversationManager` object
   - **Doctor Avatar**: Drag your doctor 3D model
   - **Patient Avatar**: Drag your patient 3D model
   - **Exam History**: Drag `ExamHistory` asset

### Step 4: Create Basic UI (2 min)

#### Canvas Setup:
```
Canvas (Screen Space - Overlay)
├── RoleSelectionPanel (Panel)
│   ├── Title (TextMeshPro): "Select Your Role"
│   ├── ScenarioTitle (TextMeshPro): Will show scenario name
│   ├── DoctorButton (Button): Text = "Doctor Exam"
│   └── PatientButton (Button): Text = "Patient Consultation"
├── ExamHUD (Panel - Top Right)
│   └── TimerText (TextMeshPro): "Time: 00:00"
└── EvaluationPanel (Panel - Center)
    ├── Background (Image - semi-transparent)
    ├── EvaluationText (TextMeshPro - Scrollable)
    └── RestartButton (Button): Text = "Restart Exam"
```

#### Link UI to MedicalExamManager:
Select `MedicalExamManager` GameObject, drag UI elements to Inspector fields:
- Role Selection Panel → `RoleSelectionPanel` Panel
- Doctor Button → `DoctorButton` Button
- Patient Button → `PatientButton` Button
- Scenario Title Text → `ScenarioTitle` TextMeshPro
- Timer Text → `TimerText` TextMeshPro
- Evaluation Panel → `EvaluationPanel` Panel
- Evaluation Text → `EvaluationText` TextMeshPro

Set Evaluation Panel initially inactive (uncheck in Inspector)

#### Link RestartButton:
- Select `RestartButton`
- In Button component, add OnClick event
- Drag `MedicalExamManager` GameObject
- Select function: `MedicalExamManager → RestartExam()`

### Step 5: Configure GameManager (30 sec)
1. Create empty GameObject named `GameManager`
2. Add `GameManager.cs` script
3. Drag `MedicalExamManager` to its inspector field

### Step 6: Test! (30 sec)
1. Press **Play**
2. Click **Doctor Exam** or **Patient Consultation**
3. Start talking!
4. Say "This is all for this conversation" to end
5. See your evaluation scores

## ✨ You're Done!

Your medical oral exam system is now ready to use.

---

## 🎯 Testing Checklist

- [ ] Role selection buttons appear on start
- [ ] Clicking button hides role selection, shows avatar
- [ ] AI starts conversation
- [ ] Timer counts down
- [ ] Conversation is logged (check Console)
- [ ] Saying "this is all" triggers evaluation
- [ ] OR timer reaching 0 triggers evaluation
- [ ] Evaluation panel appears with scores
- [ ] Restart button works

## 🐛 Troubleshooting

**Issue**: Buttons don't work
- **Fix**: Check button onClick events are assigned in MedicalExamManager.cs Start()

**Issue**: AI doesn't start talking
- **Fix**: Verify RealtimeConversationManager is assigned and has API key

**Issue**: No evaluation shown
- **Fix**: Check Console logs for parsing errors, verify AI response format

**Issue**: Timer not showing
- **Fix**: Ensure TimerText is assigned in Inspector

**Issue**: Avatar doesn't appear
- **Fix**: Verify avatar GameObjects are assigned and not disabled in scene

## 📋 Keyboard Shortcuts

- **R**: Restart exam
- **ESC**: Quit application (in build)
- **T**: Manual evaluation trigger (debug key in RealtimeConversationManager)

## 🎨 UI Customization Tips

### Make It Look Better:
1. **Role Selection Panel**:
   - Add background image
   - Style buttons with rounded corners
   - Add icons (doctor/patient symbols)

2. **Timer**:
   - Increase font size
   - Add glow effect
   - Animate when low time

3. **Evaluation Panel**:
   - Add score bars (use EvaluationDisplayUI.cs)
   - Color code scores (green/yellow/red)
   - Add animations on show

### Using EvaluationDisplayUI (Advanced):
1. Add `EvaluationDisplayUI` component to Evaluation Panel
2. Create UI for individual score displays:
   ```
   EvaluationPanel
   ├── Scores
   │   ├── TerminologieScore (Text + Bar)
   │   ├── VerstaendlichkeitScore (Text + Bar)
   │   └── AusspracheScore (Text + Bar)
   ├── OverallScore (Large Text + Bar)
   └── FeedbackText (Scrollable Text)
   ```
3. Assign fields in EvaluationDisplayUI Inspector
4. In MedicalExamManager, get EvaluationDisplayUI component and call:
   ```csharp
   evaluationDisplayUI.DisplayEvaluation(_currentEvaluation);
   ```

## 🌍 Switching to German

1. Select your MedicalExamScenario asset
2. Change `Language` dropdown to `Deutsch`
3. Update UI text labels to German:
   - "Wählen Sie Ihre Rolle"
   - "Doktor Prüfung"
   - "Patientengespräch"
   - "Zeit"
   - "Neustart"
4. AI will automatically conduct exam in German!

## 📦 Creating More Scenarios

Create multiple scenarios for different topics:

### Example Topics:
- **Cardiology**: `Cardiovascular examination and common cardiac conditions`
- **Neurology**: `Neurological examination and common neurological disorders`
- **Pediatrics**: `Pediatric examination and common childhood diseases`
- **Emergency**: `Emergency assessment and acute care management`
- **Anamnesis**: `Patient history taking and communication skills`

### For Each Topic:
1. Create new MedicalExamScenario asset
2. Configure details
3. Assign to MedicalExamManager before starting

### Future Enhancement:
Add scenario selector UI to switch between scenarios in-app!

## 📊 Tracking Progress

The `ExamHistory` asset automatically tracks all your exams:
- View in Inspector to see past evaluations
- See average scores
- Filter by scenario or role type

### Access History:
```csharp
// In your own script:
ExamHistory history = Resources.Load<ExamHistory>("ExamHistory");
float avgScore = history.GetAverageOverallScore();
List<ExamEvaluation> cardioExams = history.GetEvaluationsByScenario("Cardiology Basics");
```

---

**Ready to Practice!** 🎓

Start with English, master the basics, then switch to Deutsch for authentic German medical exam training!
