namespace OficinaMecanica.Billing.Domain.Interfaces;

using OficinaMecanica.Billing.Domain.Entities;

public interface IWebhookRecebidoRepository
{
    Task<WebhookRecebido?> ObterPorRequestIdAsync(string requestId, CancellationToken cancellationToken = default);
    Task AdicionarAsync(WebhookRecebido webhook, CancellationToken cancellationToken = default);
}
