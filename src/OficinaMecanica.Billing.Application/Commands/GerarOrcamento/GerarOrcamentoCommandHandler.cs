using MediatR;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Application.Mensagens;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Commands.GerarOrcamento;

public class GerarOrcamentoCommandHandler : IRequestHandler<GerarOrcamentoCommand>
{
    private const string MotivoCanceladoAntesDaGeracao = "Cancelado antes da geração do orçamento.";

    private readonly IOrcamentoRepository _orcamentoRepository;
    private readonly ICancelamentoPrevioRepository _cancelamentoPrevioRepository;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly IMetricasDoBilling _metricas;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public GerarOrcamentoCommandHandler(
        IOrcamentoRepository orcamentoRepository,
        ICancelamentoPrevioRepository cancelamentoPrevioRepository,
        IIntegrationEventPublisher publisher,
        IMetricasDoBilling metricas,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _orcamentoRepository = orcamentoRepository;
        _cancelamentoPrevioRepository = cancelamentoPrevioRepository;
        _publisher = publisher;
        _metricas = metricas;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task Handle(GerarOrcamentoCommand request, CancellationToken cancellationToken)
    {
        var agora = _timeProvider.GetUtcNow();

        var existente = await _orcamentoRepository.ObterPorOrdemDeServicoIdAsync(request.OrdemDeServicoId, cancellationToken);
        if (existente is not null)
        {
            await _publisher.PublishAsync(MensagensDeIntegracao.OrcamentoGeradoDe(existente, agora), cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        var cancelamentoPrevio = await _cancelamentoPrevioRepository.ObterAsync(
            request.OrdemDeServicoId, TipoCancelamentoPrevio.Orcamento, cancellationToken);
        if (cancelamentoPrevio is not null)
        {
            await PublicarNaoGeradoAsync(request.OrdemDeServicoId, MotivoCanceladoAntesDaGeracao, agora, cancellationToken);
            return;
        }

        Orcamento orcamento;
        try
        {
            orcamento = CriarOrcamento(request, agora);
        }
        catch (DomainException ex)
        {
            await PublicarNaoGeradoAsync(request.OrdemDeServicoId, ex.Message, agora, cancellationToken);
            return;
        }

        await _orcamentoRepository.AdicionarAsync(orcamento, cancellationToken);
        await _publisher.PublishAsync(MensagensDeIntegracao.OrcamentoGeradoDe(orcamento, agora), cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _metricas.RegistrarOrcamentoGerado();
    }

    private async Task PublicarNaoGeradoAsync(Guid ordemDeServicoId, string motivo, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await _publisher.PublishAsync(MensagensDeIntegracao.OrcamentoNaoGeradoDe(ordemDeServicoId, motivo, agora), cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static Orcamento CriarOrcamento(GerarOrcamentoCommand request, DateTimeOffset agora)
    {
        var servicos = request.Servicos.Select(s => new ItemOrcamento(TipoItemOrcamento.Servico, s.Nome, s.PrecoUnitario, s.Quantidade));
        var pecas = request.Pecas.Select(p => new ItemOrcamento(TipoItemOrcamento.Peca, p.Nome, p.PrecoUnitario, p.Quantidade));

        return new Orcamento(
            request.OrdemDeServicoId, request.Numero, request.ValorTotal, request.ValidoAte, request.Laudo,
            servicos.Concat(pecas), agora);
    }
}
