namespace MonitorApp.Android.Core;

public static class ControllerUriPolicy
{
    public static Uri ParseHttps(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "Controller address must be an absolute HTTPS origin without credentials, query, or fragment.",
                nameof(value));
        }

        var builder = new UriBuilder(uri)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }
}
