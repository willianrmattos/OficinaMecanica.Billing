using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OficinaMecanica.Billing.Domain.Entities;

namespace OficinaMecanica.Billing.Infrastructure.Data.Configurations;

public class CancelamentoPrevioConfiguration : IEntityTypeConfiguration<CancelamentoPrevio>
{
    public void Configure(EntityTypeBuilder<CancelamentoPrevio> builder)
    {
        builder.ToTable("CancelamentosPrevios");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.OrdemDeServicoId).IsRequired();
        builder.Property(c => c.Tipo).IsRequired();
        builder.HasIndex(c => new { c.OrdemDeServicoId, c.Tipo }).IsUnique();

        builder.Property(c => c.Motivo).IsRequired().HasMaxLength(500);
        builder.Property(c => c.RegistradoEm).IsRequired();
    }
}
