using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OficinaMecanica.Billing.Domain.Entities;

namespace OficinaMecanica.Billing.Infrastructure.Data.Configurations;

public class WebhookRecebidoConfiguration : IEntityTypeConfiguration<WebhookRecebido>
{
    public void Configure(EntityTypeBuilder<WebhookRecebido> builder)
    {
        builder.ToTable("WebhooksRecebidos");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();

        builder.Property(w => w.RequestId).IsRequired().HasMaxLength(128);
        builder.HasIndex(w => w.RequestId).IsUnique();

        builder.Property(w => w.DataId).IsRequired().HasMaxLength(64);
        builder.Property(w => w.RecebidoEm).IsRequired();
        builder.Property(w => w.Processado).IsRequired();
    }
}
