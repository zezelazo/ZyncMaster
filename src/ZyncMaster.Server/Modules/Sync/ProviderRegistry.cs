namespace ZyncMaster.Server;

// Resolves calendar readers and writers for an endpoint based on its Provider string.
//
//   Provider == "MicrosoftGraph"  → reader + writer backed by a per-account Graph provider.
//   Provider == "OutlookCom"      → no server reader (events arrive via the push endpoint);
//                                    the writer is still Microsoft Graph because OutlookCom
//                                    is never a destination in this milestone.
//
// Construction of the per-account Graph provider is delegated to a factory keyed on the
// endpoint's AccountRef so each account gets its own token provider + calendar target.
public sealed class ProviderRegistry
{
    public const string MicrosoftGraph = "MicrosoftGraph";
    public const string OutlookCom = "OutlookCom";

    private readonly Func<string?, MicrosoftGraphProvider> _graphFactory;

    public ProviderRegistry(Func<string?, MicrosoftGraphProvider> graphFactory)
    {
        _graphFactory = graphFactory ?? throw new ArgumentNullException(nameof(graphFactory));
    }

    // The reader for a source endpoint, or null when the provider has no server-side read
    // (OutlookCom). Callers that get null must route through the push endpoint instead.
    public ICalendarReader? ResolveReader(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.Provider switch
        {
            MicrosoftGraph => _graphFactory(endpoint.AccountRef),
            _ => null,
        };
    }

    // The writer for a destination endpoint. Always Microsoft Graph: the destination
    // account's calendar is written through Graph regardless of where the source came from.
    public ICalendarWriter ResolveWriter(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return _graphFactory(endpoint.AccountRef);
    }
}
