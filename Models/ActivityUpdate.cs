namespace ClaudePetOverlay.Models;

public sealed record ActivityUpdate(
    PetState State,
    string Source,
    string? ThreadId = null,
    string? Message = null,
    bool ShowInSpeechBubble = true,
    string? TaskName = null,
    DateTimeOffset? StartedAt = null,
    int ActiveTaskCount = 0,
    DateTimeOffset? EndedAt = null);
