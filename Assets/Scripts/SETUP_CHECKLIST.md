# Medical Exam VR - Setup Checklist

## ✅ Pre-Setup Verification

Before you begin, verify you have:

- [ ] Unity 2023.x or later installed
- [ ] OpenAI API key configured in OpenAIConfig asset
- [ ] Existing RealtimeConversationManager working
- [ ] VR headset (Quest or compatible) OR testing in editor
- [ ] TextMeshPro package imported
- [ ] XR Interaction Toolkit (if using VR)

---

## 📦 Files Created - Verification

Check that these files exist in your project:

### Core Scripts (Assets/Scripts/)
- [ ] `MedicalExamScenario.cs`
- [ ] `MedicalExamManager.cs`
- [ ] `ExamEvaluation.cs`
- [ ] `EvaluationDisplayUI.cs`

### Editor Tools (Assets/Scripts/Editor/)
- [ ] `ScenarioCreator.cs`

### Updated Files
- [ ] `Assets/GameManager.cs` (updated)

### Documentation (Assets/Scripts/)
- [ ] `MEDICAL_EXAM_README.md`
- [ ] `QUICK_START.md`
- [ ] `IMPLEMENTATION_SUMMARY.md`
- [ ] `ARCHITECTURE.md`
- [ ] `SETUP_CHECKLIST.md` (this file)

---

## 🎯 Setup Steps

### Step 1: Create Scenario Assets ⏱️ 2 min

Using Editor Window (Recommended):
- [ ] Open Unity
- [ ] Go to menu: `Medical Exam → Scenario Creator`
- [ ] Click a preset button (e.g., "Cardiology")
- [ ] Click "Create Scenario"
- [ ] Verify asset created in `Assets/Scenarios/`

OR Manually:
- [ ] Create folder `Assets/Scenarios/`
- [ ] Right-click → Create → Medical Exam → Scenario
- [ ] Name it `Cardiology_Basics`
- [ ] Configure in Inspector:
  - [ ] Scenario Name: "Cardiology Basics"
  - [ ] Medical Topic: "Basic cardiovascular examination..."
  - [ ] Duration: 5 minutes
  - [ ] Language: English

### Step 2: Create Exam History ⏱️ 30 sec

- [ ] In Project window, navigate to `Assets/`
- [ ] Right-click → Create → Medical Exam → Exam History
- [ ] Name it `ExamHistory`
- [ ] (Optional) Select and view in Inspector

### Step 3: Create Scene Objects ⏱️ 3 min

#### GameManager:
- [ ] Hierarchy → Right-click → Create Empty
- [ ] Name: `GameManager`
- [ ] Add Component → `GameManager.cs`

#### MedicalExamManager:
- [ ] Hierarchy → Right-click → Create Empty
- [ ] Name: `MedicalExamManager`
- [ ] Add Component → `MedicalExamManager.cs`

### Step 4: Create UI ⏱️ 5 min

#### Canvas:
- [ ] Hierarchy → Right-click → UI → Canvas
- [ ] Canvas Scaler: Scale with Screen Size (if not already)
- [ ] Reference Resolution: 1920x1080

#### Role Selection Panel:
- [ ] Under Canvas → Right-click → UI → Panel
- [ ] Name: `RoleSelectionPanel`
- [ ] Anchor: Center
- [ ] Size: 600x400

**Children of RoleSelectionPanel:**

Title Text:
- [ ] Right-click RoleSelectionPanel → UI → TextMeshPro - Text
- [ ] Name: `Title`
- [ ] Text: "Select Your Role"
- [ ] Font Size: 48
- [ ] Alignment: Center, Middle
- [ ] Position: Top

Scenario Title:
- [ ] Right-click RoleSelectionPanel → UI → TextMeshPro - Text
- [ ] Name: `ScenarioTitle`
- [ ] Text: "" (will be set by script)
- [ ] Font Size: 32
- [ ] Position: Below title

Doctor Button:
- [ ] Right-click RoleSelectionPanel → UI → Button - TextMeshPro
- [ ] Name: `DoctorButton`
- [ ] Button text: "Doctor Exam"
- [ ] Size: 250x80
- [ ] Position: Center-left

Patient Button:
- [ ] Right-click RoleSelectionPanel → UI → Button - TextMeshPro
- [ ] Name: `PatientButton`
- [ ] Button text: "Patient Consultation"
- [ ] Size: 250x80
- [ ] Position: Center-right

#### Exam HUD:
- [ ] Under Canvas → Right-click → UI → Panel
- [ ] Name: `ExamHUD`
- [ ] Anchor: Top-Right
- [ ] Size: 200x80
- [ ] Make semi-transparent

Timer Text:
- [ ] Right-click ExamHUD → UI → TextMeshPro - Text
- [ ] Name: `TimerText`
- [ ] Text: "Time: 05:00"
- [ ] Font Size: 36
- [ ] Alignment: Center, Middle

#### Evaluation Panel:
- [ ] Under Canvas → Right-click → UI → Panel
- [ ] Name: `EvaluationPanel`
- [ ] Anchor: Center
- [ ] Size: 800x600
- [ ] **IMPORTANT**: Uncheck "Active" in Inspector (initially hidden)

**Children of EvaluationPanel:**

Background:
- [ ] Already has background (from Panel)
- [ ] Make semi-transparent dark

Evaluation Text:
- [ ] Right-click EvaluationPanel → UI → TextMeshPro - Text
- [ ] Name: `EvaluationText`
- [ ] Text: "" (will be set by script)
- [ ] Font Size: 24
- [ ] Enable Rich Text
- [ ] Add Scroll View if needed
- [ ] Size: 760x500
- [ ] Position: Top

Restart Button:
- [ ] Right-click EvaluationPanel → UI → Button - TextMeshPro
- [ ] Name: `RestartButton`
- [ ] Button text: "Restart Exam"
- [ ] Size: 200x60
- [ ] Position: Bottom

### Step 5: Connect References ⏱️ 5 min

#### GameManager Configuration:
- [ ] Select `GameManager` GameObject
- [ ] In Inspector, drag `MedicalExamManager` to field

#### MedicalExamManager Configuration:

**Scenario Configuration:**
- [ ] Drag `Cardiology_Basics` asset to `Current Scenario`

**UI References:**
- [ ] Drag `RoleSelectionPanel` to `Role Selection Panel`
- [ ] Drag `DoctorButton` to `Doctor Button`
- [ ] Drag `PatientButton` to `Patient Button`
- [ ] Drag `ScenarioTitle` (TextMeshPro) to `Scenario Title Text`
- [ ] Drag `TimerText` to `Timer Text`
- [ ] Drag `EvaluationPanel` to `Evaluation Panel`
- [ ] Drag `EvaluationText` to `Evaluation Text`

**Dependencies:**
- [ ] Drag your `RealtimeConversationManager` GameObject to `Conversation Manager`
- [ ] Drag your Doctor 3D model/avatar to `Doctor Avatar`
- [ ] Drag your Patient 3D model/avatar to `Patient Avatar`

**Evaluation Settings:**
- [ ] Check `Auto Evaluate On Time End` (if desired)
- [ ] Drag `ExamHistory` asset to `Exam History`

#### RestartButton Configuration:
- [ ] Select `RestartButton`
- [ ] In Button component, find `OnClick()` section
- [ ] Click `+` to add event
- [ ] Drag `MedicalExamManager` GameObject to object field
- [ ] Select function: `MedicalExamManager → RestartExam()`

### Step 6: Avatar Setup ⏱️ 2 min

Ensure you have avatars in scene:

Doctor Avatar:
- [ ] Has 3D model
- [ ] Has `NPCDoctor.cs` or similar
- [ ] Has Animator component
- [ ] Initially **Active** OR will be activated by script

Patient Avatar:
- [ ] Has 3D model
- [ ] Has animation/lip sync
- [ ] Initially **Active** OR will be activated by script

- [ ] Both avatars referenced in MedicalExamManager

### Step 7: Verify RealtimeConversationManager ⏱️ 1 min

- [ ] `RealtimeConversationManager` GameObject exists
- [ ] Has `OpenAIConfig` assigned
- [ ] Has `MicrophoneStreamer` assigned
- [ ] Has `PcmAudioPlayer` assigned
- [ ] `Start On Awake` is **UNCHECKED** (MedicalExamManager will start it)
- [ ] Has correct API key in OpenAIConfig

---

## 🧪 Testing Phase

### Pre-Flight Checks:
- [ ] All pink/missing references resolved in Inspector
- [ ] Console shows no errors
- [ ] Evaluation Panel starts disabled
- [ ] Avatars are in scene

### Test 1: UI Functionality
- [ ] Press Play
- [ ] Role Selection Panel appears
- [ ] Scenario title shows correct name
- [ ] Doctor button visible and clickable
- [ ] Patient button visible and clickable

### Test 2: Doctor Scenario
- [ ] Click "Doctor Exam" button
- [ ] Role selection panel disappears
- [ ] Doctor avatar appears
- [ ] Timer starts counting down
- [ ] Check Console: System prompt logged
- [ ] AI starts talking (examiner questions)
- [ ] Speak response
- [ ] Check Console: Conversation logged

### Test 3: Conversation Flow
- [ ] AI asks medical questions
- [ ] Responds to your answers
- [ ] Stays on topic
- [ ] Timer updates
- [ ] Timer changes color as time decreases

### Test 4: Evaluation Trigger - Time
- [ ] Let timer run to 0 OR
- [ ] Say "This is all for this conversation"
- [ ] AI responds with evaluation
- [ ] Evaluation panel appears
- [ ] Scores displayed
- [ ] Feedback text shown

### Test 5: Evaluation Content
- [ ] Terminologie score shown (X/5)
- [ ] Verständlichkeit score shown (X/5)
- [ ] Aussprache score shown (X/5)
- [ ] Overall score shown (X/100)
- [ ] Feedback text readable

### Test 6: Restart
- [ ] Click "Restart Exam" button
- [ ] Evaluation panel disappears
- [ ] Role selection panel reappears
- [ ] Timer resets
- [ ] Can select role again

### Test 7: Patient Scenario
- [ ] Restart if needed
- [ ] Click "Patient Consultation" button
- [ ] Patient avatar appears
- [ ] AI acts as patient
- [ ] AI presents symptoms
- [ ] Ask diagnostic questions
- [ ] AI responds as patient
- [ ] End conversation
- [ ] Receive evaluation

### Test 8: Exam History
- [ ] Complete at least 2 exams
- [ ] Select `ExamHistory` asset in Project
- [ ] View in Inspector
- [ ] Verify evaluations list populated
- [ ] Check scores are saved

### Test 9: German Language (Optional)
- [ ] Create German scenario OR
- [ ] Modify existing scenario
- [ ] Set Language to "Deutsch"
- [ ] Start exam
- [ ] AI speaks German
- [ ] Evaluation in German

---

## 🐛 Troubleshooting

### Issue: Buttons Don't Respond
- [ ] Check MedicalExamManager has Start() method running
- [ ] Verify button references assigned
- [ ] Check Console for errors
- [ ] Verify EventSystem exists in scene

### Issue: AI Doesn't Start
- [ ] Check OpenAIConfig has API key
- [ ] Verify RealtimeConversationManager assigned
- [ ] Check "Start On Awake" is OFF
- [ ] Check Console for OpenAI errors
- [ ] Verify internet connection

### Issue: No Evaluation
- [ ] Check Console for AI response
- [ ] Verify evaluation format in AI response
- [ ] Check ExamEvaluation parsing regex
- [ ] Try manual trigger: Press 'T' key

### Issue: Timer Not Showing
- [ ] Verify TimerText assigned in Inspector
- [ ] Check ExamHUD panel is active
- [ ] Check scenario has valid duration
- [ ] Verify _examActive is true (debug)

### Issue: Wrong Avatar Shows
- [ ] Check avatar assignments in Inspector
- [ ] Verify avatars exist and aren't destroyed
- [ ] Check OnRoleSelected() logic
- [ ] Debug.Log which role was selected

### Issue: Evaluation Panel Won't Show
- [ ] Check evaluationPanel reference assigned
- [ ] Verify ProcessEvaluation() is called
- [ ] Check panel isn't behind other UI
- [ ] Verify Canvas rendering mode

### Issue: Scores Show 0/0
- [ ] Check AI evaluation format
- [ ] Console should show parsing errors
- [ ] Verify regex patterns in ExamEvaluation
- [ ] Check AI prompt includes evaluation format

---

## 📊 Performance Checklist

- [ ] No errors in Console
- [ ] Frame rate stable (60+ FPS)
- [ ] Audio latency acceptable
- [ ] UI responsive
- [ ] No memory leaks (profile over time)
- [ ] VR tracking smooth (if applicable)

---

## 🚀 Deployment Checklist

Before building:

- [ ] All testing passed
- [ ] No Console errors/warnings
- [ ] Scenarios configured correctly
- [ ] UI scales properly
- [ ] VR controls mapped (if applicable)
- [ ] OpenAI API key embedded OR
- [ ] API key input implemented

Build Settings:
- [ ] Scene added to Build Settings
- [ ] Platform selected (PC, Android/Quest, etc.)
- [ ] XR settings configured
- [ ] Build and run successful

Post-Build:
- [ ] Test on target device
- [ ] Verify API connectivity
- [ ] Test all scenarios
- [ ] Test both roles
- [ ] Performance acceptable

---

## 📝 Documentation Review

Have you read:
- [ ] `QUICK_START.md` - Quick setup guide
- [ ] `MEDICAL_EXAM_README.md` - Full documentation
- [ ] `ARCHITECTURE.md` - System architecture
- [ ] `IMPLEMENTATION_SUMMARY.md` - Technical overview

---

## ✨ Enhancement Ideas (Optional)

After basic setup works, consider:

- [ ] Create more scenarios (use Scenario Creator)
- [ ] Add score visualization (bars/charts)
- [ ] Implement EvaluationDisplayUI for better UI
- [ ] Add sound effects for UI interactions
- [ ] Add haptic feedback (VR controllers)
- [ ] Create German UI translations
- [ ] Add progress tracking dashboard
- [ ] Export evaluation reports (PDF/CSV)
- [ ] Add voice feedback (TTS for scores)
- [ ] Implement scenario selector UI

---

## 🎓 Final Verification

System is ready when:

- [✅] All steps completed
- [✅] All tests passed
- [✅] No errors in Console
- [✅] Can complete full exam flow
- [✅] Evaluation displays correctly
- [✅] Can restart and repeat

---

## 📞 Support

If stuck:

1. **Check Console**: Most issues show error messages
2. **Review Documentation**: Read the relevant .md files
3. **Debug Logs**: Enable detailed logging in scripts
4. **Verify References**: All Inspector fields assigned
5. **Test Incrementally**: Test each component separately

---

**Estimated Total Setup Time: 15-20 minutes**

**Ready to train!** 🎉👨‍⚕️

---

## Completion Signature

Setup completed by: ________________  
Date: ________________  
Unity Version: ________________  
Notes: ________________________________
