namespace TempBin.Guards;

public static class ResponseSecurity
{
    public static void Apply(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    }
}
