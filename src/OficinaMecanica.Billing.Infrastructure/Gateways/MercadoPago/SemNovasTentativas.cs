using MercadoPago.Http;

namespace OficinaMecanica.Billing.Infrastructure.Gateways.MercadoPago;

// O SDK repete a chamada por conta própria em falha transitória, o que somaria vários timeouts com a transação do
// consumer aberta. Optei por desligar esse retry: a falha técnica sobe e o retry do MassTransit refaz a mensagem
// inteira, com intervalos e em transação nova.
internal sealed class SemNovasTentativas : IRetryStrategy
{
    public static readonly SemNovasTentativas Instancia = new();

    public RetryResponse ShouldRetry(HttpRequestMessage httpRequest, HttpResponseMessage httpResponse, bool hadRetryableError, int numberRetries) =>
        new(false, TimeSpan.Zero);
}
