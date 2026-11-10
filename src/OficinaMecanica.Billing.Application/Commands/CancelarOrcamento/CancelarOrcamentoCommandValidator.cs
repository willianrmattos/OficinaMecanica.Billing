using FluentValidation;

namespace OficinaMecanica.Billing.Application.Commands.CancelarOrcamento;

// Optei por não validar OrcamentoId: um id vazio é tratado como ausente no handler, que localiza o orçamento pela
// ordem de serviço, porque uma ValidationException deixaria a compensação da saga sem execução.
public class CancelarOrcamentoCommandValidator : AbstractValidator<CancelarOrcamentoCommand>
{
    public CancelarOrcamentoCommandValidator()
    {
        RuleFor(x => x.OrdemDeServicoId).NotEmpty().WithMessage("A ordem de serviço é obrigatória.");
    }
}
