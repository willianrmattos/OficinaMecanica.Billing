namespace OficinaMecanica.Billing.Application.DTOs;

public record TentativaPagamentoDto(
    string GatewayPaymentId,
    string StatusGateway,
    string? StatusDetalhe,
    decimal ValorGateway,
    DateTimeOffset AtualizadoEm
);
