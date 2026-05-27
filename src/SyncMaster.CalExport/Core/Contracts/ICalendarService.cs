using System.Collections.Generic;
using SyncMaster.Core;

namespace SyncMaster.CalExport;

public interface ICalendarService
{
    IReadOnlyList<CalendarFolderInfo> GetCalendarFolders();
    IReadOnlyList<AppointmentRecord>  GetAppointments(ExportParameters parameters);
}
