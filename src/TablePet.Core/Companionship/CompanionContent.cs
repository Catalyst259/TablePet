using System.Globalization;
using System.Text.Json;

namespace TablePet.Core;

public sealed record CompanionText
{
    public string Id { get; init; } = "";
    public string Scene { get; init; } = "";
    public string Stage { get; init; } = "";
    public string Text { get; init; } = "";
    public string ShortText { get; init; } = "";
    public int Weight { get; init; } = 1;

    public string ForHeight(double petHeightDip) => petHeightDip < 180 ? ShortText : Text;
    public int DurationMs(double petHeightDip = 240) =>
        Math.Clamp(2200 + 100 * CompanionContent.VisibleLength(ForHeight(petHeightDip)), 3000, 4500);
}

public sealed record CompanionMemoryDefinition
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string UnlockCondition { get; init; } = "";
}

/// <summary>The bundled, deterministic copy catalog. Invalid content fails closed to base pet speech.</summary>
public sealed class CompanionContent
{
    private static readonly HashSet<string> Stages = ["new", "familiar", "close", "in_sync"];
    private static readonly HashSet<string> Scenes =
        ["daily_greeting", "daily_click", "work_start", "work_break", "rest_click", "return_greeting"];
    private readonly Dictionary<string, CompanionText> _texts;
    private readonly Dictionary<string, CompanionMemoryDefinition> _memories;

    public static CompanionContent Default { get; } = LoadDefault();
    public static CompanionContent Empty { get; } = new([], [], []);
    public IReadOnlyList<CompanionText> Texts { get; }
    public IReadOnlyList<CompanionMemoryDefinition> Memories { get; }
    public IReadOnlyList<string> ValidationErrors { get; }
    public bool IsValid => ValidationErrors.Count == 0;

    private CompanionContent(CompanionText[] texts, CompanionMemoryDefinition[] memories, string[] errors)
    {
        Texts = Array.AsReadOnly(texts);
        Memories = Array.AsReadOnly(memories);
        ValidationErrors = Array.AsReadOnly(errors);
        _texts = texts.ToDictionary(x => x.Id, StringComparer.Ordinal);
        _memories = memories.ToDictionary(x => x.Id, StringComparer.Ordinal);
    }

    public CompanionText? FindText(string id) => _texts.GetValueOrDefault(id);
    public CompanionMemoryDefinition? FindMemory(string id) => _memories.GetValueOrDefault(id);
    public IReadOnlyList<CompanionText> ForScene(string scene, string stage) =>
        Texts.Where(x => x.Scene == scene && x.Stage == stage).ToArray();

    public static int VisibleLength(string text) => StringInfo.ParseCombiningCharacters(text).Length;

    public static CompanionContent FromJson(string json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<CatalogData>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (data is null) return Invalid("Content is empty.");
            var errors = new List<string>();
            var textIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var text in data.Texts)
            {
                if (text is null) { errors.Add("Null text entry."); continue; }
                if (string.IsNullOrWhiteSpace(text.Id) || !textIds.Add(text.Id)) errors.Add($"Duplicate or empty text ID: {text.Id}");
                var normal = Scenes.Contains(text.Scene) && Stages.Contains(text.Stage);
                var auxiliary = text.Scene is "milestone" or "rest_short" && text.Stage == "any";
                if (!normal && !auxiliary) errors.Add($"Invalid scene/stage: {text.Id}");
                if (string.IsNullOrWhiteSpace(text.Text) || VisibleLength(text.Text) > 18 ||
                    string.IsNullOrWhiteSpace(text.ShortText) || VisibleLength(text.ShortText) > 14 || text.Weight < 1)
                    errors.Add($"Invalid text, short text or weight: {text.Id}");
            }
            var memoryIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var memory in data.Memories)
            {
                if (memory is null) { errors.Add("Null memory entry."); continue; }
                if (string.IsNullOrWhiteSpace(memory.Id) || !memoryIds.Add(memory.Id)) errors.Add($"Duplicate or empty memory ID: {memory.Id}");
                if (string.IsNullOrWhiteSpace(memory.Title) || string.IsNullOrWhiteSpace(memory.Description))
                    errors.Add($"Empty memory copy: {memory.Id}");
            }
            return errors.Count == 0
                ? new(data.Texts, data.Memories, [])
                : new([], [], errors.ToArray());
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException or NullReferenceException)
        {
            return Invalid($"Cannot load companion content: {error.Message}");
        }
    }

    private static CompanionContent Invalid(string error) => new([], [], [error]);

    private static CompanionContent LoadDefault()
    {
        try
        {
            using var stream = typeof(CompanionContent).Assembly.GetManifestResourceStream(
                "TablePet.Core.Companionship.companion-content.zh-CN.json");
            if (stream is null) return Invalid("Bundled companion content was not found.");
            using var reader = new StreamReader(stream);
            return FromJson(reader.ReadToEnd());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Invalid($"Cannot read companion content: {error.Message}");
        }
    }

    private sealed class CatalogData
    {
        public CatalogData() { }
        public CompanionText[] Texts { get; set; } = [];
        public CompanionMemoryDefinition[] Memories { get; set; } = [];
    }
}
