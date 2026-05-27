namespace SyncMaster.CalImport;

public interface IImportSource
{
    ImportPayload Load(string path);
}
