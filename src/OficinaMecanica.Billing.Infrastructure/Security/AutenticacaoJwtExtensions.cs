using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;

namespace OficinaMecanica.Billing.Infrastructure.Security;

public static class AutenticacaoJwtExtensions
{
    // Só valida tokens RS256 emitidos pelo OficinaMecanica.Seguranca, buscando a chave pública pelo JWKS: nenhum
    // segredo compartilhado e nenhuma emissão de token aqui. As opções são montadas na primeira resolução (e não
    // no registro) para enxergar a configuração final do host.
    public static IServiceCollection AddAutenticacaoJwt(this IServiceCollection services)
    {
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfiguration>((options, configuration) =>
            {
                var jwt = configuration.GetSection("JwtSettings");
                var jwksUri = jwt["JwksUri"]
                    ?? throw new InvalidOperationException("JwtSettings:JwksUri é obrigatório.");

                // O HttpDocumentRetriever recusa http:// por padrão. Em produção o JwksUri passa pela APIM (https),
                // mas em dev local o Seguranca roda sem TLS, então só nesse caso a exigência é relaxada.
                var documentRetriever = new HttpDocumentRetriever
                {
                    RequireHttps = jwksUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                };
                var configManager = new ConfigurationManager<JsonWebKeySet>(jwksUri, new JsonWebKeySetRetriever(), documentRetriever);

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt["Issuer"],
                    ValidAudience = jwt["Audience"],
                    IssuerSigningKeyResolver = (_, _, kid, _) =>
                        configManager.GetConfigurationAsync().GetAwaiter().GetResult().Keys.Where(k => k.Kid == kid),
                    ClockSkew = TimeSpan.Zero
                };
            });

        services.AddAuthorization();

        return services;
    }
}
