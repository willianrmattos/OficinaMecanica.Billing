using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Interfaces;
using OficinaMecanica.Billing.Infrastructure.Data;

namespace OficinaMecanica.Billing.Infrastructure.Repositories;

public class CancelamentoPrevioRepository : ICancelamentoPrevioRepository
{
    private readonly BillingDbContext _context;

    public CancelamentoPrevioRepository(BillingDbContext context)
    {
        _context = context;
    }

    public Task<CancelamentoPrevio?> ObterAsync(Guid ordemDeServicoId, TipoCancelamentoPrevio tipo, CancellationToken cancellationToken = default) =>
        _context.CancelamentosPrevios.FirstOrDefaultAsync(c => c.OrdemDeServicoId == ordemDeServicoId && c.Tipo == tipo, cancellationToken);

    public async Task AdicionarAsync(CancelamentoPrevio cancelamento, CancellationToken cancellationToken = default) =>
        await _context.CancelamentosPrevios.AddAsync(cancelamento, cancellationToken);
}
