using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Events;

namespace ElevenLabs
{
    public class AgentConversationManager : MonoBehaviour
    {
        [Header("Agent Configuration")]
        [SerializeField] private string agentId = "<your_agent_id>";
        [SerializeField] private bool startOnAwake = true;

        [Header("Dependencies")]
        [SerializeField] private ElevenLabsConfig config;
        [SerializeField] private MicrophoneStreamer micStreamer;
        [SerializeField] private PcmAudioPlayer audioPlayer;

        [Header("Events")]
        public UnityEvent<float> onAgentVadScore;
        public UnityEvent<string> onAgentTranscript;
        public UnityEvent<string> onUserTranscript;
        
        private ClientWebSocket _websocket;
        private Coroutine _activityPingRoutine;

        [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
        public async void StartAgent()
        {
            try
            {
                ElevenLabsClient.Create(config);
                var wsUrl = await ElevenLabsClient.Instance.GetAgentWebsocketUrlAsync(agentId);
                await InitializeWebSocketAsync(wsUrl);
                
                micStreamer.OnAudioChunk += OnMicChunk;
            }
            catch (Exception e)
            {
                Debug.LogError($"[AgentConversationManager] Startup failed: {e}");
                enabled = false;
            }
        } 
        
        private void Start()
        {
            if (startOnAwake)
            {
                StartAgent();
            }
        }

        private void Update() { }

        private async void OnApplicationQuit()
        {
            await SafeCloseSocket();
        }
        
        private async void OnDisable()
        {
            if (_activityPingRoutine != null)
                StopCoroutine(_activityPingRoutine);

            await SafeCloseSocket();
            micStreamer.OnAudioChunk -= OnMicChunk;
        }
        
        private async void OnMicChunk(string chunk)
        {
            if (_websocket?.State != WebSocketState.Open) return;
            var payload = new Dictionary<string, object> { { "user_audio_chunk", chunk } };
            await SendWebSocketMessage(JsonConvert.SerializeObject(payload));
        }
        
        private async Task InitializeWebSocketAsync(string url)
        {
            _websocket = new ClientWebSocket();
            try
            {
                await _websocket.ConnectAsync(new Uri(url), CancellationToken.None);
                Debug.Log("WebSocket connected");
                
                await SendInitiationData();
                micStreamer.StartStreaming();
                
                _activityPingRoutine = StartCoroutine(ActivityPing());
                _ = ReceiveMessagesAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentConversationManager] WebSocket connection failed: {ex}");
                throw;
            }
        }

        private async Task ReceiveMessagesAsync()
        {
            try
            {
                byte[] buffer = new byte[4096];
                while (_websocket != null && _websocket.State == WebSocketState.Open)
                {
                    var result = await _websocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        HandleRawMessage(Encoding.UTF8.GetBytes(json));
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        Debug.Log("WebSocket closed.");
                    }
                }
            }
            catch (Exception ex)
            {
                if (_websocket?.State != WebSocketState.Closed)
                    Debug.LogError($"[AgentConversationManager] WebSocket receive error: {ex}");
            }
        }

        private async Task SendWebSocketMessage(string message)
        {
            if (_websocket == null || _websocket.State != WebSocketState.Open) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await _websocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentConversationManager] SendWebSocketMessage error: {ex.Message}");
            }
        }

        private async Task SafeCloseSocket()
        {
            if (_websocket != null && _websocket.State == WebSocketState.Open)
            {
                await _websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            }
            _websocket?.Dispose();
        }
        
        private async Task SendInitiationData()
        {
            var init = new Dictionary<string, object>
            {
                { "type", "conversation_initiation_client_data" }
            };
            await SendWebSocketMessage(JsonConvert.SerializeObject(init));
        }

        private IEnumerator ActivityPing()
        {
            var json = JsonConvert.SerializeObject(new Dictionary<string, object> { { "type", "user_activity" } });
            while (_websocket is { State: WebSocketState.Open })
            {
                _ = SendWebSocketMessage(json);
                yield return new WaitForSecondsRealtime(20f);
            }
        }
        
        private void HandleRawMessage(byte[] bytes)
        {
            HandleMessage(Encoding.UTF8.GetString(bytes));
        }

        private async void HandleMessage(string message)
        {
            var eventPayload = JsonConvert.DeserializeObject<BaseEvent>(message);

            switch (eventPayload.Type)
            {
                case "ping":
                    await HandlePingEvent(message);
                    break;
                case "audio":
                    HandleAudioEvent(message);
                    break;
                case "user_transcript":
                    HandleUserTranscriptEvent(message);
                    break;
                case "agent_response":
                    HandleAgentResponseEvent(message);
                    break;
                case "vad_score":
                    HandleAgentVadScoreEvent(message);
                    break;
                case "interruption":
                    audioPlayer.StopImmediately();
                    break;
                default:
                    Debug.Log($"Unhandled event type: {eventPayload.Type}");
                    break;
            }
        }

        private async Task HandlePingEvent(string msg)
        {
            var ping = JsonConvert.DeserializeObject<PingEvent>(msg);

            var delay   = ping.PingEventData?.PingMs  ?? 0;
            var eventId = ping.PingEventData?.EventId ?? 0;
            if (delay > 0) await Task.Delay(delay);

            var pong = new Dictionary<string, object>
            {
                { "type", "pong" },
                { "event_id", eventId }
            };
            await SendWebSocketMessage(JsonConvert.SerializeObject(pong));
        }

        private void HandleAudioEvent(string msg)
        {
            var ar = JsonConvert.DeserializeObject<AudioResponseEvent>(msg);
            if (!string.IsNullOrEmpty(ar.AudioEvent?.AudioBase64))
            {
                audioPlayer.EnqueueBase64Audio(ar.AudioEvent.AudioBase64);
            }
        }

        private void HandleUserTranscriptEvent(string msg)
        {
            var data = JsonConvert.DeserializeObject<UserTranscriptEvent>(msg);
            var transcript = data.UserTranscriptionEvent?.UserTranscript;
            if (!string.IsNullOrEmpty(transcript))
            {
                onUserTranscript?.Invoke(transcript);    
            }
        }
        
        private void HandleAgentResponseEvent(string msg)
        {
            var data = JsonConvert.DeserializeObject<AgentResponseEvent>(msg);
            var transcript = data.AgentResponseEventData?.AgentResponse;
            if (!string.IsNullOrEmpty(transcript))
            {
                onAgentTranscript?.Invoke(transcript);    
            }
        }

        private void HandleAgentVadScoreEvent(string msg)
        {
            var data = JsonConvert.DeserializeObject<VadScoreEvent>(msg);
            var vadScore = data.VadScoreEventData?.VadScore;
            if (vadScore.HasValue)
            {
                onAgentVadScore?.Invoke(vadScore.Value);
            }
        }
    }
}
