using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using ClaudePetOverlay.Models;

namespace ClaudePetOverlay.Services;

public sealed class AnimationPlayer
{
    // 各状態フォルダの timing.yaml で再生タイミングを宣言する:
    //   fps: 8                       (等間隔)
    //   durations_ms: [5000, 250]    (フレームごとの表示ミリ秒。個数一致必須。
    //                                 ブロック形式 "- 値" も可)
    // 無ければ 60fps。
    //
    // カスタム素材: Load に customRoot を渡すと、状態ごとに以下を組み込み素材より
    // 優先して読む。
    //   1. <customRoot>\<state>\frame_*.png (フォルダ形式。timing.yaml 対応)
    //   2. <customRoot>\<state>.png (スプライトシート形式。<state>.json でメタ指定:
    //      columns / rows / frameCount / fps / durationsMs。json が無ければ
    //      セルのアスペクト比 576:624 を仮定して列数を推定する)
    private const double DefaultSourceFps = 60.0;
    private const double SheetDefaultFps = 16.0;
    private const double MinSourceFps = 1.0;
    private const double MaxSourceFps = 240.0;
    private const double MinFrameDurationMs = 10.0;
    private const double MaxFrameDurationMs = 60000.0;
    private const double CellAspect = 576.0 / 624.0;
    // Normal clips are authored at 576x624. Keep that resolution so the
    // source-relative 1.0x display path remains pixel-for-pixel instead of
    // decoding to 480px and then enlarging it back to 576px in WPF.
    private const int DecodeWidth = 576;
    private const int DragDecodeWidth = 384;
    private const int MaxCachedClips = 3;

    private readonly Dictionary<PetState, Func<IReadOnlyList<BitmapSource>>> _clipLoaders = new();
    private readonly Dictionary<PetState, int> _clipFrameCounts = new();
    private readonly Dictionary<PetState, double> _clipFps = new();
    private readonly Dictionary<PetState, ClipTiming> _clipTimings = new();
    private readonly Dictionary<PetState, bool> _clipIsCustom = new();
    private readonly Dictionary<PetState, IReadOnlyList<BitmapSource>> _clipCache = new();
    private readonly LinkedList<PetState> _recentClips = new();
    private readonly object _cacheGate = new();
    private PetState _state = PetState.Idle;
    private TimeSpan _stateStartedAt;
    private bool _needsClockReset = true;

    public PetState State => _state;

    public void Load(string framesRoot, string? customRoot = null)
    {
        lock (_cacheGate)
        {
            _clipCache.Clear();
            _recentClips.Clear();
        }
        _clipLoaders.Clear();
        _clipFrameCounts.Clear();
        _clipFps.Clear();
        _clipTimings.Clear();
        _clipIsCustom.Clear();

        foreach (var state in Enum.GetValues<PetState>())
        {
            if (customRoot is not null && TryRegisterCustom(customRoot, state))
            {
                _clipIsCustom[state] = true;
                continue;
            }
            RegisterFolderClip(state, Path.Combine(framesRoot, state.AssetFolder()));
            _clipIsCustom[state] = false;
        }

        _ = GetClip(PetState.Idle);
    }

    public bool IsCustom(PetState state) => _clipIsCustom.TryGetValue(state, out var custom) && custom;

    // ------------------------------------------------------------------
    // クリップ登録

    private bool TryRegisterCustom(string customRoot, PetState state)
    {
        try
        {
            var folder = Path.Combine(customRoot, state.AssetFolder());
            if (Directory.Exists(folder)
                && Directory.GetFiles(folder, "frame_*.png").Length > 0)
            {
                RegisterFolderClip(state, folder);
                return true;
            }

            var sheetPath = Path.Combine(customRoot, state.AssetFolder() + ".png");
            if (File.Exists(sheetPath))
            {
                RegisterSheetClip(state, sheetPath, Path.Combine(customRoot, state.AssetFolder() + ".json"));
                return true;
            }
        }
        catch (Exception)
        {
            // 壊れたカスタム素材は無視して組み込み素材へフォールバックする。
            return false;
        }
        return false;
    }

    private void RegisterFolderClip(PetState state, string folder)
    {
        var paths = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "frame_*.png").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
        if (paths.Length == 0)
        {
            throw new InvalidOperationException($"Animation frames are missing: {folder}");
        }

        var (fps, timing) = ReadTimingYaml(folder, paths.Length);
        _clipFrameCounts[state] = paths.Length;
        _clipFps[state] = fps ?? DefaultSourceFps;
        if (timing is { } value)
        {
            _clipTimings[state] = value;
        }

        var decodeWidth = state is PetState.RunningLeft or PetState.RunningRight
            ? DragDecodeWidth
            : DecodeWidth;
        _clipLoaders[state] = () => paths.Select(path => LoadBitmap(path, decodeWidth)).ToArray();
    }

    private void RegisterSheetClip(PetState state, string sheetPath, string metaPath)
    {
        var sheetWidth = ReadPngWidth(sheetPath);
        var sheetHeight = ReadPngHeight(sheetPath);
        if (sheetWidth <= 0 || sheetHeight <= 0)
        {
            throw new InvalidOperationException($"Unreadable sprite sheet: {sheetPath}");
        }

        var meta = ReadSheetMeta(metaPath);
        var columns = meta.Columns
            ?? Math.Clamp((int)Math.Round((double)sheetWidth / sheetHeight * (1.0 / CellAspect) / (meta.Rows ?? 1)), 1, 64);
        var rows = meta.Rows ?? 1;
        var frameCount = Math.Clamp(meta.FrameCount ?? columns * rows, 1, columns * rows);

        _clipFrameCounts[state] = frameCount;
        _clipFps[state] = Math.Clamp(meta.Fps ?? SheetDefaultFps, MinSourceFps, MaxSourceFps);
        if (meta.DurationsMs is { Length: > 0 } durations && durations.Length == frameCount)
        {
            var seconds = new double[frameCount];
            var total = 0.0;
            for (var index = 0; index < frameCount; index++)
            {
                seconds[index] = Math.Clamp(durations[index], MinFrameDurationMs, MaxFrameDurationMs) / 1000.0;
                total += seconds[index];
            }
            _clipTimings[state] = new ClipTiming(seconds, total);
        }

        _clipLoaders[state] = () =>
        {
            var sheet = LoadBitmap(sheetPath, 0);
            var cellWidth = sheet.PixelWidth / columns;
            var cellHeight = sheet.PixelHeight / rows;
            var frames = new BitmapSource[frameCount];
            for (var index = 0; index < frameCount; index++)
            {
                var cell = new CroppedBitmap(
                    sheet,
                    new Int32Rect(
                        index % columns * cellWidth,
                        index / columns * cellHeight,
                        cellWidth,
                        cellHeight));
                cell.Freeze();
                frames[index] = cell;
            }
            return frames;
        };
    }

    private sealed record SheetMeta(
        int? Columns,
        int? Rows,
        int? FrameCount,
        double? Fps,
        double[]? DurationsMs);

    private static SheetMeta ReadSheetMeta(string metaPath)
    {
        if (!File.Exists(metaPath))
        {
            return new SheetMeta(null, null, null, null, null);
        }
        using var document = JsonDocument.Parse(File.ReadAllText(metaPath));
        var root = document.RootElement;

        int? ReadInt(string name) =>
            root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) && number > 0
                ? number
                : null;
        double? ReadDouble(string name) =>
            root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && number > 0
                ? number
                : null;
        double[]? ReadArray(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray()
                    .Where(item => item.TryGetDouble(out _))
                    .Select(item => item.GetDouble())
                    .ToArray()
                : null;

        return new SheetMeta(
            ReadInt("columns"),
            ReadInt("rows"),
            ReadInt("frameCount"),
            ReadDouble("fps"),
            ReadArray("durationsMs"));
    }

    // ------------------------------------------------------------------
    // タイミング設定の読み取り

    // YAML はキー 2 つの最小サブセットだけを自前で読む (依存追加を避ける)。
    // 読めない/壊れている場合は (null, null) = 既定レート。
    private static (double? Fps, ClipTiming? Timing) ReadTimingYaml(string folder, int frameCount)
    {
        var path = Path.Combine(folder, "timing.yaml");
        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return (null, null);
            }
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return (null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null);
        }

        double? fps = null;
        List<double>? durations = null;
        var inDurationsBlock = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Split('#')[0].TrimEnd();
            if (line.Trim().Length == 0)
            {
                continue;
            }
            var trimmed = line.Trim();
            if (inDurationsBlock && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                if (!TryParseDouble(trimmed[2..], out var item))
                {
                    return (null, null);
                }
                durations!.Add(item);
                continue;
            }
            inDurationsBlock = false;

            if (trimmed.StartsWith("fps:", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseDouble(trimmed[4..], out var value) || value < MinSourceFps || value > MaxSourceFps)
                {
                    return (null, null);
                }
                fps = value;
            }
            else if (trimmed.StartsWith("durations_ms:", StringComparison.OrdinalIgnoreCase))
            {
                durations = new List<double>();
                var rest = trimmed["durations_ms:".Length..].Trim();
                if (rest.Length == 0)
                {
                    inDurationsBlock = true; // ブロック形式: 続く "- 値" 行を読む
                    continue;
                }
                if (!rest.StartsWith('[') || !rest.EndsWith(']'))
                {
                    return (null, null);
                }
                foreach (var token in rest[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!TryParseDouble(token, out var item))
                    {
                        return (null, null);
                    }
                    durations.Add(item);
                }
            }
        }

        ClipTiming? timing = null;
        if (durations is not null)
        {
            if (durations.Count != frameCount || durations.Any(value => value <= 0))
            {
                return (null, null);
            }
            var seconds = new double[frameCount];
            var total = 0.0;
            for (var index = 0; index < frameCount; index++)
            {
                seconds[index] = Math.Clamp(durations[index], MinFrameDurationMs, MaxFrameDurationMs) / 1000.0;
                total += seconds[index];
            }
            timing = new ClipTiming(seconds, total);
        }
        return (fps, timing);
    }

    private static bool TryParseDouble(string token, out double value) =>
        double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // ------------------------------------------------------------------
    // 再生

    public void SetState(PetState state, bool restart = true)
    {
        if (_state == state && !restart)
        {
            return;
        }

        _state = state;
        _needsClockReset = true;
    }

    public FrameBlend GetFrame(TimeSpan renderingTime, bool smoothInterpolation)
    {
        if (_needsClockReset)
        {
            _stateStartedAt = renderingTime;
            _needsClockReset = false;
        }

        var frames = GetClip(_state);
        var elapsed = Math.Max(0, (renderingTime - _stateStartedAt).TotalSeconds);
        int currentIndex;
        if (_clipTimings.TryGetValue(_state, out var timing))
        {
            var cyclePosition = elapsed % timing.TotalSeconds;
            currentIndex = frames.Count - 1;
            var accumulated = 0.0;
            for (var index = 0; index < timing.FrameSeconds.Length; index++)
            {
                accumulated += timing.FrameSeconds[index];
                if (cyclePosition < accumulated)
                {
                    currentIndex = index;
                    break;
                }
            }
        }
        else
        {
            currentIndex = (int)Math.Floor(elapsed * ClipFps(_state)) % frames.Count;
        }
        var nextIndex = (currentIndex + 1) % frames.Count;
        // Blending two transparent sprites creates double outlines and perceived
        // flicker, so always show discrete frames regardless of the clip rate.
        return new FrameBlend(frames[currentIndex], frames[nextIndex], 0);
    }

    public double ClipFps(PetState state) =>
        _clipFps.TryGetValue(state, out var fps) ? fps : DefaultSourceFps;

    // そのクリップを取りこぼしなく表示するのに必要なレート (durations 指定時は最短フレームから導出)。
    public double RequiredDisplayFps(PetState state) =>
        _clipTimings.TryGetValue(state, out var timing)
            ? 1.0 / timing.FrameSeconds.Min()
            : ClipFps(state);

    public double DurationSeconds(PetState state) =>
        _clipTimings.TryGetValue(state, out var timing)
            ? timing.TotalSeconds
            : _clipFrameCounts[state] / ClipFps(state);

    public bool HasVariableTimings(PetState state) => _clipTimings.ContainsKey(state);

    public IReadOnlyDictionary<PetState, int> FrameCounts() => _clipFrameCounts;

    public Task PreloadAsync(params PetState[] states) => Task.Run(() =>
    {
        foreach (var state in states)
        {
            _ = GetClip(state);
        }
    });

    public bool IsCached(PetState state)
    {
        lock (_cacheGate)
        {
            return _clipCache.ContainsKey(state);
        }
    }

    private IReadOnlyList<BitmapSource> GetClip(PetState state)
    {
        lock (_cacheGate)
        {
            if (_clipCache.TryGetValue(state, out var cached))
            {
                TouchCache(state);
                return cached;
            }
        }

        var loaded = _clipLoaders[state]();
        lock (_cacheGate)
        {
            if (!_clipCache.TryGetValue(state, out var clip))
            {
                clip = loaded;
                _clipCache[state] = clip;
            }
            TouchCache(state);
            return clip;
        }
    }

    private void TouchCache(PetState state)
    {
        _recentClips.Remove(state);
        _recentClips.AddLast(state);
        while (_clipCache.Count > MaxCachedClips)
        {
            var candidate = _recentClips.First;
            while (candidate is not null
                   && (candidate.Value == state || IsPinnedDragClip(candidate.Value)))
            {
                candidate = candidate.Next;
            }
            if (candidate is null)
            {
                break;
            }

            _recentClips.Remove(candidate);
            _clipCache.Remove(candidate.Value);
        }
    }

    private static bool IsPinnedDragClip(PetState state) =>
        state is PetState.RunningLeft or PetState.RunningRight;

    private static BitmapSource LoadBitmap(string path, int decodeWidth)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        // DecodePixelWidth は縮小専用。素材が指定幅以下の場合に指定すると拡大デコードに
        // なり無駄にぼやけるので原寸で読む。decodeWidth <= 0 は常に原寸。
        var actualWidth = ReadPngWidth(path);
        if (decodeWidth > 0 && (actualWidth <= 0 || actualWidth > decodeWidth))
        {
            image.DecodePixelWidth = decodeWidth;
        }
        stream.Seek(0, SeekOrigin.Begin);
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static int ReadPngWidth(string path) => ReadPngHeaderField(path, offset: 16);

    private static int ReadPngHeight(string path) => ReadPngHeaderField(path, offset: 20);

    private static int ReadPngHeaderField(string path, int offset)
    {
        // PNG signature (8B) + IHDR chunk length/type (8B) + width (4B) + height (4B), big-endian
        Span<byte> header = stackalloc byte[24];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Read(header) < header.Length)
            {
                return 0;
            }
        }
        catch (IOException)
        {
            return 0;
        }
        return (header[offset] << 24) | (header[offset + 1] << 16) | (header[offset + 2] << 8) | header[offset + 3];
    }

    private readonly record struct ClipTiming(double[] FrameSeconds, double TotalSeconds);
}

public readonly record struct FrameBlend(BitmapSource Current, BitmapSource Next, double Blend);
