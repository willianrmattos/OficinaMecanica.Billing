using MediatR;

namespace OficinaMecanica.Billing.Application.Commands.ProcessarWebhookPagamento;

public record ProcessarWebhookPagamentoCommand(
    string DataId,
    string XRequestId,
    string? XSignature
) : IRequest;
