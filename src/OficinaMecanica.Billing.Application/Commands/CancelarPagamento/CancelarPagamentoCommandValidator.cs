using FluentValidation;

namespace OficinaMecanica.Billing.Application.Commands.CancelarPagamento;

// Optei por não validar PagamentoId: um id vazio é tratado como ausente no handler, que localiza o pagamento pela
// ordem de serviço, porque uma ValidationException deixaria a saga sem resposta.
public class CancelarPagamentoCommandValidator : AbstractValidator<CancelarPagamentoCommand>
{
    public CancelarPagamentoCommandValidator()
    {
        RuleFor(x => x.OrdemDeServicoId).NotEmpty().WithMessage("A ordem de serviço é obrigatória.");
    }
}
