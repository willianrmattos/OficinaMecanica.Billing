namespace OficinaMecanica.Billing.Application.Exceptions;

public class PagamentoDoGatewayIndisponivelException : Exception
{
    public PagamentoDoGatewayIndisponivelException(string gatewayPaymentId)
        : base($"O pagamento {gatewayPaymentId} ainda não está disponível no gateway.") { }
}
