using MediatR;
using OficinaMecanica.Billing.Application.Common;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Application.Mensagens;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Commands.CancelarPagamento;

public class CancelarPagamentoCommandHandler : IRequestHandler<CancelarPagamentoCommand>
{
    private readonly IPagamentoRepository _pagamentoRepository;
    private readonly IOrcamentoRepository _orcamentoRepository;
    private readonly ICancelamentoPrevioRepository _cancelamentoPrevioRepository;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public CancelarPagamentoCommandHandler(
        IPagamentoRepository pagamentoRepository,
        IOrcamentoRepository orcamentoRepository,
        ICancelamentoPrevioRepository cancelamentoPrevioRepository,
        IIntegrationEventPublisher publisher,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _pagamentoRepository = pagamentoRepository;
        _orcamentoRepository = orcamentoRepository;
        _cancelamentoPrevioRepository = cancelamentoPrevioRepository;
        _publisher = publisher;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task Handle(CancelarPagamentoCommand request, CancellationToken cancellationToken)
    {
        var agora = _timeProvider.GetUtcNow();
        var motivo = NormalizadorDeMotivo.Normalizar(request.Motivo);

        var pagamento = await LocalizarAsync(request, cancellationToken);
        if (pagamento is null)
        {
            await ResponderSemPagamentoAsync(request.OrdemDeServicoId, motivo, agora, cancellationToken);
            return;
        }

        // O cancelamento da saga por prazo chega depois de ExpiraEm: nesse caso o pagamento expirou, e
        // registrar Expirado (e não Cancelado) mantém a causa real no histórico.
        if (pagamento.Status == StatusPagamento.Pendente && agora >= pagamento.ExpiraEm)
            pagamento.Expirar(agora);

        // O Billing é a autoridade sobre o pagamento: se o gateway já aprovou, a resposta à saga é
        // PagamentoAprovado e o orçamento fica como está, nunca um cancelamento que o dinheiro já desmentiu.
        var resultado = pagamento.Cancelar(motivo);
        if (resultado == ResultadoCancelamentoPagamento.JaAprovado)
        {
            await _publisher.PublishAsync(MensagensDeIntegracao.PagamentoAprovadoDe(pagamento, agora), cancellationToken);
        }
        else
        {
            await CancelarOrcamentoAprovadoAsync(pagamento, motivo, cancellationToken);
            await _publisher.PublishAsync(MensagensDeIntegracao.PagamentoCanceladoDe(pagamento, agora), cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    // A saga pode cancelar antes de conhecer o id do pagamento; a ordem de serviço é única no Billing.
    private Task<Pagamento?> LocalizarAsync(CancelarPagamentoCommand request, CancellationToken cancellationToken) =>
        request.PagamentoId is { } pagamentoId && pagamentoId != Guid.Empty
            ? _pagamentoRepository.ObterPorIdAsync(pagamentoId, cancellationToken)
            : _pagamentoRepository.ObterPorOrdemDeServicoIdAsync(request.OrdemDeServicoId, cancellationToken);

    private async Task CancelarOrcamentoAprovadoAsync(Pagamento pagamento, string motivo, CancellationToken cancellationToken)
    {
        var orcamento = await _orcamentoRepository.ObterPorIdAsync(pagamento.OrcamentoId, cancellationToken);
        if (orcamento is { Status: StatusOrcamento.Aprovado })
            orcamento.Cancelar(motivo);
    }

    private async Task ResponderSemPagamentoAsync(Guid ordemDeServicoId, string motivo, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var existente = await _cancelamentoPrevioRepository.ObterAsync(
            ordemDeServicoId, TipoCancelamentoPrevio.Pagamento, cancellationToken);
        if (existente is null)
        {
            var cancelamento = new CancelamentoPrevio(ordemDeServicoId, TipoCancelamentoPrevio.Pagamento, motivo, agora);
            await _cancelamentoPrevioRepository.AdicionarAsync(cancelamento, cancellationToken);
        }

        await _publisher.PublishAsync(MensagensDeIntegracao.PagamentoCanceladoSemPagamentoDe(ordemDeServicoId, agora), cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
