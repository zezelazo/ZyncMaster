using System.Collections.Generic;
using SyncMaster.Core;

namespace SyncMaster.Graph;

public interface IParticipantRenderer
{
    // Renders the full event body for a CREATE: description + participants block.
    string BuildBodyForCreate(string description, IReadOnlyList<ParticipantRecord> participants);

    // Merges a fresh participants block into an existing body (preserving user edits
    // outside the marker delimiters). Used for UPDATE.
    string MergeIntoExistingBody(string existingBodyHtml, IReadOnlyList<ParticipantRecord> participants);
}
