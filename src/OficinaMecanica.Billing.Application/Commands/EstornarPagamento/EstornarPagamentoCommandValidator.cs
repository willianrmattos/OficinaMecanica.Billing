using FluentValidation;

namespace OficinaMecanica.Billing.Application.Commands.EstornarPagamento;

// Optei por não validar PagamentoId: um id vazio ou desconhecido não pode virar ValidationException, que deixaria a
// saga sem resposta; o handler responde EstornoFalhou para pagamento inexistente.
public class EstornarPagamentoCommandValidator : AbstractValidator<EstornarPagamentoCommand>
{
    public EstornarPagamentoCommandValidator()
    {
        RuleFor(x => x.OrdemDeServicoId).NotEmpty().WithMessage("A ordem de serviço é obrigatória.");
    }
}
