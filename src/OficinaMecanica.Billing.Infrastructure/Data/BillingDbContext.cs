using MassTransit;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Domain.Common;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Infrastructure.Data;

// Optei por não despachar domain events aqui (como o monolito faz): o Billing não levanta eventos de domínio,
// as mensagens de integração saem pelo IIntegrationEventPublisher antes do SaveChanges e entram no outbox
// na mesma transação.
public class BillingDbContext : DbContext, IUnitOfWork
{
    public DbSet<Orcamento> Orcamentos => Set<Orcamento>();
    public DbSet<ItemOrcamento> ItensOrcamento => Set<ItemOrcamento>();
    public DbSet<Pagamento> Pagamentos => Set<Pagamento>();
    public DbSet<TentativaPagamento> TentativasPagamento => Set<TentativaPagamento>();
    public DbSet<Estorno> Estornos => Set<Estorno>();
    public DbSet<WebhookRecebido> WebhooksRecebidos => Set<WebhookRecebido>();
    public DbSet<CancelamentoPrevio> CancelamentosPrevios => Set<CancelamentoPrevio>();

    public BillingDbContext(DbContextOptions<BillingDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BillingDbContext).Assembly);

        // Tabelas do MassTransit: inbox (deduplicação por MessageId no consumidor), outbox e estado do outbox.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        base.OnModelCreating(modelBuilder);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<AggregateRoot>()
            .Where(e => e.State == EntityState.Modified)
            .ToList())
        {
            entry.Entity.IncrementVersion();
        }

        return await base.SaveChangesAsync(cancellationToken);
    }
}
