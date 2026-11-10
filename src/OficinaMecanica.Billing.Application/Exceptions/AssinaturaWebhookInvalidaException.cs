namespace OficinaMecanica.Billing.Application.Exceptions;

public class AssinaturaWebhookInvalidaException : Exception
{
    public AssinaturaWebhookInvalidaException()
        : base("A assinatura do webhook é inválida.") { }
}
