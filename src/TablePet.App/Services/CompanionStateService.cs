using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TablePet.Core;

namespace TablePet.App.Services;

/// <summary>Owns one serialized writer. A receipt identifies the actual durable snapshot.</summary>
public sealed class CompanionStateService
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private readonly object _gate = new();
    private readonly List<(long Revision, TaskCompletionSource<bool> Completion)> _waiters = [];
    private CompanionState? _pending;
    private Task? _writer;
    private bool _writing;
    private long _inFlightRevision = -1;

    public CompanionStateService(string? path = null)
    {
        StatePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopPet.Skeletal", "companion-state.json");
    }

    public string StatePath { get; }
    public string BackupPath => StatePath + ".bak";
    public CompanionState Current { get; private set; } = new();
    public bool CanWrite { get; private set; } = true;
    public bool UnsupportedVersion { get; private set; }
    public string? LastError { get; private set; }
    public string? RecoveryNotice { get; private set; }
    public long SavedRevision { get; private set; } = -1;
    public DateTimeOffset? LastSavedAtUtc { get; private set; }
    public event Action<long>? Saved;
    public event Action? StatusChanged;

    public CompanionState Load()
    {
        lock (_gate)
        {
            if (_writing) throw new InvalidOperationException("保存尚未结束，请稍后重试读取。");
            CanWrite = true;
            UnsupportedVersion = false;
            LastError = RecoveryNotice = null;
            SavedRevision = -1;
            LastSavedAtUtc = null;
            try
            {
                if (File.Exists(StatePath))
                {
                    try { return Accept(Read(StatePath)); }
                    catch (UnsupportedStateException) { throw; }
                    catch (Exception error) when (IsStorageError(error))
                    {
                        Preserve(StatePath, "invalid");
                    }
                }
                else if (!File.Exists(BackupPath)) return Accept(new CompanionState(), isSaved: false);

                if (File.Exists(BackupPath))
                {
                    var restored = Read(BackupPath);
                    WriteSnapshot(restored, preservePrimary: false);
                    RecoveryNotice = restored.SavedAtUtc is { } saved
                        ? $"已恢复到 {saved.ToLocalTime():yyyy-MM-dd HH:mm} 的相伴记录。此后未能恢复的记录可能缺失，原文件已保留。"
                        : "已从备份恢复相伴记录，原文件已保留。";
                    return Accept(restored);
                }
                throw new InvalidDataException("没有可用的相伴记录备份。");
            }
            catch (Exception error) when (IsStorageError(error))
            {
                CanWrite = false;
                UnsupportedVersion = error is UnsupportedStateException;
                LastError = UnsupportedVersion ? "这份相伴记录需要兼容的新版本。现有文件已保留。"
                    : "相伴记录暂时读不到。原文件已保留，你可以重试读取。";
                ErrorLogger.Write("相伴记录读取失败。", error);
                Current = new CompanionState();
                return Current;
            }
        }
    }

    private CompanionState Accept(CompanionState state, bool isSaved = true)
    {
        Current = state;
        if (isSaved) { SavedRevision = state.StateRevision; LastSavedAtUtc = state.SavedAtUtc; }
        return state;
    }

    public Task<bool> SaveAsync(CompanionState state)
    {
        var snapshot = state.DeepClone();
        var errors = snapshot.Validate();
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors), nameof(state));
        lock (_gate)
        {
            if (!CanWrite) return Task.FromResult(false);
            if (snapshot.StateRevision <= SavedRevision) return Task.FromResult(true);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((snapshot.StateRevision, completion));
            if (snapshot.StateRevision > _inFlightRevision && snapshot.StateRevision > SavedRevision
                && (_pending is null || snapshot.StateRevision >= _pending.StateRevision)) _pending = snapshot;
            if (!_writing)
            {
                _writing = true;
                _writer = Task.Run(WritePending);
            }
            return completion.Task;
        }
    }

    private void WritePending()
    {
        while (true)
        {
            CompanionState snapshot;
            lock (_gate)
            {
                if (_pending is null) { _writing = false; return; }
                snapshot = _pending;
                _pending = null;
                _inFlightRevision = snapshot.StateRevision;
            }
            var succeeded = false;
            try
            {
                snapshot.SavedAtUtc = DateTimeOffset.UtcNow;
                WriteSnapshot(snapshot, preservePrimary: true);
                lock (_gate)
                {
                    SavedRevision = Math.Max(SavedRevision, snapshot.StateRevision);
                    LastSavedAtUtc = snapshot.SavedAtUtc;
                    LastError = null;
                }
                succeeded = true;
            }
            catch (Exception error) when (IsStorageError(error))
            {
                lock (_gate)
                {
                    LastError = "这次相伴暂未保存。请重试保存，退出前未保存的变化可能丢失。";
                    if (error is UnsupportedStateException) { CanWrite = false; UnsupportedVersion = true; }
                }
                ErrorLogger.Write("相伴记录保存失败。", error);
            }
            lock (_gate)
            {
                _inFlightRevision = -1;
                foreach (var waiter in _waiters.Where(w => w.Revision <= snapshot.StateRevision).ToArray())
                {
                    waiter.Completion.TrySetResult(succeeded);
                    _waiters.Remove(waiter);
                }
            }
            if (succeeded) NotifySafely(() => Saved?.Invoke(snapshot.StateRevision));
            NotifySafely(() => StatusChanged?.Invoke());
        }
    }

    private void WriteSnapshot(CompanionState snapshot, bool preservePrimary)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(StatePath))!;
        Directory.CreateDirectory(directory);
        var temporary = StatePath + ".tmp";
        try
        {
            string? backup = null;
            if (preservePrimary && File.Exists(StatePath))
            {
                try { Read(StatePath); backup = BackupPath; }
                catch (UnsupportedStateException) { throw; }
                catch (Exception error) when (IsStorageError(error)) { Preserve(StatePath, "invalid"); }
            }
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(StatePath)) File.Replace(temporary, StatePath, backup);
            else File.Move(temporary, StatePath);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static CompanionState Read(string path)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("相伴记录格式无效。");
        int Version(string name)
        {
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value)) return value;
            throw new InvalidDataException("相伴记录缺少版本信息。");
        }
        var schema = Version("schemaVersion");
        if (schema > 1) throw new UnsupportedStateException();
        var rules = Version("rulesVersion");
        if (schema > 1 || rules > 1) throw new UnsupportedStateException();
        if (schema != 1 || rules != 1) throw new InvalidDataException("相伴记录版本无效。");
        var state = JsonSerializer.Deserialize<CompanionState>(json, JsonOptions)
            ?? throw new InvalidDataException("相伴记录为空。");
        if (state.SchemaVersion != 1 || state.RulesVersion != 1) throw new UnsupportedStateException();
        var errors = state.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        return state;
    }

    private static void Preserve(string path, string reason)
    {
        if (!File.Exists(path)) return;
        File.Copy(path, path + $".{reason}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.bak");
    }

    /// <summary>Only exposed by the recovery UI after an explicit restart confirmation.</summary>
    public CompanionState StartNewRecord()
    {
        lock (_gate)
        {
            if (_writing) throw new InvalidOperationException("保存尚未结束，请稍后重试。");
            if (CanWrite || UnsupportedVersion) throw new InvalidOperationException("当前记录不需要重新开始。");
            Preserve(StatePath, "restart");
            Preserve(BackupPath, "restart");
            var empty = new CompanionState();
            // The new file is committed before old recovery sources are removed.
            WriteSnapshot(empty, preservePrimary: false);
            if (File.Exists(BackupPath)) File.Delete(BackupPath);
            CanWrite = true; LastError = RecoveryNotice = null;
            return Accept(empty);
        }
    }

    public Task WaitForPendingWritesAsync()
    {
        lock (_gate) return _writer ?? Task.CompletedTask;
    }

    private static bool IsStorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or ArgumentException or NotSupportedException;
    private static void NotifySafely(Action action)
    {
        try { action(); }
        catch (Exception error) { ErrorLogger.Write("相伴记录通知失败。", error); }
    }
    private sealed class UnsupportedStateException : IOException { }
}
