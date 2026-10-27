namespace TablePet.Core;

/// <summary>A one-shot expression offered by an animation pack, with a relative selection weight.</summary>
public sealed record IdleAction(string Clip, int Weight = 1)
{
    public static IReadOnlyList<IdleAction> Available(IReadOnlyDictionary<string, AnimationClip> clips,
        IEnumerable<IdleAction>? requested = null)
    {
        var choices = requested?.ToArray() ?? [];
        var blinkWeight = choices.FirstOrDefault(a => a.Clip == "blink" && a.Weight is >= 1 and <= 1000)?.Weight ?? 4;
        var available = new List<IdleAction> { new("blink", blinkWeight) };
        var seen = new HashSet<string> { "idle", "blink", "fall_asleep", "sleep_loop", "wake", "rise", "hold", "settle" };
        var restingFrame = clips["idle"].Frames[0].File;
        foreach (var action in choices)
        {
            if (string.IsNullOrWhiteSpace(action.Clip) || action.Weight is < 1 or > 1000 || seen.Contains(action.Clip)
                || !clips.TryGetValue(action.Clip, out var clip) || clip.Name != action.Clip || clip.Loop
                || clip.Frames.Count < 2 || clip.Frames.Any(f => string.IsNullOrWhiteSpace(f.File) || f.DurationMs <= 0)
                || clip.Frames[0].File != restingFrame || clip.Frames[^1].File != restingFrame) continue;
            seen.Add(action.Clip);
            available.Add(action);
        }
        return available.AsReadOnly();
    }
}
