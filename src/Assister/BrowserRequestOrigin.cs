namespace Assister;

internal static class BrowserRequestOrigin
{
    public static bool IsAllowed(HttpRequest Request)
    {
        // Browsers set this before a reverse proxy can rewrite the public scheme or authority.
        var FetchSite = Request.Headers["Sec-Fetch-Site"].ToString();
        if (FetchSite is "cross-site" or "same-site") return false;
        if (FetchSite == "same-origin") return true;
        if (!Request.Headers.TryGetValue("Origin", out var Origin)) return true;
        return Uri.TryCreate(Origin.ToString(), UriKind.Absolute, out var Source)
            && Uri.TryCreate($"{Request.Scheme}://{Request.Host}", UriKind.Absolute, out var Target)
            && Source.UserInfo.Length == 0 && Source.PathAndQuery == "/" && Source.Fragment.Length == 0
            && Source.Scheme == Target.Scheme && Source.IdnHost.Equals(Target.IdnHost, StringComparison.OrdinalIgnoreCase)
            && Source.Port == Target.Port;
    }
}
