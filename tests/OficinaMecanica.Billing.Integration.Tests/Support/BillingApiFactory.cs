using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using OficinaMecanica.Billing.Infrastructure.Data;
using OficinaMecanica.Billing.Infrastructure.Gateways;

namespace OficinaMecanica.Billing.Integration.Tests.Support;

// Sobe a API de verdade (Program.cs, controllers, middleware, Swagger, JWT, MediatR) trocando só os pontos de
// persistência e transporte: EF InMemory no lugar do SQL e o transporte em memória no lugar do RabbitMQ, sem outbox.
public sealed class BillingApiFactory : WebApplicationFactory<Program>
{
    public const string SegredoDoFake = ConfiguracaoDeTeste.SegredoDoFake;

    private readonly string _nomeDoBanco = $"billing-{Guid.NewGuid():N}";
    private readonly Dictionary<string, string?> _configuracao = new();
    private string _ambiente = "Development";

    public FakeTimeProvider Relogio { get; } = new(DateTimeOffset.UtcNow);

    public FakePagamentoGateway Gateway => Services.GetRequiredService<FakePagamentoGateway>();

    public BillingApiFactory()
    {
        _configuracao["Pagamento:Provider"] = "Fake";
        _configuracao["Fake:WebhookSecret"] = SegredoDoFake;
        _configuracao["Mensageria:Transporte"] = "EmMemoria";
        _configuracao["Mensageria:Persistencia"] = "Nenhuma";
        _configuracao["Billing:ReconciliadorHabilitado"] = "false";
        _configuracao["JwtSettings:Issuer"] = EmissorDeTokens.Emissor;
        _configuracao["JwtSettings:Audience"] = EmissorDeTokens.Audiencia;
        _configuracao["JwtSettings:JwksUri"] = "http://127.0.0.1:1/.well-known/jwks.json";
    }

    public BillingApiFactory Com(string chave, string? valor)
    {
        _configuracao[chave] = valor;
        return this;
    }

    public BillingApiFactory EmAmbiente(string ambiente)
    {
        _ambiente = ambiente;
        return this;
    }

    public BillingApiFactory ComJwks(EmissorDeTokens emissor) => Com("JwtSettings:JwksUri", emissor.JwksUri);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_ambiente);
        foreach (var (chave, valor) in _configuracao)
            builder.UseSetting(chave, valor);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<BillingDbContext>>();
            services.RemoveAll<DbContextOptions<BillingDbContext>>();
            services.AddDbContext<BillingDbContext>(opcoes => opcoes.UseInMemoryDatabase(_nomeDoBanco));

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Relogio);
        });
    }

    public async Task<T> ComBancoAsync<T>(Func<BillingDbContext, Task<T>> acao)
    {
        using var escopo = Services.CreateScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<BillingDbContext>());
    }

    public Task ComBancoAsync(Func<BillingDbContext, Task> acao) =>
        ComBancoAsync<int>(async db =>
        {
            await acao(db);
            return 0;
        });
}
