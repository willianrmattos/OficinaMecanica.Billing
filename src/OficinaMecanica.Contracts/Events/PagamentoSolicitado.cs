namespace OficinaMecanica.Contracts.Events;

public sealed record PagamentoSolicitado(
    Guid MessageId,
    Guid OrdemDeServicoId,
    DateTimeOffset OcorridoEm,
    Guid PagamentoId,
    string LinkPagamento,
    DateTimeOffset ExpiraEm);
