namespace OficinaMecanica.Billing.Application.Exceptions;

public class PagamentoRecusadoPeloGatewayException : Exception
{
    public PagamentoRecusadoPeloGatewayException(string mensagem, Exception? causa = null)
        : base(mensagem, causa) { }
}
