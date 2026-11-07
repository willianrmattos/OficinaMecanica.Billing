using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace OficinaMecanica.Billing.Integration.Tests.Support;

public static class ConfiguracaoDeTeste
{
    public const string SegredoDoFake = "segredo-fake-somente-de-teste";
    public const string SegredoDoMercadoPago = "segredo-mp-somente-de-teste";

    // Conexão com um servidor que não existe: os testes que usam isto só montam o DbContext, nunca abrem conexão.
    public const string ConexaoInerte = "Server=tcp:servidor-inexistente.invalid,1433;Database=BillingDb;Encrypt=True;TrustServerCertificate=True;";

    public static IConfiguration Criar(params (string Chave, string? Valor)[] valores) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(valores.ToDictionary(v => v.Chave, v => v.Valor))
            .Build();

    public static IConfiguration ComFake() => Criar(
        ("ConnectionStrings:DefaultConnection", ConexaoInerte),
        ("Pagamento:Provider", "Fake"),
        ("Fake:WebhookSecret", SegredoDoFake),
        ("JwtSettings:JwksUri", "http://localhost:1/jwks.json"));

    public static IConfiguration ComMercadoPago() => Criar(
        ("ConnectionStrings:DefaultConnection", ConexaoInerte),
        ("Pagamento:Provider", "MercadoPago"),
        ("MercadoPago:AccessToken", "TEST-token"),
        ("MercadoPago:WebhookSecret", SegredoDoMercadoPago),
        ("MercadoPago:NotificationBaseUrl", "https://apimfiap.azure-api.net/billingserver"),
        ("JwtSettings:JwksUri", "http://localhost:1/jwks.json"));

    public static IHostEnvironment Ambiente(string nome)
    {
        var ambiente = new Moq.Mock<IHostEnvironment>();
        ambiente.SetupGet(a => a.EnvironmentName).Returns(nome);
        return ambiente.Object;
    }
}
