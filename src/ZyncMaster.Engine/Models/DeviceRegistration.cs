using System;

namespace ZyncMaster.Engine;

// The result of a brokered device registration (POST /api/devices/register). The api key is the
// one-time full "keyId.secret" string the server returns exactly once — the App MUST persist it
// (DPAPI device.key) because it is unrecoverable. LeaseUntil is the initial lease the server grants
// so the just-registered device is immediately treated as "App running".
public sealed record DeviceRegistration
{
    public string DeviceId { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public DateTimeOffset? LeaseUntil { get; init; }
}
