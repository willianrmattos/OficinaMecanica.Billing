using FluentValidation;

namespace OficinaMecanica.Billing.Application.Commands.SolicitarPagamento;

public class SolicitarPagamentoCommandValidator : AbstractValidator<SolicitarPagamentoCommand>
{
    public SolicitarPagamentoCommandValidator()
    {
        RuleFor(x => x.OrdemDeServicoId).NotEmpty().WithMessage("A ordem de serviço é obrigatória.");
        RuleFor(x => x.OrcamentoId).NotEmpty().WithMessage("O orçamento é obrigatório.");
    }
}
