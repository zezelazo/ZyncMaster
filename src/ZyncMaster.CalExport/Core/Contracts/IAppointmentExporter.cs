using System.Collections.Generic;
using ZyncMaster.Core;

namespace ZyncMaster.CalExport;

public interface IAppointmentExporter
{
    string FileSuffix    { get; }   // "simple" or "complete"
    string FileExtension { get; }   // "txt" or "json"
    string Serialize(IReadOnlyList<AppointmentRecord> records, ExportContext context);
}
