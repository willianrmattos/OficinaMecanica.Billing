using FluentValidation;

namespace OficinaMecanica.Billing.Application.Commands.RefazerEstorno;

public class RefazerEstornoCommandValidator : AbstractValidator<RefazerEstornoCommand>
{
    public RefazerEstornoCommandValidator()
    {
        RuleFor(x => x.PagamentoId).NotEmpty().WithMessage("O pagamento é obrigatório.");
        RuleFor(x => x.Motivo).MaximumLength(500).WithMessage("O motivo deve ter no máximo 500 caracteres.");
    }
}
