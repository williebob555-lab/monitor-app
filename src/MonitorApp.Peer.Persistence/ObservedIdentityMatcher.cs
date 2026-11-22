namespace MonitorApp.Peer.Persistence;

public static class ObservedIdentityMatcher
{
    public static bool ShareStrongIdentity(
        IEnumerable<IdentityClaim> left,
        IEnumerable<IdentityClaim> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var leftClaims = left
            .Where(IsUsableStrongClaim)
            .Select(claim => (claim.ClaimType, claim.NormalizedValue))
            .ToHashSet();

        return right
            .Where(IsUsableStrongClaim)
            .Any(claim => leftClaims.Contains((claim.ClaimType, claim.NormalizedValue)));
    }

    public static bool IsNetworkAddressClaim(string claimType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);

        var lastSegment = claimType
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Last()
            .Replace('-', '_')
            .ToLowerInvariant();
        return lastSegment is
            "ip" or
            "ip_address" or
            "ipv4" or
            "ipv4_address" or
            "ipv6" or
            "ipv6_address" or
            "network_address";
    }

    private static bool IsUsableStrongClaim(IdentityClaim claim) =>
        claim.Strength == IdentityStrength.Strong &&
        !IsNetworkAddressClaim(claim.ClaimType);
}
