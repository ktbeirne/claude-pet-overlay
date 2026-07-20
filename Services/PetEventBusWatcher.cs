using System.IO;
using System.Text.Json;
using ClaudePetOverlay.Models;

namespace ClaudePetOverlay.Services;

public sealed class PetEventBusWatcher : IDisposable
{
    // Codex ペットの pet-events.jsonl とは分離した Claude 専用バス。
    private const string EventFileName = "claude-pet-events.jsonl";

    private readonly string _eventPath;
    private FileSystemWatcher? _watcher;
    private long _offset;
    private string _remainder = string.Empty;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PetEventBusWatcher()
    {
        // フック (claude_pet_hook.py) と同じ規則で解決する: 既定は
        // <ユーザープロファイル>\.agent-activity、環境変数 CLAUDE_PET_ACTIVITY_DIR
        // があれば両者ともそちらを使う (片側だけ上書きされるとバスがズレるため)。
        var overrideDirectory = Environment.GetEnvironmentVariable("CLAUDE_PET_ACTIVITY_DIR");
        var directory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".agent-activity")
            : overrideDirectory;
        Directory.CreateDirectory(directory);
        _eventPath = Path.Combine(directory, EventFileName);
        if (!File.Exists(_eventPath))
        {
            using var _ = File.Create(_eventPath);
        }
        _offset = new FileInfo(_eventPath).Length;
    }

    public event Action<ActivityUpdate>? ActivityChanged;

    public void Start()
    {
        var directory = Path.GetDirectoryName(_eventPath)!;
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(_eventPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => _ = ReadAsync();
    }

    private async Task ReadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Delay(40).ConfigureAwait(false);
            await using var stream = new FileStream(
                _eventPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _offset)
            {
                _offset = 0;
                _remainder = string.Empty;
            }
            stream.Seek(_offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync().ConfigureAwait(false);
            _offset = stream.Position;
            if (text.Length == 0)
            {
                return;
            }

            text = _remainder + text;
            var endsWithNewLine = text.EndsWith('\n');
            var lines = text.Split('\n');
            _remainder = endsWithNewLine ? string.Empty : lines[^1];
            var lineCount = lines.Length - 1;
            for (var index = 0; index < lineCount; index++)
            {
                ProcessLine(lines[index].TrimEnd('\r'));
            }
        }
        catch (IOException)
        {
            // A later change notification retries the event.
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ProcessLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var stateText = root.TryGetProperty("state", out var stateValue) ? stateValue.GetString() : null;
            if (!TryParseState(stateText, out var state))
            {
                return;
            }

            var source = root.TryGetProperty("source", out var sourceValue)
                ? sourceValue.GetString() ?? "External"
                : "External";
            var threadId = ReadString(root, "threadId", "thread_id");
            var message = ReadString(root, "message");
            var taskName = ReadString(root, "taskName", "task_name");
            var startedAt = ReadTimestamp(root, "startedAt", "started_at");
            var endedAt = ReadTimestamp(root, "endedAt", "ended_at");
            if (endedAt is null && state is PetState.Jumping or PetState.Failed)
            {
                endedAt = ReadTimestamp(root, "timestamp");
            }
            var activeTaskCount = ReadInt(root, "activeTaskCount", "active_task_count");
            var showInSpeechBubble = ReadBool(root, true, "showInSpeechBubble", "show_in_speech_bubble");
            ActivityChanged?.Invoke(new ActivityUpdate(
                state,
                source,
                threadId,
                message,
                showInSpeechBubble,
                taskName,
                startedAt,
                activeTaskCount,
                endedAt));
        }
        catch (JsonException)
        {
            // Ignore malformed external events.
        }
    }

    private static bool TryParseState(string? value, out PetState state)
    {
        var normalized = value?.Replace("_", "-").Trim().ToLowerInvariant();
        state = normalized switch
        {
            "idle" => PetState.Idle,
            "running" or "working" => PetState.Running,
            "running-right" or "move-right" => PetState.RunningRight,
            "running-left" or "move-left" => PetState.RunningLeft,
            "waving" or "wave" => PetState.Waving,
            "jumping" or "ready" or "complete" => PetState.Jumping,
            "failed" or "blocked" or "error" => PetState.Failed,
            "waiting" or "needs-input" => PetState.Waiting,
            "review" or "reviewing" => PetState.Review,
            _ => PetState.Idle,
        };
        return normalized is "idle" or "running" or "working" or "running-right" or "move-right"
            or "running-left" or "move-left" or "waving" or "wave" or "jumping" or "ready"
            or "complete" or "failed" or "blocked" or "error" or "waiting" or "needs-input"
            or "review" or "reviewing";
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        return null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, params string[] names)
    {
        var value = ReadString(element, names);
        return DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;
    }

    private static int ReadInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number))
            {
                return Math.Max(0, number);
            }
        }
        return 0;
    }

    private static bool ReadBool(JsonElement element, bool fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }
        return fallback;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _gate.Dispose();
    }
}
