using Bogus;
using OficinaMecanica.Billing.Domain.Entities;

namespace OficinaMecanica.Billing.Tests.Common.Builders;

public class WebhookRecebidoBuilder
{
    private static readonly Faker Faker = new("pt_BR");

    private string _requestId = Guid.NewGuid().ToString();
    private string _dataId = Faker.Random.Long(1_000_000_000, 9_999_999_999).ToString();
    private DateTimeOffset _recebidoEm = RelogioFixo.InstanteBase;

    public WebhookRecebidoBuilder ComRequestId(string requestId) { _requestId = requestId; return this; }
    public WebhookRecebidoBuilder ComDataId(string dataId) { _dataId = dataId; return this; }
    public WebhookRecebidoBuilder RecebidoEm(DateTimeOffset recebidoEm) { _recebidoEm = recebidoEm; return this; }

    public WebhookRecebido Recebido() => new(_requestId, _dataId, _recebidoEm);

    public WebhookRecebido Processado()
    {
        var webhook = Recebido();
        webhook.MarcarComoProcessado();
        return webhook;
    }
}
