namespace OficinaMecanica.Contracts.Events;

// PagamentoId e opcional pelo mesmo motivo de CancelarPagamento: o cancelamento pode chegar ao
// Billing antes de existir um Pagamento para a OS, e a resposta ainda precisa sair.
public sealed record PagamentoCancelado(
    Guid MessageId,
    Guid OrdemDeServicoId,
    DateTimeOffset OcorridoEm,
    Guid? PagamentoId);
