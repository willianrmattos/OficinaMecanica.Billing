using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace OficinaMecanica.Billing.Infrastructure.Health;

// Seção "Health". O cache existe porque /health/ready é anônimo e, atrás do wildcard da APIM, qualquer cliente da internet
// o alcança: sem cache, cada requisição abriria uma conexão SQL e uma AMQP. Com ele o custo fica limitado a uma avaliação
// de cada dependência por janela, não importa a taxa de requisições.
public sealed class OpcoesDaProntidao
{
    public const string Secao = "Health";
    public const int CachePadraoEmSegundos = 5;

    // Menor que o periodSeconds da readiness (30 s) de propósito: cada probe do Kubernetes cai fora da janela e vê um
    // resultado novo. Zero (ou negativo) desliga o cache; os testes de infraestrutura precisam ver cada mudança na hora.
    public int ProntidaoCacheSegundos { get; set; } = CachePadraoEmSegundos;
}

// Último resultado de um check e até quando vale, compartilhado entre as chamadas (um por registro). A trava faz as
// requisições simultâneas esperarem a primeira avaliação em vez de abrirem uma conexão cada.
public sealed class EstadoDoCacheDeProntidao
{
    internal SemaphoreSlim Trava { get; } = new(1, 1);
    internal HealthCheckResult? Resultado { get; set; }
    internal DateTimeOffset ValidoAte { get; set; }
}

public sealed class HealthCheckComCache : IHealthCheck
{
    private readonly IHealthCheck _interno;
    private readonly EstadoDoCacheDeProntidao _estado;
    private readonly IOptions<OpcoesDaProntidao> _opcoes;
    private readonly TimeProvider _relogio;

    public HealthCheckComCache(
        IHealthCheck interno, EstadoDoCacheDeProntidao estado, IOptions<OpcoesDaProntidao> opcoes, TimeProvider relogio)
    {
        _interno = interno;
        _estado = estado;
        _opcoes = opcoes;
        _relogio = relogio;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var validade = TimeSpan.FromSeconds(Math.Max(0, _opcoes.Value.ProntidaoCacheSegundos));
        if (validade == TimeSpan.Zero)
            return await _interno.CheckHealthAsync(context, cancellationToken);

        await _estado.Trava.WaitAsync(cancellationToken);
        try
        {
            if (_estado.Resultado is { } guardado && _relogio.GetUtcNow() < _estado.ValidoAte)
                return guardado;

            // Uma exceção (o chamador cancelou) sobe sem gravar nada: o cache só guarda uma medição que terminou.
            var resultado = await _interno.CheckHealthAsync(context, cancellationToken);
            _estado.Resultado = resultado;
            _estado.ValidoAte = _relogio.GetUtcNow() + validade;
            return resultado;
        }
        finally
        {
            _estado.Trava.Release();
        }
    }
}

public static class HealthChecksComCacheExtensions
{
    // Registra o check real (criado a cada avaliação, no escopo da própria, como o AddCheck<T> faz) dentro do cache. O
    // estado do cache é um só por registro: é criado aqui, uma vez, e capturado pela fábrica.
    public static IHealthChecksBuilder AddCheckComCache<TCheck>(
        this IHealthChecksBuilder builder, string nome, HealthStatus? failureStatus, IEnumerable<string> tags)
        where TCheck : class, IHealthCheck
    {
        var estado = new EstadoDoCacheDeProntidao();

        return builder.Add(new HealthCheckRegistration(
            nome,
            provedor => new HealthCheckComCache(
                ActivatorUtilities.GetServiceOrCreateInstance<TCheck>(provedor),
                estado,
                provedor.GetRequiredService<IOptions<OpcoesDaProntidao>>(),
                provedor.GetService<TimeProvider>() ?? TimeProvider.System),
            failureStatus,
            tags));
    }
}
