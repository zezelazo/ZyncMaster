using System;
using System.Collections.Generic;
using ZyncMaster.Core;

namespace ZyncMaster.Graph;

public interface IParticipantRenderer
{
    // Renders the full event body for a CREATE: participants block + an optional "invitation created"
    // line (the source creation time) + the description.
    string BuildBodyForCreate(string description, IReadOnlyList<ParticipantRecord> participants, DateTimeOffset? created = null);

    // Merges a fresh participants block into an existing body (preserving user edits
    // outside the marker delimiters). Used for UPDATE.
    string MergeIntoExistingBody(string existingBodyHtml, IReadOnlyList<ParticipantRecord> participants);
}
