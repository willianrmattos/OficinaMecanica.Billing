using MediatR;
using OficinaMecanica.Billing.Application.DTOs;

namespace OficinaMecanica.Billing.Application.Commands.RefazerEstorno;

public record RefazerEstornoCommand(
    Guid PagamentoId,
    string? Motivo
) : IRequest<PagamentoDto>;
