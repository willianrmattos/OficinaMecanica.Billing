using MassTransit;
using OficinaMecanica.Billing.Application.Interfaces;

namespace OficinaMecanica.Billing.Infrastructure.Messaging;

// Implementação da porta de publicação sobre o MassTransit. Com o bus outbox ligado, Publish/Send só gravam a
// mensagem no DbContext do escopo e ela sai do banco junto com o SaveChanges do handler (ou do consumer).
// O MessageId do contrato é copiado para o do envelope: é ele que o inbox do consumidor usa para deduplicar, e
// os ids são determinísticos (derivados do agregado), então a mesma mensagem republicada tem sempre o mesmo id.
public sealed class MassTransitIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ISendEndpointProvider _sendEndpointProvider;

    public MassTransitIntegrationEventPublisher(IPublishEndpoint publishEndpoint, ISendEndpointProvider sendEndpointProvider)
    {
        _publishEndpoint = publishEndpoint;
        _sendEndpointProvider = sendEndpointProvider;
    }

    public Task PublishAsync<T>(T mensagem, CancellationToken cancellationToken = default) where T : class =>
        MessageIdDoContrato<T>.Obter(mensagem) is { } messageId
            ? _publishEndpoint.Publish(mensagem, Pipe.Execute<PublishContext<T>>(ctx => ctx.MessageId = messageId), cancellationToken)
            : _publishEndpoint.Publish(mensagem, cancellationToken);

    public async Task SendAsync<T>(string fila, T mensagem, CancellationToken cancellationToken = default) where T : class
    {
        var endpoint = await _sendEndpointProvider.GetSendEndpoint(new Uri($"queue:{fila}"));

        if (MessageIdDoContrato<T>.Obter(mensagem) is { } messageId)
            await endpoint.Send(mensagem, Pipe.Execute<SendContext<T>>(ctx => ctx.MessageId = messageId), cancellationToken);
        else
            await endpoint.Send(mensagem, cancellationToken);
    }
}
