using FluentValidation;

namespace OficinaMecanica.Billing.Application.Commands.ProcessarWebhookPagamento;

// Optei por não exigir x-signature aqui: assinatura ausente é assinatura inválida (401 na borda), não
// um erro de formato (400), e quem decide isso é o IAssinaturaWebhookValidator dentro do handler.
public class ProcessarWebhookPagamentoCommandValidator : AbstractValidator<ProcessarWebhookPagamentoCommand>
{
    public ProcessarWebhookPagamentoCommandValidator()
    {
        RuleFor(x => x.DataId).NotEmpty().WithMessage("O identificador do pagamento (data.id) é obrigatório.");
        RuleFor(x => x.XRequestId).NotEmpty().WithMessage("O cabeçalho x-request-id é obrigatório.");
    }
}
