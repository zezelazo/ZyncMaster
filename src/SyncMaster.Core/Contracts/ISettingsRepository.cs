namespace SyncMaster.Core;

public interface ISettingsRepository<T> where T : class
{
    T?   TryLoad(string path);
    T    LoadOrCreateDefault(string path);
    void Save(T settings, string path);
    bool Exists(string path);
}
