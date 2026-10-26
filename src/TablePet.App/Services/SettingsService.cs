using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using TablePet.Core;
using Timer = System.Threading.Timer;

namespace TablePet.App.Services;

/// <summary>Versioned JSON settings, validated before publication and replaced atomically.</summary>
public sealed class SettingsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    private readonly object _gate = new();
    private readonly Timer _saveTimer;
    private bool _dirty;
    private bool _disposed;

    public SettingsService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopPet", "settings.json");
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string SettingsPath { get; }
    public PetSettings Current { get; private set; } = new();
    public string? LastError { get; private set; }
    public event Action<PetSettings>? Changed;
    /// <summary>May be raised by the save worker; UI subscribers must dispatch to their UI thread.</summary>
    public event Action<string>? SaveFailed;

    public PetSettings Load()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!File.Exists(SettingsPath)) return Current;
            try
            {
                var loaded = JsonSerializer.Deserialize<PetSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                    ?? throw new JsonException("配置内容为空。");
                if (loaded.SchemaVersion != 1) throw new JsonException("不支持此配置文件版本。");
                loaded.EnsureValid();
                Current = loaded;
                LastError = null;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                Current = new PetSettings();
                LastError = "设置读取失败，已使用默认设置。";
                ErrorLogger.Write(LastError, ex);
                // Preserve unreadable or invalid data for recovery instead of deleting it.
                try
                {
                    var backup = SettingsPath + $".invalid-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.bak";
                    File.Copy(SettingsPath, backup, overwrite: false);
                    LastError += " 原配置已备份。";
                }
                catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
                {
                    ErrorLogger.Write("无法备份配置。", backupError);
                }
            }
            return Current;
        }
    }

    public void Update(PetSettings settings, bool saveImmediately = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.EnsureValid();
        bool changed;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            changed = settings != Current;
            if (!changed && !saveImmediately) return;
            Current = settings;
            _dirty = true;
            _saveTimer.Change(saveImmediately ? Timeout.Infinite : 600, Timeout.Infinite);
        }
        if (changed) Changed?.Invoke(settings);
        if (saveImmediately) Flush();
    }

    public void ResetDefaults() => Update(new PetSettings(), saveImmediately: true);

    public bool Flush()
    {
        string? failure = null;
        lock (_gate)
        {
            if (!_dirty) return true;
            var temporaryPath = SettingsPath + ".tmp";
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(SettingsPath))!;
                Directory.CreateDirectory(directory);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(Current, JsonOptions);
                using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(SettingsPath))
                    File.Replace(temporaryPath, SettingsPath, destinationBackupFileName: null);
                else
                    File.Move(temporaryPath, SettingsPath);
                _dirty = false;
                LastError = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastError = failure = "设置保存失败，原配置仍然保留。";
                ErrorLogger.Write(failure, ex);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        if (failure is not null) SaveFailed?.Invoke(failure);
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _saveTimer.Dispose();
        }
        Flush();
    }
}
