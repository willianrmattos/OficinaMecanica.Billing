using MediatR;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Application.Mensagens;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Commands.SolicitarPagamento;

public class SolicitarPagamentoCommandHandler : IRequestHandler<SolicitarPagamentoCommand>
{
    private const string MotivoOrcamentoNaoEncontrado = "Orçamento não encontrado para a ordem de serviço.";
    private const string MotivoCanceladoAntesDaSolicitacao = "Cancelado antes da solicitação do pagamento.";
    private const string MotivoOrcamentoExpirado = "O orçamento expirou e não pode ser pago.";
    private const string MotivoRecusaDoGateway = "O gateway recusou a criação do pagamento.";

    private readonly IOrcamentoRepository _orcamentoRepository;
    private readonly IPagamentoRepository _pagamentoRepository;
    private readonly ICancelamentoPrevioRepository _cancelamentoPrevioRepository;
    private readonly IPagamentoGateway _gateway;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly IMetricasDoBilling _metricas;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SolicitarPagamentoCommandHandler> _logger;

    public SolicitarPagamentoCommandHandler(
        IOrcamentoRepository orcamentoRepository,
        IPagamentoRepository pagamentoRepository,
        ICancelamentoPrevioRepository cancelamentoPrevioRepository,
        IPagamentoGateway gateway,
        IIntegrationEventPublisher publisher,
        IMetricasDoBilling metricas,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<SolicitarPagamentoCommandHandler> logger)
    {
        _orcamentoRepository = orcamentoRepository;
        _pagamentoRepository = pagamentoRepository;
        _cancelamentoPrevioRepository = cancelamentoPrevioRepository;
        _gateway = gateway;
        _publisher = publisher;
        _metricas = metricas;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task Handle(SolicitarPagamentoCommand request, CancellationToken cancellationToken)
    {
        var agora = _timeProvider.GetUtcNow();

        var orcamento = await _orcamentoRepository.ObterPorIdAsync(request.OrcamentoId, cancellationToken);
        if (orcamento is null || orcamento.OrdemDeServicoId != request.OrdemDeServicoId)
        {
            await PublicarNaoSolicitadoAsync(request.OrdemDeServicoId, MotivoOrcamentoNaoEncontrado, agora, cancellationToken);
            return;
        }

        var pagamento = await _pagamentoRepository.ObterPorOrdemDeServicoIdAsync(request.OrdemDeServicoId, cancellationToken);

        if (pagamento is not null && !AguardaPreferencia(pagamento))
        {
            await ResponderPagamentoExistenteAsync(pagamento, agora, cancellationToken);
            return;
        }

        if (pagamento is null)
        {
            var cancelamentoPrevio = await _cancelamentoPrevioRepository.ObterAsync(
                request.OrdemDeServicoId, TipoCancelamentoPrevio.Pagamento, cancellationToken);
            if (cancelamentoPrevio is not null)
            {
                await PublicarNaoSolicitadoAsync(request.OrdemDeServicoId, MotivoCanceladoAntesDaSolicitacao, agora, cancellationToken);
                return;
            }

            pagamento = await PrepararNovoPagamentoAsync(orcamento, request.ExpiraEm, agora, cancellationToken);
            if (pagamento is null)
                return;
        }

        await CriarPreferenciaNoGatewayAsync(orcamento, pagamento, agora, cancellationToken);
    }

    // Pagamento pendente sem preferência é o que sobra quando o pré-persist foi commitado e a chamada ao gateway
    // não terminou: retoma dali em vez de criar um segundo pagamento.
    private static bool AguardaPreferencia(Pagamento pagamento) =>
        pagamento.Status == StatusPagamento.Pendente && pagamento.PreferenceId is null;

    private async Task ResponderPagamentoExistenteAsync(Pagamento pagamento, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        switch (pagamento.Status)
        {
            case StatusPagamento.Pendente:
                await _publisher.PublishAsync(MensagensDeIntegracao.PagamentoSolicitadoDe(pagamento, agora), cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                break;

            case StatusPagamento.Aprovado:
                await _publisher.PublishAsync(MensagensDeIntegracao.PagamentoAprovadoDe(pagamento, agora), cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                break;

            default:
                var motivo = $"Já existe um pagamento para a ordem de serviço com status '{pagamento.Status}'.";
                await PublicarNaoSolicitadoAsync(pagamento.OrdemDeServicoId, motivo, agora, cancellationToken);
                break;
        }
    }

    private async Task<Pagamento?> PrepararNovoPagamentoAsync(
        Orcamento orcamento,
        DateTimeOffset expiraEm,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        if (orcamento.Status == StatusOrcamento.Gerado && orcamento.EstaExpirado(agora))
        {
            orcamento.Expirar(agora);
            await PublicarNaoSolicitadoAsync(orcamento.OrdemDeServicoId, MotivoOrcamentoExpirado, agora, cancellationToken);
            return null;
        }

        Pagamento pagamento;
        try
        {
            // O pagamento nasce antes da aprovação do orçamento para que um prazo inválido não
            // deixe o orçamento aprovado em memória quando a falha for persistida.
            pagamento = new Pagamento(orcamento.Id, orcamento.OrdemDeServicoId, orcamento.ValorTotal, expiraEm, agora);
            orcamento.Aprovar(agora);
        }
        catch (DomainException ex)
        {
            await PublicarNaoSolicitadoAsync(orcamento.OrdemDeServicoId, ex.Message, agora, cancellationToken);
            return null;
        }

        // Pré-persisto o pagamento como Pendente antes de chamar o gateway. Fora de um consumer esse registro
        // sobrevive a uma queda e o reprocessamento o retoma; dentro da transação do MassTransit este Save não é
        // commit, e a segurança vem de a preferência órfã (sem link gravado) não cobrar ninguém.
        await _pagamentoRepository.AdicionarAsync(pagamento, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return pagamento;
    }

    private async Task CriarPreferenciaNoGatewayAsync(
        Orcamento orcamento,
        Pagamento pagamento,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        PreferenciaGatewayDto preferencia;
        try
        {
            preferencia = await _gateway.CriarPreferenciaAsync(
                new CriarPreferenciaGatewayRequest(pagamento.Id, $"Ordem de serviço {orcamento.Numero}", pagamento.Valor, pagamento.ExpiraEm),
                cancellationToken);
        }
        catch (PagamentoRecusadoPeloGatewayException ex)
        {
            // Optei por tratar como fato de negócio só a recusa permanente do gateway; timeout e 5xx propagam
            // para o retry do consumidor e o pagamento pré-persistido sem preferência é retomado na reentrega.
            // Um 4xx definitivo pode ser defeito do nosso próprio request e aqui ele cancela a ordem do cliente,
            // então registro como erro e conto numa métrica própria para alguém ser avisado. O aviso sai depois do
            // SaveChanges: se o save falhar, a reentrega refaz a chamada ao gateway e avisa uma vez só.
            pagamento.Cancelar(MotivoRecusaDoGateway);
            await PublicarNaoSolicitadoAsync(pagamento.OrdemDeServicoId, MotivoRecusaDoGateway, agora, cancellationToken);
            _logger.LogError(ex, "O gateway recusou criar a preferência do pagamento {PagamentoId}; a solicitação foi cancelada.", pagamento.Id);
            _metricas.RegistrarPagamentoRecusadoPeloGateway();
            return;
        }

        pagamento.DefinirPreferencia(preferencia.PreferenceId, preferencia.LinkPagamento);

        await _publisher.PublishAsync(MensagensDeIntegracao.PagamentoSolicitadoDe(pagamento, agora), cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task PublicarNaoSolicitadoAsync(Guid ordemDeServicoId, string motivo, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await _publisher.PublishAsync(MensagensDeIntegracao.PagamentoNaoSolicitadoDe(ordemDeServicoId, motivo, agora), cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
