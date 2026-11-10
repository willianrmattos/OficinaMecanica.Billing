namespace OficinaMecanica.Billing.Application.DTOs;

public record PagamentoDto(
    Guid Id,
    Guid OrcamentoId,
    Guid OrdemDeServicoId,
    string Status,
    decimal Valor,
    string? LinkPagamento,
    DateTimeOffset ExpiraEm,
    DateTimeOffset CriadoEm,
    string? GatewayPaymentIdAprovado,
    DateTimeOffset? AprovadoEm,
    bool AprovacaoTardia,
    string? MotivoCancelamento,
    IEnumerable<TentativaPagamentoDto> Tentativas,
    EstornoDto? Estorno
);
