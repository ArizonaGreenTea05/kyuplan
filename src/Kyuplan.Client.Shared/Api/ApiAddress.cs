namespace Kyuplan.Client.Api;

/// <summary>What a person may type as the address of the API, and the one form it is kept in.</summary>
public static class ApiAddress
{
    /// <summary>
    /// An absolute <c>http://</c> or <c>https://</c> address without login, query or fragment (those do not belong in a base
    /// address, and a login would end up in a settings file in clear text). Surrounding blanks are ignored.
    /// <paramref name="address"/> is then the canonical form: lower-case scheme and host, a path (if the API sits below one) and always
    /// a trailing slash, so that "http://Planner:5100" and "http://planner:5100/" are the same address.
    /// </summary>
    public static bool TryNormalize(string? text, out string address)
    {
        address = "";
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.Host.Length == 0
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return false;

        address = uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/";
        return true;
    }
}
