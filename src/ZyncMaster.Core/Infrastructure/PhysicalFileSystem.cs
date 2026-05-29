using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZyncMaster.Core;

public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path)      => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public string ReadAllText(string path)
        => File.ReadAllText(path, Encoding.UTF8);

    public void WriteAllText(string path, string content, Encoding encoding)
        => File.WriteAllText(path, content, encoding);

    public void WriteAllLines(string path, IEnumerable<string> lines, Encoding encoding)
        => File.WriteAllLines(path, lines, encoding);
}
