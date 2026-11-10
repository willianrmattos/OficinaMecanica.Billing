using OficinaMecanica.Billing.Application.DTOs;

namespace OficinaMecanica.Billing.Application.Interfaces;

public interface IPagamentoGateway
{
    // Falhas técnicas (timeout, 5xx) propagam como exceção qualquer; uma recusa permanente do gateway (4xx
    // definitivo) deve ser traduzida pelo adaptador em PagamentoRecusadoPeloGatewayException.
    Task<PreferenciaGatewayDto> CriarPreferenciaAsync(CriarPreferenciaGatewayRequest request, CancellationToken cancellationToken = default);

    // Optei por reservar o null para "o gateway respondeu que o pagamento não existe" (404), que logo após uma
    // notificação é transitório. O adaptador também normaliza Valor para 2 casas decimais antes de devolver,
    // porque o domínio compara com igualdade exata.
    Task<PagamentoGatewayDto?> ObterPagamentoAsync(string gatewayPaymentId, CancellationToken cancellationToken = default);

    Task<string> EstornarAsync(string gatewayPaymentId, string chaveDeIdempotencia, CancellationToken cancellationToken = default);
}
