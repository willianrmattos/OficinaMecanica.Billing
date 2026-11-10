namespace OficinaMecanica.Contracts.Events;

public sealed record PagamentoNaoSolicitado(
    Guid MessageId,
    Guid OrdemDeServicoId,
    DateTimeOffset OcorridoEm,
    string Motivo);
