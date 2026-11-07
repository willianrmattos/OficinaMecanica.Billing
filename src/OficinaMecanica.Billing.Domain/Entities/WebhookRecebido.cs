using OficinaMecanica.Billing.Domain.Common;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Entities;

public class WebhookRecebido : Entity
{
    public string RequestId { get; private set; } = string.Empty;
    public string DataId { get; private set; } = string.Empty;
    public DateTimeOffset RecebidoEm { get; private set; }
    public bool Processado { get; private set; }

    private WebhookRecebido() { } // EF Core

    public WebhookRecebido(string requestId, string dataId, DateTimeOffset recebidoEm)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new DomainException("O identificador da requisição do webhook é obrigatório.");

        if (string.IsNullOrWhiteSpace(dataId))
            throw new DomainException("O identificador do dado do webhook é obrigatório.");

        RequestId = requestId;
        DataId = dataId;
        RecebidoEm = recebidoEm;
    }

    public void MarcarComoProcessado() => Processado = true;
}
