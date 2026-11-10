using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OficinaMecanica.Billing.Domain.Entities;

namespace OficinaMecanica.Billing.Infrastructure.Data.Configurations;

public class OrcamentoConfiguration : IEntityTypeConfiguration<Orcamento>
{
    public void Configure(EntityTypeBuilder<Orcamento> builder)
    {
        builder.ToTable("Orcamentos");
        builder.HasKey(o => o.Id);

        // O Id nasce no construtor do domínio: sem ValueGeneratedNever o EF trata o filho novo de um agregado
        // já rastreado como existente (Modified) e emite UPDATE em vez de INSERT.
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.OrdemDeServicoId).IsRequired();
        builder.HasIndex(o => o.OrdemDeServicoId).IsUnique();

        builder.Property(o => o.Numero).IsRequired().HasMaxLength(30);
        builder.Property(o => o.Status).IsRequired();
        builder.Property(o => o.ValorTotal).HasColumnType("decimal(18,2)").IsRequired();
        builder.Property(o => o.ValidoAte).IsRequired();
        builder.Property(o => o.CriadoEm).IsRequired();
        builder.Property(o => o.Laudo).HasMaxLength(4000);
        builder.Property(o => o.MotivoCancelamento).HasMaxLength(500);
        builder.Property(o => o.Version).IsConcurrencyToken();

        builder.HasMany(o => o.Itens)
            .WithOne()
            .HasForeignKey("OrcamentoId")
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(o => o.Itens).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
