using MassTransit.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Domain.Interfaces;
using OficinaMecanica.Billing.Infrastructure.BackgroundServices;
using OficinaMecanica.Billing.Infrastructure.Data;
using OficinaMecanica.Billing.Infrastructure.Gateways;
using OficinaMecanica.Billing.Infrastructure.Gateways.MercadoPago;
using OficinaMecanica.Billing.Infrastructure.Messaging;
using OficinaMecanica.Billing.Infrastructure.Observability;
using OficinaMecanica.Billing.Infrastructure.Options;
using OficinaMecanica.Billing.Infrastructure.Repositories;
using OficinaMecanica.Billing.Infrastructure.Security;

namespace OficinaMecanica.Billing.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<BillingDbContext>((provedor, options) =>
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                sql =>
                {
                    sql.MigrationsAssembly(typeof(BillingDbContext).Assembly.FullName);

                    // O Azure SQL serverless pausa por inatividade e a primeira conexão depois do pause falha com erro
                    // transitório (40613) enquanto o banco acorda; o retry do EF cobre o caminho HTTP.
                    // Dentro de um consumer o EF não pode repetir sozinho: a transação é do MassTransit e uma
                    // repetição com o ChangeTracker já usado só produziria DbUpdateConcurrencyException. Lá quem
                    // repete é o UseMessageRetry, que reprocessa a mensagem inteira. O escopo de um consumer é o
                    // que tem IScopedConsumeContextProvider.HasContext verdadeiro.
                    var dentroDeConsumer = provedor.GetService<IScopedConsumeContextProvider>()?.HasContext ?? false;
                    if (!dentroDeConsumer)
                        sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
                }));

        services.AddScoped<IUnitOfWork>(provedor => provedor.GetRequiredService<BillingDbContext>());

        services.AddScoped<IOrcamentoRepository, OrcamentoRepository>();
        services.AddScoped<IPagamentoRepository, PagamentoRepository>();
        services.AddScoped<IWebhookRecebidoRepository, WebhookRecebidoRepository>();
        services.AddScoped<ICancelamentoPrevioRepository, CancelamentoPrevioRepository>();

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<MetricasDoBilling>();
        services.AddSingleton<IMetricasDoBilling>(provedor => provedor.GetRequiredService<MetricasDoBilling>());

        services.AddScoped<IIntegrationEventPublisher, MassTransitIntegrationEventPublisher>();

        services.AddPagamento(configuration);
        services.AddReconciliador(configuration);
        services.AddAutenticacaoJwt();

        return services;
    }

    // O provedor é resolvido na primeira vez que o gateway é pedido (e não no registro), para enxergar a configuração
    // final do host. As credenciais do Mercado Pago vêm só de configuração (env/Key Vault), nunca do repositório.
    internal static IServiceCollection AddPagamento(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OpcoesDoMercadoPago>().Bind(configuration.GetSection(OpcoesDoMercadoPago.Secao));
        services.AddOptions<OpcoesDoGatewayFake>().Bind(configuration.GetSection(OpcoesDoGatewayFake.Secao));
        services.AddOptions<OpcoesDePagamento>()
            .Bind(configuration.GetSection(OpcoesDePagamento.Secao))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OpcoesDePagamento>, ValidadorDasOpcoesDePagamento>();

        services.AddSingleton<FakePagamentoGateway>();
        services.AddHttpClient<MercadoPagoGateway>((provedor, cliente) =>
            cliente.Timeout = TimeSpan.FromSeconds(provedor.GetRequiredService<IOptions<OpcoesDoMercadoPago>>().Value.TimeoutSegundos));

        services.AddScoped<IPagamentoGateway>(provedor =>
            provedor.GetRequiredService<IOptions<OpcoesDePagamento>>().Value.Provider == ProvedorDePagamento.Fake
                ? provedor.GetRequiredService<FakePagamentoGateway>()
                : provedor.GetRequiredService<MercadoPagoGateway>());

        services.AddSingleton<IAssinaturaWebhookValidator>(provedor =>
        {
            var pagamento = provedor.GetRequiredService<IOptions<OpcoesDePagamento>>().Value;
            var mercadoPago = provedor.GetRequiredService<IOptions<OpcoesDoMercadoPago>>().Value;
            var segredo = pagamento.Provider == ProvedorDePagamento.Fake
                ? provedor.GetRequiredService<IOptions<OpcoesDoGatewayFake>>().Value.WebhookSecret
                : mercadoPago.WebhookSecret;

            return new AssinaturaWebhookMercadoPago(
                segredo ?? string.Empty,
                TimeSpan.FromSeconds(mercadoPago.ToleranciaDaAssinaturaSegundos),
                provedor.GetRequiredService<TimeProvider>());
        });

        return services;
    }

    internal static IServiceCollection AddReconciliador(this IServiceCollection services, IConfiguration configuration)
    {
        // Optei por ValidateOnStart em vez de deixar o `.Value` do construtor do reconciliador falhar: a validação roda antes de
        // qualquer hosted service iniciar, então o MassTransit nem tenta conectar e a falha vem junto com as demais de
        // configuração, com o reconciliador habilitado ou não.
        services.AddOptions<OpcoesDoBilling>()
            .Bind(configuration.GetSection(OpcoesDoBilling.Secao))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OpcoesDoBilling>, ValidadorDasOpcoesDoBilling>();

        services.AddHostedService<ReconciliadorDePagamentosBackgroundService>();

        return services;
    }
}
