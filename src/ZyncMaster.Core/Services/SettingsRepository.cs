using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace ZyncMaster.Core;

public sealed class SettingsRepository<T> : ISettingsRepository<T> where T : class, new()
{
    private readonly IFileSystem _fs;

    public SettingsRepository(IFileSystem fs)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
    }

    public T? TryLoad(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));

        if (!_fs.FileExists(path))
            return null;

        try
        {
            var json = _fs.ReadAllText(path);
            return JsonConvert.DeserializeObject<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public T LoadOrCreateDefault(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));

        if (_fs.FileExists(path))
        {
            var loaded = TryLoad(path);
            if (loaded != null)
                return loaded;

            throw new SettingsLoadException(
                $"Settings file '{path}' exists but could not be deserialized as {typeof(T).Name}. Inspect the file or delete it to regenerate defaults.");
        }

        var defaults = new T();
        Save(defaults, path);
        return defaults;
    }

    public void Save(T settings, string path)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (path     == null) throw new ArgumentNullException(nameof(path));

        // Ensure the parent directory exists before writing. On a fresh machine the settings path
        // (e.g. %LOCALAPPDATA%\ZyncMaster\App\settings.json) lives under a directory nothing has
        // created yet; without this, WriteAllText throws DirectoryNotFoundException and the default
        // settings file is silently never created — which bricks the app into an "unconfigured"
        // state with no file to fix and no error surfaced.
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !_fs.DirectoryExists(dir))
            _fs.CreateDirectory(dir);

        var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
        _fs.WriteAllText(path, json, Encoding.UTF8);
    }

    public bool Exists(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        return _fs.FileExists(path);
    }
}
