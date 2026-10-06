using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Assister.Interactions;

public sealed class ClientAuthentication(IConfiguration Configuration, IDataProtectionProvider Protection)
{
    private readonly IDataProtector Protector = Protection.CreateProtector("Assister.RichClient.Session.v1");
    public string Owner(string ClientId) => Configuration[$"RichClients:Clients:{ClientId}:Owner"] ?? ClientId;
    public string? Authenticate(HttpContext Context)
    {
        var Header = Context.Request.Headers.Authorization.ToString();
        var Token = Header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? Header[7..] : null;
        if (Token is null && Context.Request.Cookies.TryGetValue("assister-client", out var Cookie))
        {
            try
            {
                var Parts = Protector.Unprotect(Cookie).Split('\n');
                if (Parts.Length == 2 && long.TryParse(Parts[0], out var Expires) && Expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) Token = Parts[1];
            }
            catch (CryptographicException) { }
        }
        return Token is null ? null : Identify(Token);
    }
    public string? Identify(string Token)
    {
        if (Token.Length is < 24 or > 512) return null;
        var Hash = SHA256.HashData(Encoding.UTF8.GetBytes(Token));
        foreach (var Client in Configuration.GetSection("RichClients:Clients").GetChildren())
        {
            var Expected = Client["Token"];
            if (Expected is { Length: >= 24 } && CryptographicOperations.FixedTimeEquals(Hash, SHA256.HashData(Encoding.UTF8.GetBytes(Expected)))) return Client.Key;
        }
        return null;
    }
    public void SignIn(HttpContext Context, string Token)
    {
        var Expires = DateTimeOffset.UtcNow.AddHours(8);
        Context.Response.Cookies.Append("assister-client", Protector.Protect(Expires.ToUnixTimeSeconds() + "\n" + Token),
            new CookieOptions { HttpOnly = true, Secure = Context.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/api/client", Expires = Expires });
    }
}
