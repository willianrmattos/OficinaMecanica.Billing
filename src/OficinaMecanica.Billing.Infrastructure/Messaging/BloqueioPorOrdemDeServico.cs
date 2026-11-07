using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Infrastructure.Data;

namespace OficinaMecanica.Billing.Infrastructure.Messaging;

// Serializa o processamento das mensagens da mesma ordem de serviço. Sem isto, GerarOrcamento e CancelarOrcamento
// da mesma ordem, processados ao mesmo tempo, não se enxergam: o cancelamento não encontra o orçamento (ainda não
// confirmado) e grava o tombstone, enquanto o GerarOrcamento não encontra o tombstone e cria o orçamento. Os dois
// confirmam e o cancelamento se perde. Os índices únicos não ajudam, porque as duas linhas estão em tabelas diferentes.
public interface IBloqueioPorOrdemDeServico
{
    // Retorna quando a ordem de serviço é exclusiva desta transação; o bloqueio cai junto com o commit ou o rollback.
    Task AdquirirAsync(Guid ordemDeServicoId, CancellationToken cancellationToken);
}

// sp_getapplock com dono "Transaction": o SQL Server solta o bloqueio no fim da transação do consumer (que o
// MassTransit abre para o outbox), então vale entre réplicas e não deixa bloqueio órfão se o processo cair.
public sealed class BloqueioPorOrdemDeServicoSqlServer : IBloqueioPorOrdemDeServico
{
    // O CommandTimeout padrão do SqlClient é 30 s. Se o bloqueio esperasse o mesmo tempo, o erro final viria de uma corrida
    // entre o timeout do cliente (SqlException de timeout, conexão em estado incerto) e o do servidor. Com 20 s o
    // sp_getapplock devolve -1 antes e o THROW abaixo produz sempre o mesmo erro, que o retry da mensagem repete.
    public const int TempoLimitePadraoEmMilissegundos = 20_000;

    private readonly BillingDbContext _db;
    private readonly int _tempoLimiteEmMilissegundos;

    public BloqueioPorOrdemDeServicoSqlServer(BillingDbContext db) : this(db, TempoLimitePadraoEmMilissegundos)
    {
    }

    // Construtor interno: o container de DI só enxerga o público, e os testes reduzem o tempo para provar o erro.
    internal BloqueioPorOrdemDeServicoSqlServer(BillingDbContext db, int tempoLimiteEmMilissegundos)
    {
        _db = db;
        _tempoLimiteEmMilissegundos = tempoLimiteEmMilissegundos;
    }

    // O valor que o sp_getapplock realmente recebe; os testes o leem da instância criada pelo construtor público (e pelo DI).
    internal int TempoLimiteEfetivoEmMilissegundos => _tempoLimiteEmMilissegundos;

    public async Task AdquirirAsync(Guid ordemDeServicoId, CancellationToken cancellationToken)
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("O bloqueio por ordem de serviço exige uma transação aberta.");

        var recurso = new SqlParameter("@recurso", $"billing:ordem-de-servico:{ordemDeServicoId:N}");
        var tempoLimite = new SqlParameter("@tempoLimite", _tempoLimiteEmMilissegundos);

        // Resultado negativo (timeout, deadlock, cancelamento) vira exceção: a mensagem volta para o retry em vez de
        // seguir sem a exclusividade que o handler assume.
        await _db.Database.ExecuteSqlRawAsync(
            "DECLARE @resultado int; " +
            "EXEC @resultado = sp_getapplock @Resource = @recurso, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @tempoLimite; " +
            "IF @resultado < 0 THROW 50001, 'Não foi possível bloquear a ordem de serviço.', 1;",
            new object[] { recurso, tempoLimite },
            cancellationToken);
    }
}

// Quando o outbox do EF está desligado (testes sobre EF InMemory) não há transação relacional para segurar o bloqueio.
public sealed class SemBloqueioPorOrdemDeServico : IBloqueioPorOrdemDeServico
{
    public Task AdquirirAsync(Guid ordemDeServicoId, CancellationToken cancellationToken) => Task.CompletedTask;
}
