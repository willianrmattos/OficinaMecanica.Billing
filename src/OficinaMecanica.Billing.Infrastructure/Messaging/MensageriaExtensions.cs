using FluentValidation;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Domain.Exceptions;
using OficinaMecanica.Billing.Infrastructure.Data;
using OficinaMecanica.Billing.Infrastructure.Health;
using OficinaMecanica.Billing.Infrastructure.Messaging.Consumers;
using OficinaMecanica.Billing.Infrastructure.Options;
using OficinaMecanica.Contracts;

namespace OficinaMecanica.Billing.Infrastructure.Messaging;

public static class MensageriaExtensions
{
    // Pontos de troca para os testes (seção "Mensageria"): Transporte (RabbitMQ ou memória) e Persistência (outbox
    // do EF ou nenhuma, para rodar sobre EF InMemory, que não suporta o outbox transacional).
    public static IServiceCollection AddMensageria(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OpcoesDaMensageria>()
            .Bind(configuration.GetSection(OpcoesDaMensageria.Secao))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OpcoesDaMensageria>, ValidadorDasOpcoesDaMensageria>();
        services.AddOptions<OpcoesDoRabbitMq>().Bind(configuration.GetSection(OpcoesDoRabbitMq.Secao));

        var opcoes = configuration.GetSection(OpcoesDaMensageria.Secao).Get<OpcoesDaMensageria>() ?? new OpcoesDaMensageria();

        // Sem WaitUntilStarted, StartAsync devolve antes de endpoints e bindings existirem e uma mensagem
        // publicada logo em seguida pode se perder.
        services.Configure<MassTransitHostOptions>(host =>
        {
            host.WaitUntilStarted = true;
            host.StartTimeout = TimeSpan.FromSeconds(opcoes.TimeoutDeInicioSegundos);
        });

        // Fail-closed: só os dois valores conhecidos têm composição. Um valor sem nome (o binder aceita "0" ou "9" para um enum)
        // nunca cai no ramo "sem outbox" por exclusão, senão a API subiria em memória sem ninguém ter pedido.
        switch (opcoes.Persistencia)
        {
            case PersistenciaDaMensageria.EntityFramework:
                services.AddScoped<IBloqueioPorOrdemDeServico, BloqueioPorOrdemDeServicoSqlServer>();
                break;
            case PersistenciaDaMensageria.Nenhuma:
                services.AddSingleton<IBloqueioPorOrdemDeServico, SemBloqueioPorOrdemDeServico>();
                break;
            default:
                throw ValorIndefinido("Persistencia", opcoes.Persistencia, "EntityFramework ou Nenhuma");
        }

        if (opcoes.Transporte == TransporteDaMensageria.RabbitMq)
        {
            // Broker fora é Degraded (HTTP 200 em /health/ready): com o outbox a API continua aceitando escrita.
            services.AddHealthChecks().AddCheckComCache<RabbitMqHealthCheck>(
                "rabbitmq", failureStatus: HealthStatus.Degraded, tags: new[] { "ready" });
        }

        // O check do bus que o MassTransit registra também não pode tirar o pod de rotação por causa do broker, mesmo que
        // um dia ele passe a reportar a falta de conexão: rebaixo a falha dele para Degraded.
        services.PostConfigure<HealthCheckServiceOptions>(health =>
        {
            foreach (var registro in health.Registrations.Where(r => r.Name == "masstransit-bus"))
                registro.FailureStatus = HealthStatus.Degraded;
        });

        services.AddMassTransit(x =>
        {
            x.AddConsumer<GerarOrcamentoConsumer>();
            x.AddConsumer<SolicitarPagamentoConsumer>();
            x.AddConsumer<CancelarOrcamentoConsumer>();
            x.AddConsumer<CancelarPagamentoConsumer>();
            x.AddConsumer<EstornarPagamentoConsumer>();

            if (opcoes.Persistencia == PersistenciaDaMensageria.EntityFramework)
            {
                // O IsolationLevel do outbox fica no padrão do MassTransit: Serializable quebra o
                // BusOutboxDeliveryService (READPAST só vale em READ COMMITTED/REPEATABLE READ) e gera deadlocks.
                x.AddEntityFrameworkOutbox<BillingDbContext>(outbox =>
                {
                    outbox.UseSqlServer();
                    outbox.UseBusOutbox();
                    outbox.QueryDelay = TimeSpan.FromMilliseconds(opcoes.OutboxQueryDelayMs);
                });
            }

            switch (opcoes.Transporte)
            {
                case TransporteDaMensageria.RabbitMq:
                    x.UsingRabbitMq((contexto, cfg) =>
                    {
                        var rabbit = contexto.GetRequiredService<IOptions<OpcoesDoRabbitMq>>().Value;
                        cfg.Host(rabbit.Host, (ushort)rabbit.Port, rabbit.VirtualHost, host =>
                        {
                            host.Username(rabbit.Username ?? string.Empty);
                            host.Password(rabbit.Password ?? string.Empty);
                        });

                        ConfigurarEndpointDoBilling(contexto, cfg, opcoes);
                    });
                    break;
                case TransporteDaMensageria.EmMemoria:
                    x.UsingInMemory((contexto, cfg) => ConfigurarEndpointDoBilling(contexto, cfg, opcoes));
                    break;
                default:
                    throw ValorIndefinido("Transporte", opcoes.Transporte, "RabbitMq ou EmMemoria");
            }
        });

        return services;
    }

    private static InvalidOperationException ValorIndefinido(string chave, Enum valor, string validos) =>
        new($"Mensageria:{chave} tem um valor inválido ({valor}); use {validos}.");

    // Ordem dos filtros: retry (externo) -> outbox/inbox -> consumers. O retry fica por fora para cada tentativa
    // reprocessar a mensagem inteira em uma transação nova do outbox.
    private static void ConfigurarEndpointDoBilling(IBusRegistrationContext contexto, IReceiveConfigurator cfg, OpcoesDaMensageria opcoes)
    {
        cfg.ReceiveEndpoint(Filas.Billing, endpoint =>
        {
            endpoint.UseMessageRetry(retry =>
            {
                retry.Intervals(opcoes.IntervalosDeRetry());

                // Repetir não muda o resultado: entrada inválida, regra de negócio violada e assinatura inválida.
                // PagamentoDoGatewayIndisponivelException e as falhas técnicas do gateway ficam fora desta lista de
                // propósito, porque o gateway pode passar a responder na tentativa seguinte.
                retry.Ignore<ValidationException>();
                retry.Ignore<DomainException>();
                retry.Ignore<AssinaturaWebhookInvalidaException>();
            });

            if (opcoes.Persistencia == PersistenciaDaMensageria.EntityFramework)
                endpoint.UseEntityFrameworkOutbox<BillingDbContext>(contexto);

            endpoint.ConfigureConsumer<GerarOrcamentoConsumer>(contexto);
            endpoint.ConfigureConsumer<SolicitarPagamentoConsumer>(contexto);
            endpoint.ConfigureConsumer<CancelarOrcamentoConsumer>(contexto);
            endpoint.ConfigureConsumer<CancelarPagamentoConsumer>(contexto);
            endpoint.ConfigureConsumer<EstornarPagamentoConsumer>(contexto);
        });
    }
}
