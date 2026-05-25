using System;
using SyncMaster.Core;

namespace SyncMaster.CalImport;

public sealed class ImportPlanItem
{
    private ImportPlanItem(AppointmentRecord record, ImportAction action, string? existingEventId, string? existingBodyHtml)
    {
        Record           = record ?? throw new ArgumentNullException(nameof(record));
        Action           = action;
        ExistingEventId  = existingEventId;
        ExistingBodyHtml = existingBodyHtml;
    }

    public AppointmentRecord Record           { get; }
    public ImportAction      Action           { get; }
    public string?           ExistingEventId  { get; }
    public string?           ExistingBodyHtml { get; }

    public static ImportPlanItem ForCreate(AppointmentRecord record)
        => new ImportPlanItem(record, ImportAction.Create, existingEventId: null, existingBodyHtml: null);

    public static ImportPlanItem ForUpdate(AppointmentRecord record, string existingEventId, string existingBodyHtml)
    {
        if (string.IsNullOrEmpty(existingEventId))
            throw new ArgumentException("existingEventId must not be null or empty for ImportAction.Update", nameof(existingEventId));
        if (existingBodyHtml == null)
            throw new ArgumentNullException(nameof(existingBodyHtml), "existingBodyHtml must not be null for ImportAction.Update (use empty string if absent)");

        return new ImportPlanItem(record, ImportAction.Update, existingEventId, existingBodyHtml);
    }

    public static ImportPlanItem ForCancel(AppointmentRecord record, string existingEventId)
    {
        if (string.IsNullOrEmpty(existingEventId))
            throw new ArgumentException("existingEventId must not be null or empty for ImportAction.Cancel", nameof(existingEventId));

        return new ImportPlanItem(record, ImportAction.Cancel, existingEventId, existingBodyHtml: null);
    }

    public static ImportPlanItem ForSkip(AppointmentRecord record)
        => new ImportPlanItem(record, ImportAction.Skip, existingEventId: null, existingBodyHtml: null);
}
