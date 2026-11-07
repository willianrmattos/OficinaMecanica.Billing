using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OficinaMecanica.Billing.Domain.Entities;

namespace OficinaMecanica.Billing.Infrastructure.Data.Configurations;

public class ItemOrcamentoConfiguration : IEntityTypeConfiguration<ItemOrcamento>
{
    public void Configure(EntityTypeBuilder<ItemOrcamento> builder)
    {
        builder.ToTable("ItensOrcamento");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.Tipo).IsRequired();
        builder.Property(i => i.Nome).IsRequired().HasMaxLength(200);
        builder.Property(i => i.PrecoUnitario).HasColumnType("decimal(18,2)").IsRequired();
        builder.Property(i => i.Quantidade).IsRequired();

        builder.Ignore(i => i.Subtotal);
    }
}
