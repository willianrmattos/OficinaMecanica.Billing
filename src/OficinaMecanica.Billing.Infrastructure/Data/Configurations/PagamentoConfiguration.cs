using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OficinaMecanica.Billing.Domain.Entities;

namespace OficinaMecanica.Billing.Infrastructure.Data.Configurations;

public class PagamentoConfiguration : IEntityTypeConfiguration<Pagamento>
{
    public void Configure(EntityTypeBuilder<Pagamento> builder)
    {
        builder.ToTable("Pagamentos");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.OrcamentoId).IsRequired();
        builder.HasIndex(p => p.OrcamentoId);

        // Um pagamento por ordem de serviço: é o que faz o SolicitarPagamento duplicado ou concorrente
        // esbarrar na constraint em vez de gerar uma segunda cobrança.
        builder.Property(p => p.OrdemDeServicoId).IsRequired();
        builder.HasIndex(p => p.OrdemDeServicoId).IsUnique();

        builder.Property(p => p.Valor).HasColumnType("decimal(18,2)").IsRequired();
        builder.Property(p => p.Status).IsRequired();
        builder.Property(p => p.PreferenceId).HasMaxLength(100);
        builder.Property(p => p.LinkPagamento).HasMaxLength(1000);
        builder.Property(p => p.ExpiraEm).IsRequired();
        builder.Property(p => p.CriadoEm).IsRequired();
        builder.Property(p => p.GatewayPaymentIdAprovado).HasMaxLength(64);
        builder.Property(p => p.AprovadoEm);
        builder.Property(p => p.AprovacaoTardia).IsRequired();
        builder.Property(p => p.MotivoCancelamento).HasMaxLength(500);
        builder.Property(p => p.Version).IsConcurrencyToken();

        builder.HasMany(p => p.Tentativas)
            .WithOne()
            .HasForeignKey("PagamentoId")
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Tentativas).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasOne(p => p.Estorno)
            .WithOne()
            .HasForeignKey<Estorno>("PagamentoId")
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.Ignore(p => p.EstornoPendente);
    }
}
