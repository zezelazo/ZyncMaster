using System.Collections.Generic;
using ZyncMaster.Core;

namespace ZyncMaster.CalExport;

public interface ICalendarService
{
    IReadOnlyList<CalendarFolderInfo> GetCalendarFolders();
    IReadOnlyList<AppointmentRecord>  GetAppointments(ExportParameters parameters);
}
