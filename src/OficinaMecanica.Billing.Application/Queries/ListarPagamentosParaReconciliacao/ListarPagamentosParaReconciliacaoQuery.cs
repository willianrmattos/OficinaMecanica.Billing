using MediatR;

namespace OficinaMecanica.Billing.Application.Queries.ListarPagamentosParaReconciliacao;

public record ListarPagamentosParaReconciliacaoQuery(int Limite) : IRequest<IReadOnlyList<Guid>>;
