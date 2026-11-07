using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Interfaces;
using OficinaMecanica.Billing.Infrastructure.Data;

namespace OficinaMecanica.Billing.Infrastructure.Repositories;

public class WebhookRecebidoRepository : IWebhookRecebidoRepository
{
    private readonly BillingDbContext _context;

    public WebhookRecebidoRepository(BillingDbContext context)
    {
        _context = context;
    }

    public Task<WebhookRecebido?> ObterPorRequestIdAsync(string requestId, CancellationToken cancellationToken = default) =>
        _context.WebhooksRecebidos.FirstOrDefaultAsync(w => w.RequestId == requestId, cancellationToken);

    public async Task AdicionarAsync(WebhookRecebido webhook, CancellationToken cancellationToken = default) =>
        await _context.WebhooksRecebidos.AddAsync(webhook, cancellationToken);
}
