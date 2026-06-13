namespace ZyncMaster.Server;

public sealed record TokenResult
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
    public string? UserPrincipalName { get; init; }

    // Identity parsed from the id_token, used by /connect/callback to upsert the user and
    // issue the panel cookie. Subject is the stable id (oid, falling back to sub).
    public string? Subject { get; init; }
    public string? Email { get; init; }
    public string? DisplayName { get; init; }

    // The id_token "email_verified" claim when the IdP emitted it (a JSON bool or the
    // string "true"/"false"). Null when the claim was absent. The endpoint layer — not this
    // record — decides the final emailVerified trust value (see IdentityConnectEndpoints).
    public bool? EmailVerified { get; init; }

    // The id_token "tid" (tenant id). For Microsoft personal accounts (MSA / "consumers") this
    // is the fixed MSA tenant 9188040d-6c67-4c5b-b112-36a304b66dad, where the email IS the
    // account's verified sign-in identity. For work/school (AAD) it is the org tenant, where the
    // raw email claim is NOT authoritative (an attacker can set it in a free tenant — nOAuth).
    public string? TenantId { get; init; }

    // The id_token "xms_edov" ("email domain owner verified") claim — Microsoft's explicit AAD
    // mitigation that the issuing tenant owns the email's domain. True only when AAD vouches for
    // the email; absent/false means the email must NOT be trusted for account-linking.
    public bool? EmailDomainOwnerVerified { get; init; }
}

public sealed record ConnectedAccount
{
    public required string UserPrincipalName { get; init; }
    public required string EncryptedRefreshToken { get; init; }
    public DateTimeOffset ConnectedUtc { get; init; }
}
