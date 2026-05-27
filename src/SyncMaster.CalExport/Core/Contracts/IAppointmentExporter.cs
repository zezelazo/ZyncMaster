using System.Collections.Generic;
using SyncMaster.Core;

namespace SyncMaster.CalExport;

public interface IAppointmentExporter
{
    string FileSuffix    { get; }   // "simple" or "complete"
    string FileExtension { get; }   // "txt" or "json"
    string Serialize(IReadOnlyList<AppointmentRecord> records, ExportContext context);
}
