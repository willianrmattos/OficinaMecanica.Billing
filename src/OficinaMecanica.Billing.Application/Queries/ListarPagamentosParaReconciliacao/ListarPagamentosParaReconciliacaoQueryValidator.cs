using FluentValidation;

namespace OficinaMecanica.Billing.Application.Queries.ListarPagamentosParaReconciliacao;

public class ListarPagamentosParaReconciliacaoQueryValidator : AbstractValidator<ListarPagamentosParaReconciliacaoQuery>
{
    public ListarPagamentosParaReconciliacaoQueryValidator()
    {
        RuleFor(x => x.Limite).InclusiveBetween(1, 1000).WithMessage("O limite deve estar entre 1 e 1000.");
    }
}
