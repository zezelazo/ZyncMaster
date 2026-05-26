using System;
using System.Collections.Generic;
using SyncMaster.Core;

namespace SyncMaster.Graph;

public sealed class ImportPlanBuilder
{
    public IReadOnlyList<ImportPlanItem> Build(
        IReadOnlyList<AppointmentRecord>             records,
        IReadOnlyDictionary<string, ExistingEventLookup> existingMap)
    {
        if (records     == null) throw new ArgumentNullException(nameof(records));
        if (existingMap == null) throw new ArgumentNullException(nameof(existingMap));

        var plan = new List<ImportPlanItem>(records.Count);

        foreach (var record in records)
        {
            existingMap.TryGetValue(record.Id, out var existing);

            ImportPlanItem item;
            if (record.IsCancelled)
                item = existing != null
                    ? ImportPlanItem.ForCancel(record, existing.Id)
                    : ImportPlanItem.ForSkip(record);
            else
                item = existing != null
                    ? ImportPlanItem.ForUpdate(record, existing.Id, existing.BodyHtml ?? "")
                    : ImportPlanItem.ForCreate(record);

            plan.Add(item);
        }

        return plan;
    }
}
