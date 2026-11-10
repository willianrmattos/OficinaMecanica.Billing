using MediatR;

namespace OficinaMecanica.Billing.Application.Commands.CancelarOrcamento;

public record CancelarOrcamentoCommand(
    Guid OrdemDeServicoId,
    Guid? OrcamentoId,
    string Motivo
) : IRequest;
