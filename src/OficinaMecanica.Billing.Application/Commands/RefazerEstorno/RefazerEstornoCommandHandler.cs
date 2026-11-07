using MediatR;
using OficinaMecanica.Billing.Application.Commands.EstornarPagamento;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Commands.RefazerEstorno;

// Ação do staff sobre um estorno que falhou: reaproveita o handler do estorno (mesma regra de idempotência e as
// mesmas respostas à saga, que em EstornoPendenteManual só sai desse estado com PagamentoEstornado) em vez de
// duplicar o fluxo, e só acrescenta a checagem de que o pagamento está de fato em EstornoFalhou.
public class RefazerEstornoCommandHandler : IRequestHandler<RefazerEstornoCommand, PagamentoDto>
{
    private const string MotivoPadrao = "Estorno refeito manualmente pelo staff.";

    private readonly IPagamentoRepository _pagamentoRepository;
    private readonly IMediator _mediator;

    public RefazerEstornoCommandHandler(IPagamentoRepository pagamentoRepository, IMediator mediator)
    {
        _pagamentoRepository = pagamentoRepository;
        _mediator = mediator;
    }

    public async Task<PagamentoDto> Handle(RefazerEstornoCommand request, CancellationToken cancellationToken)
    {
        var pagamento = await _pagamentoRepository.ObterPorIdAsync(request.PagamentoId, cancellationToken)
            ?? throw new KeyNotFoundException($"Pagamento {request.PagamentoId} não encontrado.");

        if (pagamento.Status != StatusPagamento.EstornoFalhou)
            throw new DomainException($"Só é possível refazer o estorno de um pagamento com estorno falho. Status atual: '{pagamento.Status}'.");

        var motivo = string.IsNullOrWhiteSpace(request.Motivo) ? MotivoPadrao : request.Motivo;
        await _mediator.Send(new EstornarPagamentoCommand(pagamento.OrdemDeServicoId, pagamento.Id, motivo), cancellationToken);

        return PagamentoMapper.ToDto(pagamento);
    }
}
