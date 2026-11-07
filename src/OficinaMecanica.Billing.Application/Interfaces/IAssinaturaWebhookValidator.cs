namespace OficinaMecanica.Billing.Application.Interfaces;

public interface IAssinaturaWebhookValidator
{
    bool Validar(string? xSignature, string? xRequestId, string dataId);
}
