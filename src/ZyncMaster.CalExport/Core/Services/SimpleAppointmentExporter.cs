using System.Collections.Generic;
using ZyncMaster.Core;

namespace ZyncMaster.CalExport;

public sealed class SimpleAppointmentExporter : IAppointmentExporter
{
    public string FileSuffix    => "simple";
    public string FileExtension => "txt";

    // Delegates to the shared formatter in ZyncMaster.Core so the COM export and the
    // Server's Graph-source export emit a byte-identical Simple-mode .txt. The column
    // format lives in exactly one place (SimpleAppointmentFormatter.FormatLine).
    public string Serialize(IReadOnlyList<AppointmentRecord> records, ExportContext context)
        => SimpleAppointmentFormatter.Format(records);
}
