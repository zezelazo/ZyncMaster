using System.Collections.Generic;
using System.Text;

namespace SyncMaster.Core;

public interface IFileSystem
{
    bool   FileExists(string path);
    bool   DirectoryExists(string path);
    void   CreateDirectory(string path);
    string ReadAllText(string path);
    void   WriteAllText(string path, string content, Encoding encoding);
    void   WriteAllLines(string path, IEnumerable<string> lines, Encoding encoding);
}
