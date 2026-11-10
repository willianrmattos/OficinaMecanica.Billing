using FluentValidation;

namespace OficinaMecanica.Billing.Application.Commands.GerarOrcamento;

// Optei por validar aqui só o que impede responder à saga (id vazio, listas ausentes): as regras do
// orçamento em si ficam no domínio e viram OrcamentoNaoGerado, em vez de uma exceção sem resposta.
public class GerarOrcamentoCommandValidator : AbstractValidator<GerarOrcamentoCommand>
{
    public GerarOrcamentoCommandValidator()
    {
        RuleFor(x => x.OrdemDeServicoId).NotEmpty().WithMessage("A ordem de serviço é obrigatória.");
        RuleFor(x => x.Servicos).NotNull().WithMessage("A lista de serviços é obrigatória.");
        RuleFor(x => x.Pecas).NotNull().WithMessage("A lista de peças é obrigatória.");
    }
}
