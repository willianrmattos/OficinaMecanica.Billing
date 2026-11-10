using FluentValidation;

namespace OficinaMecanica.Billing.Application.Commands.ReconciliarPagamento;

public class ReconciliarPagamentoCommandValidator : AbstractValidator<ReconciliarPagamentoCommand>
{
    public ReconciliarPagamentoCommandValidator()
    {
        RuleFor(x => x.PagamentoId).NotEmpty().WithMessage("O pagamento é obrigatório.");
        RuleFor(x => x.MaximoDeTentativasDeEstorno).GreaterThan(0).WithMessage("O máximo de tentativas de estorno deve ser maior que zero.");
    }
}
