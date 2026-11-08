using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace OficinaMecanica.Billing.Infrastructure.Data;

// O SQL Server é a dependência sem a qual a API não aceita escrita (o outbox mora nele): com o banco fora, o pod sai
// de rotação. O tempo é limitado porque o retry do EF (EnableRetryOnFailure) esperaria vários segundos antes de
// desistir de um banco parado e a probe não pode ficar pendurada.
public sealed class SqlServerHealthCheck : IHealthCheck
{
    private static readonly TimeSpan LimiteDaConexao = TimeSpan.FromSeconds(3);

    private readonly BillingDbContext _db;

    public SqlServerHealthCheck(BillingDbContext db)
    {
        _db = db;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(LimiteDaConexao);

        try
        {
            if (await _db.Database.CanConnectAsync(limite.Token))
                return HealthCheckResult.Healthy("Conexão com o banco de dados estabelecida.");

            // O CanConnectAsync engole o cancelamento e devolve falso: se quem cancelou foi o chamador (a probe abortou),
            // propago o cancelamento em vez de relatar um banco fora do ar que não foi medido.
            cancellationToken.ThrowIfCancellationRequested();
            return HealthCheckResult.Unhealthy("Não foi possível conectar ao banco de dados.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Não foi possível conectar ao banco de dados.", ex);
        }
    }
}
