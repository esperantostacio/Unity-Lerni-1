# Microphone & Conversation Setup Guide

## Issue: Microphone Not Working / AI Not Hearing You

### Quick Check - RealtimeConversationManager Settings

Select your `RealtimeConversationManager` GameObject and verify:

1. **OpenAI Config** is assigned ✅
2. **Microphone Streamer** is assigned ✅  
3. **Pcm Audio Player** is assigned ✅
4. **Start On Awake** is **UNCHECKED** ❌ (MedicalExamManager starts it instead)
5. **API Key** is set in OpenAIConfig asset ✅

### How Conversation Flow Works

```
User clicks role button
    ↓
MedicalExamManager.OnRoleSelected()
    ↓
Generates system prompt
    ↓
Calls conversationManager.StartAgent()
    ↓
RealtimeConversationManager:
    - Connects to OpenAI WebSocket
    - Starts microphone
    - Begins listening
    ↓
Microphone captures audio
    ↓
Sends to OpenAI Realtime API
    ↓
AI responds with audio
    ↓
Plays through PcmAudioPlayer
```

## Troubleshooting Steps

### 1. Check Microphone Permissions

**Windows:**
- Settings → Privacy → Microphone
- Enable "Allow apps to access your microphone"
- Enable for Unity Editor

**In Unity:**
- Check Console for microphone errors
- Look for "Microphone not found" or "Permission denied"

### 2. Verify MicrophoneStreamer

Select `RealtimeConversationManager` → Find `MicrophoneStreamer` component:

```
MicrophoneStreamer Settings:
- Device Name: (leave empty for default mic)
- Sample Rate: 16000 or 24000
- Chunk Size: 4096
```

### 3. Check OpenAI API Connection

In Console, when exam starts you should see:
```
[OpenAI] Starting realtime agent...
[OpenAI] WebSocket connected
[MedicalExamManager] Starting exam...
```

If you see errors:
- `API Key is not set` → Set key in OpenAIConfig
- `Connection failed` → Check internet
- `401 Unauthorized` → Wrong API key

### 4. Test Microphone Separately

Add this test button to your scene:

**Create Test Button:**
```
1. Hierarchy → Right-click → UI → Button
2. Name: "Test Microphone"
3. Add this code to a test script:

public void TestMicrophone()
{
    string[] devices = Microphone.devices;
    Debug.Log($"Found {devices.Length} microphone(s):");
    foreach (string device in devices)
    {
        Debug.Log($"  - {device}");
    }
    
    if (devices.Length > 0)
    {
        AudioClip clip = Microphone.Start(null, true, 1, 16000);
        Debug.Log("Microphone started!");
    }
}
```

### 5. Verify Conversation Events

MedicalExamManager subscribes to these events:
```csharp
conversationManager.onAgentTranscript.AddListener(OnAgentSpoke);
conversationManager.onUserTranscript.AddListener(OnUserSpoke);
```

**Check in Console:**
- `[MedicalExamManager] Agent: [text]` → AI spoke
- `[MedicalExamManager] User: [text]` → You spoke

If you don't see "User:" logs, microphone isn't capturing!

### 6. Common Issues

**Issue: No audio input detected**
- Check Windows Sound Settings → Input → Test microphone
- Try different microphone
- Restart Unity Editor

**Issue: AI doesn't respond to questions**
- System prompt might not be set correctly
- Check [MedicalExamManager] logs for prompt
- Verify role selection triggered StartExam()

**Issue: Can hear AI but AI can't hear me**
- Microphone permissions blocked
- Wrong microphone selected
- MicrophoneStreamer not streaming

**Issue: Conversation starts but stops immediately**
- Check OpenAI API quota/billing
- Check for WebSocket disconnection errors
- API rate limit might be hit

### 7. Debug Mode

Enable detailed logging:

In `RealtimeConversationManager.cs`, add this at the start of Update():
```csharp
void Update()
{
    _ws?.DispatchMessageQueue();
    
    // Debug: Check if receiving mic data
    if (micStreamer != null && micStreamer.IsStreaming)
    {
        Debug.Log($"[Debug] Mic streaming: {micStreamer.IsStreaming}");
    }
}
```

### 8. Manual Test Without Role Selection

In MedicalExamDebugger window:
1. Press Play
2. Click "Start Conversation" button
3. Check Console for errors
4. Speak and watch for "User:" logs

## What We Changed (Nothing breaks microphone)

✅ We added MedicalExamManager that **calls** StartAgent()
✅ We inject system prompts via reflection
✅ We track conversation for evaluation

❌ We did NOT change:
- MicrophoneStreamer
- Audio capture logic
- OpenAI connection code
- WebSocket handling

## Expected Behavior

**When working correctly:**
1. Click role button
2. See: `[MedicalExamManager] Starting exam`
3. See: `[OpenAI] Starting realtime agent...`
4. Hear AI start talking (first greeting)
5. Speak your response
6. See: `[MedicalExamManager] User: [your words]`
7. AI responds
8. See: `[MedicalExamManager] Agent: [AI response]`

## Test With Debugger

1. Open: `Medical Exam → Debugger Window`
2. Press Play
3. Select scenario
4. Click "Doctor to Doctor"
5. **Immediately check Console for errors**
6. Speak: "Hello, test test"
7. Should see: `[MedicalExamManager] User: Hello, test test`

If you don't see that last line, microphone is not working!

## Quick Fix Steps

1. **Check mic is plugged in** 🎤
2. **Windows mic permissions** ✅
3. **RealtimeConversationManager has all references** ✅
4. **OpenAI API key is set** 🔑
5. **Start On Awake is OFF** ❌
6. **Restart Unity** if needed 🔄

---

**Still not working?**
- Check Console for exact error messages
- Test with original OpenAI sample scene (without our medical exam code)
- Try different microphone
- Check OpenAI API dashboard for request logs
