using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace OficinaMecanica.Billing.Integration.Tests.Support;

// Faz o papel do OficinaMecanica.Seguranca nos testes: gera um par RSA, publica o JWKS por HTTP (para o retriever
// de verdade buscar a chave) e emite tokens RS256 com o emissor e a audiência que o Billing espera.
public sealed class EmissorDeTokens : IAsyncDisposable
{
    public const string Emissor = "OficinaMecanica.Seguranca";
    public const string Audiencia = "OficinaMecanica.Client";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly WebApplication _servidor;

    public string KeyId { get; } = Guid.NewGuid().ToString("N");
    public string JwksUri { get; private set; } = string.Empty;

    private EmissorDeTokens(WebApplication servidor)
    {
        _servidor = servidor;
    }

    public static async Task<EmissorDeTokens> IniciarAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var servidor = builder.Build();
        var emissor = new EmissorDeTokens(servidor);

        servidor.MapGet("/.well-known/jwks.json", () => Results.Content(emissor.JwksJson(), "application/json"));
        servidor.Urls.Add("http://127.0.0.1:0");
        await servidor.StartAsync();

        var endereco = servidor.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        emissor.JwksUri = $"{endereco}/.well-known/jwks.json";
        return emissor;
    }

    public string EmitirToken(
        string? emissor = Emissor,
        string? audiencia = Audiencia,
        TimeSpan? validade = null,
        string? keyId = null,
        RSA? chave = null)
    {
        var agora = DateTime.UtcNow;
        var duracao = validade ?? TimeSpan.FromMinutes(30);
        var chaveDeAssinatura = new RsaSecurityKey(chave ?? _rsa) { KeyId = keyId ?? KeyId };

        var token = new JwtSecurityToken(
            issuer: emissor,
            audience: audiencia,
            claims: new[] { new Claim(JwtRegisteredClaimNames.Sub, "staff-1"), new Claim(ClaimTypes.Role, "Admin") },
            notBefore: validade is { } v && v < TimeSpan.Zero ? agora + v - TimeSpan.FromMinutes(1) : agora.AddSeconds(-5),
            expires: agora + duracao,
            signingCredentials: new SigningCredentials(chaveDeAssinatura, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static RSA NovaChaveRsa() => RSA.Create(2048);

    private string JwksJson()
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = KeyId });
        var chave = new Dictionary<string, string?>
        {
            ["kty"] = jwk.Kty,
            ["use"] = "sig",
            ["alg"] = SecurityAlgorithms.RsaSha256,
            ["kid"] = KeyId,
            ["n"] = jwk.N,
            ["e"] = jwk.E,
        };
        return JsonSerializer.Serialize(new { keys = new[] { chave } });
    }

    public async ValueTask DisposeAsync()
    {
        await _servidor.StopAsync();
        await _servidor.DisposeAsync();
        _rsa.Dispose();
    }
}
