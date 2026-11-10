namespace OficinaMecanica.Billing.Application.DTOs;

public record CriarPreferenciaGatewayRequest(
    Guid PagamentoId,
    string Descricao,
    decimal Valor,
    DateTimeOffset ExpiraEm
);
