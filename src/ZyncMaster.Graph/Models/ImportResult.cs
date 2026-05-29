using System.Collections.Generic;

namespace ZyncMaster.Graph;

public sealed class ImportResult
{
    private readonly List<string> _failed = new List<string>();

    public int                Created   { get; set; }
    public int                Updated   { get; set; }
    public int                Cancelled { get; set; }
    public int                Skipped   { get; set; }
    public IReadOnlyList<string> Failed => _failed;

    public void AddFailure(string message) => _failed.Add(message);
}
