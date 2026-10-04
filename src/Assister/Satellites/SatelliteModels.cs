namespace Assister.Satellites;

public enum VoiceOwnership { Unknown, Available, OwnedByAssister, Conflict, Unsupported }
public enum VoiceSessionState { Idle, WakeDetected, CapturingAudio, SpeechComplete, Transcribing, Routing, GeneratingResponse, Synthesizing, WaitingForPlayback, PlayingResponse, Complete, Cancelled, Disconnected, STTFailed, RoutingFailed, TTSFailed, PlaybackFailed }
public enum PlaybackKind { VoiceResponse, Announcement, Media }

// Only identity and desired configuration belong in SQLite. Credentials remain external references.
public sealed class Satellite
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? AreaId { get; set; }
    public string ProviderType { get; set; } = "ESPHome";
    public string Endpoint { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Configuration { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record SatelliteCapabilities
{
    public bool Microphone { get; init; }
    public bool MultiChannelMicrophone { get; init; }
    public bool VoiceAssistant { get; init; }
    public bool ApiAudio { get; init; }
    public bool WakeWord { get; init; }
    public bool WakeWordConfiguration { get; init; }
    public bool Speaker { get; init; }
    public bool MediaPlayer { get; init; }
    public bool MediaPlayback { get; init; }
    public bool AnnouncementPlayback { get; init; }
    public bool VolumeControl { get; init; }
    public bool MuteControl { get; init; }
    public bool Timers { get; init; }
    public bool StartConversation { get; init; }
    public bool LedFeedback { get; init; }
}

public sealed record WakeWord(string Id, string Name);
public sealed record VoiceConfiguration(WakeWord[] AvailableWakeWords, string[] ActiveWakeWords, int MaxActiveWakeWords);
public sealed record SatelliteRuntimeState
{
    public string ConnectionState { get; init; } = "Offline";
    public DateTimeOffset? LastSeen { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public string DeviceName { get; init; } = "";
    public string Model { get; init; } = "";
    public string FirmwareVersion { get; init; } = "";
    public string ESPHomeVersion { get; init; } = "";
    public string ApiVersion { get; init; } = "";
    public VoiceOwnership VoiceOwnership { get; init; }
    public SatelliteCapabilities Capabilities { get; init; } = new();
    public VoiceConfiguration VoiceConfiguration { get; init; } = new([], [], 0);
    public string? ConfigurationDrift { get; init; }
    public Guid? CurrentVoiceSessionId { get; init; }
    public Guid? ConversationId { get; init; }
    public VoiceSessionState Activity { get; init; }
    public string? WakeWord { get; init; }
    public int MicrophoneSourceChannels { get; init; } = 1;
    public string CurrentPlaybackState { get; init; } = "Unknown";
    public double? CurrentVolume { get; init; }
    public bool? MuteState { get; init; }
    public string? LastError { get; init; }
}

public sealed record SatelliteHistoryEvent(DateTimeOffset At, string Type, string? Detail, Guid? SessionId, Guid? TraceId);
