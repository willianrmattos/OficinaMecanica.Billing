using FluentValidation;

namespace OficinaMecanica.Billing.Application.Queries.ListarOrcamentos;

public class ListarOrcamentosQueryValidator : AbstractValidator<ListarOrcamentosQuery>
{
    public ListarOrcamentosQueryValidator()
    {
        RuleFor(x => x.Pagina).GreaterThanOrEqualTo(1).WithMessage("A página deve ser maior ou igual a 1.");
        RuleFor(x => x.TamanhoPagina).InclusiveBetween(1, 100).WithMessage("O tamanho da página deve estar entre 1 e 100.");
    }
}
