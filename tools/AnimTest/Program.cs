// AnimationPlayer の素材 fps (fps.txt) / 可変フレーム時間 (durations.txt) /
// カスタム素材 (customRoot) の検証ハーネス。
// 使い方: AnimTest <framesRoot> [customRoot]
//   framesRoot には 9 状態のフォルダが必要。前提とするテストデータ:
//     idle:    4 枚 + fps.txt "8"
//     running: 4 枚 + fps.txt "16"
//     waving:  4 枚 (fps.txt なし -> 既定 60)
//     failed:  4 枚 + fps.txt "abc" (壊れた値 -> 既定 60)
//     waiting: 4 枚 + durations.txt "100,200,300,400" (可変時間再生)
//     jumping: 4 枚 + durations.txt "100,200" (個数不一致 -> 無視して fps 経路)
//   customRoot を渡すと再ロードしてカスタム素材経路も検証する。前提:
//     review.png (4 フレーム横並びシート) + review.json {"columns":4,"fps":8}
//     waving\frame_*.png 2 枚 + fps.txt "10" (フォルダ形式カスタム)
// 全チェック成功で exit 0、失敗で exit 1。

using System.Windows.Media.Imaging;
using ClaudePetOverlay.Models;
using ClaudePetOverlay.Services;

var failures = 0;

void Check(string name, bool condition, string detail = "")
{
    if (condition)
    {
        Console.WriteLine($"  ok: {name}");
    }
    else
    {
        failures++;
        Console.WriteLine($"FAIL: {name} {detail}");
    }
}

if (args.Length < 1)
{
    Console.WriteLine("usage: AnimTest <framesRoot>");
    return 2;
}

var player = new AnimationPlayer();
player.Load(args[0]);

Console.WriteLine("[fps.txt の読み取り]");
Check("idle = 8fps", Math.Abs(player.ClipFps(PetState.Idle) - 8) < 1e-9, $"got {player.ClipFps(PetState.Idle)}");
Check("running = 16fps", Math.Abs(player.ClipFps(PetState.Running) - 16) < 1e-9, $"got {player.ClipFps(PetState.Running)}");
Check("waving = 既定 60fps", Math.Abs(player.ClipFps(PetState.Waving) - 60) < 1e-9, $"got {player.ClipFps(PetState.Waving)}");
Check("failed (不正値) = 既定 60fps", Math.Abs(player.ClipFps(PetState.Failed) - 60) < 1e-9, $"got {player.ClipFps(PetState.Failed)}");

Console.WriteLine("[DurationSeconds]");
Check("idle 4 枚 @8fps = 0.5s", Math.Abs(player.DurationSeconds(PetState.Idle) - 0.5) < 1e-9, $"got {player.DurationSeconds(PetState.Idle)}");
Check("running 4 枚 @16fps = 0.25s", Math.Abs(player.DurationSeconds(PetState.Running) - 0.25) < 1e-9, $"got {player.DurationSeconds(PetState.Running)}");

Console.WriteLine("[GetFrame のフレーム進行 (idle 4 枚 @8fps)]");
// 8fps では 0.125s ごとに 1 フレーム進む。t = 0 / 0.125 / 0.25 / 0.375 は
// frame 0/1/2/3 (すべて相異なる)、t = 0.5 で frame 0 へラップする。
// 既定 60fps のままなら t=0.125 と t=0.25 が同一フレーム (7%4 == 15%4 == 3) になり検出できる。
player.SetState(PetState.Idle);
double[] times = [0, 0.125, 0.25, 0.375, 0.5];
var frames = new BitmapSource[times.Length];
for (var i = 0; i < times.Length; i++)
{
    frames[i] = player.GetFrame(TimeSpan.FromSeconds(times[i]), smoothInterpolation: false).Current;
}
var firstFour = frames.Take(4).ToArray();
Check("t=0..0.375 の 4 フレームが相異なる", firstFour.Distinct().Count() == 4);
Check("t=0.5 で先頭フレームへラップ", ReferenceEquals(frames[0], frames[4]));

Console.WriteLine("[durations.txt (waiting 4 枚 = 100,200,300,400ms)]");
Check("可変時間が有効", player.HasVariableTimings(PetState.Waiting));
Check("DurationSeconds = 1.0s", Math.Abs(player.DurationSeconds(PetState.Waiting) - 1.0) < 1e-9, $"got {player.DurationSeconds(PetState.Waiting)}");
// 区間: f0 = [0, 0.1), f1 = [0.1, 0.3), f2 = [0.3, 0.6), f3 = [0.6, 1.0), 1.0 でラップ。
player.SetState(PetState.Waiting);
double[] waitTimes = [0.0, 0.05, 0.15, 0.45, 0.75, 1.05];
var waitFrames = new BitmapSource[waitTimes.Length];
// SetState 直後の最初の GetFrame がクロック基準になるため t=0 を最初に呼ぶ。
for (var i = 0; i < waitTimes.Length; i++)
{
    waitFrames[i] = player.GetFrame(TimeSpan.FromSeconds(waitTimes[i]), smoothInterpolation: false).Current;
}
Check("t=0.05 は先頭フレーム", ReferenceEquals(waitFrames[0], waitFrames[1]));
Check("t=0.15 で 2 枚目へ", !ReferenceEquals(waitFrames[1], waitFrames[2]));
Check("t=0.05/0.15/0.45/0.75 が相異なる", waitFrames.Skip(1).Take(4).Distinct().Count() == 4);
Check("t=1.05 で先頭フレームへラップ", ReferenceEquals(waitFrames[0], waitFrames[5]));

Console.WriteLine("[durations.txt 個数不一致 (jumping) -> fps 経路]");
Check("可変時間は無効", !player.HasVariableTimings(PetState.Jumping));
Check("DurationSeconds = 4/60s", Math.Abs(player.DurationSeconds(PetState.Jumping) - 4.0 / 60.0) < 1e-9, $"got {player.DurationSeconds(PetState.Jumping)}");

if (args.Length > 1)
{
    Console.WriteLine("[カスタム素材 (customRoot、再ロード検証込み)]");
    player.Load(args[0], args[1]);
    Check("review はシート形式カスタム", player.IsCustom(PetState.Review));
    Check("review フレーム数 = 4", player.FrameCounts()[PetState.Review] == 4, $"got {player.FrameCounts()[PetState.Review]}");
    Check("review fps = 8 (json 指定)", Math.Abs(player.ClipFps(PetState.Review) - 8) < 1e-9, $"got {player.ClipFps(PetState.Review)}");
    player.SetState(PetState.Review);
    var r0 = player.GetFrame(TimeSpan.Zero, smoothInterpolation: false).Current;
    var r1 = player.GetFrame(TimeSpan.FromSeconds(0.125), smoothInterpolation: false).Current;
    var rWrap = player.GetFrame(TimeSpan.FromSeconds(0.5), smoothInterpolation: false).Current;
    Check("シートのフレームが切り替わる", !ReferenceEquals(r0, r1));
    Check("シートがラップする", ReferenceEquals(r0, rWrap));
    Check("シートのセル幅 = 96 (384/4)", r0.PixelWidth == 96, $"got {r0.PixelWidth}");
    Check("waving はフォルダ形式カスタム", player.IsCustom(PetState.Waving));
    Check("waving フレーム数 = 2", player.FrameCounts()[PetState.Waving] == 2, $"got {player.FrameCounts()[PetState.Waving]}");
    Check("waving fps = 10", Math.Abs(player.ClipFps(PetState.Waving) - 10) < 1e-9, $"got {player.ClipFps(PetState.Waving)}");
    Check("idle は組み込みのまま", !player.IsCustom(PetState.Idle));
    Check("idle の fps.txt が再ロード後も有効", Math.Abs(player.ClipFps(PetState.Idle) - 8) < 1e-9);
}

Console.WriteLine();
if (failures > 0)
{
    Console.WriteLine($"NG: {failures} 件失敗");
    return 1;
}
Console.WriteLine("全件パス");
return 0;
