namespace OficinaMecanica.Contracts.Commands;

// PagamentoId e opcional de proposito: se o Billing atrasar a resposta PagamentoSolicitado, a saga
// ainda nao conhece o id e o Billing localiza o pagamento pelo OrdemDeServicoId.
public sealed record CancelarPagamento(
    Guid MessageId,
    Guid OrdemDeServicoId,
    DateTimeOffset OcorridoEm,
    Guid? PagamentoId,
    string Motivo);
