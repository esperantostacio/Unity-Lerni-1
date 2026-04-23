# Performance Bottleneck Analysis - Conversation Flow

## Current Flow (Slow):
```
User speaks 
  ↓
Whisper transcription (API call, ~2-3s)
  ↓
GPT response (API call, ~1-2s)
  ↓
TTS MP3 generation (API call, ~0.5-1s)
  ↓
MP3 → temp file write
  ↓
MP3 file load via UnityWebRequest
  ↓
AudioClip decode
  ↓
Audio plays
```

## Identified Bottlenecks:

### 1. **MP3 File I/O (ElevenLabsTTS.cs)**
   - **Issue**: Writing MP3 data to temp file, then reading it back with UnityWebRequest
   - **Overhead**: ~200-500ms file I/O + memory overhead
   - **Fix**: Stream MP3 directly to AudioClip without file I/O

### 2. **Audio Encoding Overhead**
   - **Issue**: MP3 needs decoding by AudioType.MPEG (slow)
   - **Alternative**: Use WAV format (PCM audio, no decoding needed) or keep MP3 but cache decoder

### 3. **Sequential Processing**
   - **Issue**: Each step waits for completion before next starts
   - **Fix**: Parallel processing where possible

### 4. **Unnecessary Waits**
   - Line 1801: `yield return new WaitForSeconds(0.2f)` - buffer flush wait
   - Line 3518: `yield return new WaitForSeconds(0.3f)` - evaluation display wait
   - Multiple recording/AI indicator toggle delays

### 5. **MP3 Decoding Bottleneck**
   - OpenAI returns MP3, Unity must decode MP3 → PCM at runtime
   - Solution: Request WAV format instead (no decoding needed)
   - OR: Use NativeAudio plugin for faster MP3 decoding

## Quick Wins (Easy & Fast):

1. **Remove file I/O entirely** - Stream MP3 directly or use WAV
2. **Request WAV instead of MP3** from OpenAI (tts-1 supports it)
3. **Eliminate unnecessary WaitForSeconds** delays
4. **Parallel recording UI indicators** instead of sequential

## Optimization Strategy (Priority Order):

1. ✅ **HIGH IMPACT**: Replace MP3 file I/O with direct NativeAudio playback or WAV streaming
2. ✅ **MEDIUM IMPACT**: Change OpenAI TTS response format from MP3 to WAV (faster, no decoding)
3. ✅ **LOW IMPACT**: Remove artificial delays (0.2s, 0.3s waits)
4. ✅ **OPTIMIZATION**: Cache TTS requests (same text = reuse audio)
