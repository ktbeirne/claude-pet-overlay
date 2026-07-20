namespace ClaudePetOverlay.Models;

public enum PetState
{
    Idle,
    RunningRight,
    RunningLeft,
    Waving,
    Jumping,
    Failed,
    Waiting,
    Running,
    Review,
}

public static class PetStateExtensions
{
    public static string AssetFolder(this PetState state) => state switch
    {
        PetState.Idle => "idle",
        PetState.RunningRight => "running-right",
        PetState.RunningLeft => "running-left",
        PetState.Waving => "waving",
        PetState.Jumping => "jumping",
        PetState.Failed => "failed",
        PetState.Waiting => "waiting",
        PetState.Running => "running",
        PetState.Review => "review",
        _ => "idle",
    };

    public static string DisplayName(this PetState state) => state switch
    {
        PetState.Idle => "待機中",
        PetState.RunningRight => "移動中 →",
        PetState.RunningLeft => "← 移動中",
        PetState.Waving => "こんにちは",
        PetState.Jumping => "完了！",
        PetState.Failed => "エラー",
        PetState.Waiting => "入力待ち",
        PetState.Running => "Claude 作業中",
        PetState.Review => "確認中",
        _ => state.ToString(),
    };
}
