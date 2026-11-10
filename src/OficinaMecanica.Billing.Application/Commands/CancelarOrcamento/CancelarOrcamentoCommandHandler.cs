using MediatR;
using OficinaMecanica.Billing.Application.Common;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Commands.CancelarOrcamento;

public class CancelarOrcamentoCommandHandler : IRequestHandler<CancelarOrcamentoCommand>
{
    private readonly IOrcamentoRepository _orcamentoRepository;
    private readonly ICancelamentoPrevioRepository _cancelamentoPrevioRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public CancelarOrcamentoCommandHandler(
        IOrcamentoRepository orcamentoRepository,
        ICancelamentoPrevioRepository cancelamentoPrevioRepository,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _orcamentoRepository = orcamentoRepository;
        _cancelamentoPrevioRepository = cancelamentoPrevioRepository;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task Handle(CancelarOrcamentoCommand request, CancellationToken cancellationToken)
    {
        var motivo = NormalizadorDeMotivo.Normalizar(request.Motivo);

        var orcamento = await LocalizarAsync(request, cancellationToken);
        if (orcamento is null)
        {
            await RegistrarCancelamentoPrevioAsync(request.OrdemDeServicoId, motivo, cancellationToken);
            return;
        }

        orcamento.Cancelar(motivo);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    // A saga pode cancelar antes de conhecer o id do orçamento; a ordem de serviço é única no Billing.
    private Task<Orcamento?> LocalizarAsync(CancelarOrcamentoCommand request, CancellationToken cancellationToken) =>
        request.OrcamentoId is { } orcamentoId && orcamentoId != Guid.Empty
            ? _orcamentoRepository.ObterPorIdAsync(orcamentoId, cancellationToken)
            : _orcamentoRepository.ObterPorOrdemDeServicoIdAsync(request.OrdemDeServicoId, cancellationToken);

    private async Task RegistrarCancelamentoPrevioAsync(Guid ordemDeServicoId, string motivo, CancellationToken cancellationToken)
    {
        var existente = await _cancelamentoPrevioRepository.ObterAsync(
            ordemDeServicoId, TipoCancelamentoPrevio.Orcamento, cancellationToken);
        if (existente is not null)
            return;

        var cancelamento = new CancelamentoPrevio(
            ordemDeServicoId, TipoCancelamentoPrevio.Orcamento, motivo, _timeProvider.GetUtcNow());
        await _cancelamentoPrevioRepository.AdicionarAsync(cancelamento, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
