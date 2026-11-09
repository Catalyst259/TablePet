namespace TablePet.Core;

/// <summary>An automatic action, with a relative weight in independent draws.</summary>
public sealed record IdleAction(string Clip, int Weight = 1)
{
    public static IReadOnlyList<IdleAction> Available(IReadOnlyDictionary<string, AnimationClip> clips,
        IEnumerable<IdleAction>? requested = null)
    {
        var choices = requested?.ToArray() ?? [];
        var blinkWeight = choices.FirstOrDefault(a => a.Clip == "blink" && a.Weight is >= 1 and <= 1000)?.Weight ?? 2;
        var available = new List<IdleAction> { new("blink", blinkWeight) };
        var seen = new HashSet<string> { "blink" };
        var restingFrame = clips["idle"].Frames[0].File;
        bool Valid(string name) => clips.TryGetValue(name, out var clip) && clip.Name == name
            && clip.Frames.Count > 0 && clip.Frames.All(f => !string.IsNullOrWhiteSpace(f.File) && f.DurationMs > 0);
        bool SafeRise() => Valid("rise") && Valid("hold") && Valid("settle")
            && !clips["rise"].Loop && !clips["settle"].Loop
            && clips["rise"].Frames[0].File == restingFrame
            && clips["rise"].Frames[^1].File == clips["hold"].Frames[0].File
            && clips["settle"].Frames[0].File == clips["hold"].Frames[0].File
            && clips["settle"].Frames[^1].File == restingFrame;
        foreach (var action in choices)
        {
            // Wink and meow belong to explicit input. Rise is a complete sequence,
            // while yawn and firefly each join the resting pose on both sides.
            if (action.Clip is not ("yawn" or "rise" or "firefly") || action.Weight is < 1 or > 1000
                || seen.Contains(action.Clip) || !Valid(action.Clip)) continue;
            var clip = clips[action.Clip];
            if (action.Clip == "rise")
            {
                if (!SafeRise()) continue;
            }
            else if (clip.Loop || clip.Frames.Count < 2 || clip.Frames[0].File != restingFrame
                || clip.Frames[^1].File != restingFrame) continue;
            seen.Add(action.Clip);
            available.Add(action);
        }
        return available.AsReadOnly();
    }
}
