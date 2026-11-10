using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OficinaMecanica.Billing.Domain.Entities;

namespace OficinaMecanica.Billing.Infrastructure.Data.Configurations;

public class TentativaPagamentoConfiguration : IEntityTypeConfiguration<TentativaPagamento>
{
    public void Configure(EntityTypeBuilder<TentativaPagamento> builder)
    {
        builder.ToTable("TentativasPagamento");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        // Um pagamento do gateway pertence a um único Pagamento: a constraint protege a deduplicação por payment.id
        // mesmo com duas notificações concorrentes.
        builder.Property(t => t.GatewayPaymentId).IsRequired().HasMaxLength(64);
        builder.HasIndex(t => t.GatewayPaymentId).IsUnique();

        builder.Property(t => t.StatusGateway).IsRequired().HasMaxLength(50);
        builder.Property(t => t.StatusDetalhe).HasMaxLength(100);
        builder.Property(t => t.ValorGateway).HasColumnType("decimal(18,2)").IsRequired();
        builder.Property(t => t.AtualizadoEm).IsRequired();
        builder.Property(t => t.GatewayRefundId).HasMaxLength(64);

        builder.Ignore(t => t.Aprovada);
    }
}
