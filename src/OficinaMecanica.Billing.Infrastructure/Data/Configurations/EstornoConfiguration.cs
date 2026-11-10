using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OficinaMecanica.Billing.Domain.Entities;

namespace OficinaMecanica.Billing.Infrastructure.Data.Configurations;

public class EstornoConfiguration : IEntityTypeConfiguration<Estorno>
{
    public void Configure(EntityTypeBuilder<Estorno> builder)
    {
        builder.ToTable("Estornos");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.GatewayRefundId).HasMaxLength(64);
        builder.Property(e => e.Tentativas).IsRequired();
        builder.Property(e => e.Erro).HasMaxLength(500);
        builder.Property(e => e.Status).IsRequired();
        builder.Property(e => e.AbandonadoEm);
    }
}
