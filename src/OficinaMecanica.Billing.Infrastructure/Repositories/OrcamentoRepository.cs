using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Interfaces;
using OficinaMecanica.Billing.Infrastructure.Data;

namespace OficinaMecanica.Billing.Infrastructure.Repositories;

public class OrcamentoRepository : IOrcamentoRepository
{
    private readonly BillingDbContext _context;

    public OrcamentoRepository(BillingDbContext context)
    {
        _context = context;
    }

    public Task<Orcamento?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Orcamentos.Include(o => o.Itens).FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public Task<Orcamento?> ObterPorOrdemDeServicoIdAsync(Guid ordemDeServicoId, CancellationToken cancellationToken = default) =>
        _context.Orcamentos.Include(o => o.Itens).FirstOrDefaultAsync(o => o.OrdemDeServicoId == ordemDeServicoId, cancellationToken);

    public async Task<IEnumerable<Orcamento>> ListarAsync(
        int pagina, int tamanhoPagina, StatusOrcamento? filtroStatus = null, CancellationToken cancellationToken = default)
    {
        var consulta = _context.Orcamentos.AsNoTracking().Include(o => o.Itens).AsQueryable();
        if (filtroStatus is not null)
            consulta = consulta.Where(o => o.Status == filtroStatus);

        // O Id desempata orçamentos criados no mesmo instante, para a paginação não repetir nem pular linhas.
        return await consulta
            .OrderByDescending(o => o.CriadoEm)
            .ThenBy(o => o.Id)
            .Skip((pagina - 1) * tamanhoPagina)
            .Take(tamanhoPagina)
            .ToListAsync(cancellationToken);
    }

    public Task<int> ContarAsync(StatusOrcamento? filtroStatus = null, CancellationToken cancellationToken = default)
    {
        var consulta = _context.Orcamentos.AsQueryable();
        if (filtroStatus is not null)
            consulta = consulta.Where(o => o.Status == filtroStatus);

        return consulta.CountAsync(cancellationToken);
    }

    public async Task AdicionarAsync(Orcamento orcamento, CancellationToken cancellationToken = default) =>
        await _context.Orcamentos.AddAsync(orcamento, cancellationToken);
}
