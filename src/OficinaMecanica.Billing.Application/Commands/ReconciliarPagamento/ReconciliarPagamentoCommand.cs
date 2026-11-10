using MediatR;

namespace OficinaMecanica.Billing.Application.Commands.ReconciliarPagamento;

public record ReconciliarPagamentoCommand(
    Guid PagamentoId,
    int MaximoDeTentativasDeEstorno
) : IRequest;
