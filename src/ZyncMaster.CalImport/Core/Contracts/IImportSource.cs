namespace ZyncMaster.CalImport;

public interface IImportSource
{
    ImportPayload Load(string path);
}
