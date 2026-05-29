namespace ZyncMaster.Core;

public sealed class ParticipantRecord
{
    public string Name     { get; init; } = "";
    public string Email    { get; init; } = "";
    public string Type     { get; init; } = "";      // "required", "optional", "resource"
    public string Response { get; init; } = "";      // "accepted", "tentative", "declined", "notResponded", "organizer", "none"
}
