using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Infrastructure.Data;
using OficinaMecanica.Billing.Infrastructure.Messaging;

namespace OficinaMecanica.Billing.Integration.Tests.Infra;

[Trait("Category", "Infra")]
[Collection(ColecaoInfra.Nome)]
public sealed class BloqueioPorOrdemDeServicoTests : IAsyncLifetime
{
    private readonly InfraFixture _infra;
    private readonly string _banco = $"BillingLock_{Guid.NewGuid():N}"[..24];

    public BloqueioPorOrdemDeServicoTests(InfraFixture infra)
    {
        _infra = infra;
    }

    public async Task InitializeAsync()
    {
        await using var db = NovoContexto();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await _infra.ApagarBancoAsync(_banco);
    }

    private BillingDbContext NovoContexto() =>
        new(new DbContextOptionsBuilder<BillingDbContext>().UseSqlServer(_infra.ConexaoSql(_banco)).Options);

    [Fact]
    public async Task SemTransacaoAberta_DeveRecusarEmVezDeSeguirSemExclusividade()
    {
        await using var db = NovoContexto();

        var act = () => new BloqueioPorOrdemDeServicoSqlServer(db).AdquirirAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*transação*");
    }

    [Fact]
    public async Task MesmaOrdem_ASegundaTransacaoEsperaAPrimeiraConfirmar()
    {
        var ordem = Guid.NewGuid();
        await using var primeiro = NovoContexto();
        await using var segundo = NovoContexto();
        await using var transacao1 = await primeiro.Database.BeginTransactionAsync();
        await using var transacao2 = await segundo.Database.BeginTransactionAsync();
        await new BloqueioPorOrdemDeServicoSqlServer(primeiro).AdquirirAsync(ordem, CancellationToken.None);

        var espera = Task.Run(() => new BloqueioPorOrdemDeServicoSqlServer(segundo).AdquirirAsync(ordem, CancellationToken.None));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var bloqueadaEnquantoAPrimeiraSegura = !espera.IsCompleted;
        await transacao1.CommitAsync();
        await espera.WaitAsync(TimeSpan.FromSeconds(10));

        bloqueadaEnquantoAPrimeiraSegura.Should().BeTrue();
        espera.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Rollback_DeveLiberarAOrdemParaOutraTransacao()
    {
        var ordem = Guid.NewGuid();
        await using var primeiro = NovoContexto();
        await using var segundo = NovoContexto();
        await using var transacao1 = await primeiro.Database.BeginTransactionAsync();
        await using var transacao2 = await segundo.Database.BeginTransactionAsync();
        await new BloqueioPorOrdemDeServicoSqlServer(primeiro).AdquirirAsync(ordem, CancellationToken.None);
        var espera = Task.Run(() => new BloqueioPorOrdemDeServicoSqlServer(segundo).AdquirirAsync(ordem, CancellationToken.None));

        await transacao1.RollbackAsync();

        await espera.WaitAsync(TimeSpan.FromSeconds(10));
        espera.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task OrdensDiferentes_NaoDevemSeBloquear()
    {
        await using var primeiro = NovoContexto();
        await using var segundo = NovoContexto();
        await using var transacao1 = await primeiro.Database.BeginTransactionAsync();
        await using var transacao2 = await segundo.Database.BeginTransactionAsync();
        await new BloqueioPorOrdemDeServicoSqlServer(primeiro).AdquirirAsync(Guid.NewGuid(), CancellationToken.None);

        var act = () => new BloqueioPorOrdemDeServicoSqlServer(segundo).AdquirirAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().CompleteWithinAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EsperaMaiorQueOTempoLimite_DeveFalharComOErroDoServidorEmVezDeEsperarSemFim()
    {
        var ordem = Guid.NewGuid();
        await using var primeiro = NovoContexto();
        await using var segundo = NovoContexto();
        await using var transacao1 = await primeiro.Database.BeginTransactionAsync();
        await using var transacao2 = await segundo.Database.BeginTransactionAsync();
        await new BloqueioPorOrdemDeServicoSqlServer(primeiro).AdquirirAsync(ordem, CancellationToken.None);

        var act = () => new BloqueioPorOrdemDeServicoSqlServer(segundo, tempoLimiteEmMilissegundos: 300)
            .AdquirirAsync(ordem, CancellationToken.None);

        var excecao = (await act.Should().ThrowAsync<SqlException>()).Which;
        excecao.Number.Should().Be(50001);
        excecao.Message.Should().Contain("bloquear a ordem de serviço");
    }

    [Fact]
    public void TempoLimitePadrao_DeveSerMenorQueOTimeoutPadraoDe30SegundosDoComandoSql()
    {
        BloqueioPorOrdemDeServicoSqlServer.TempoLimitePadraoEmMilissegundos.Should().BeLessThan(30_000);
    }

    [Fact]
    public async Task MesmaTransacaoPedindoDuasVezes_NaoDeveSeAutoBloquear()
    {
        var ordem = Guid.NewGuid();
        await using var db = NovoContexto();
        await using var transacao = await db.Database.BeginTransactionAsync();
        var bloqueio = new BloqueioPorOrdemDeServicoSqlServer(db);
        await bloqueio.AdquirirAsync(ordem, CancellationToken.None);

        var act = () => bloqueio.AdquirirAsync(ordem, CancellationToken.None);

        await act.Should().CompleteWithinAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SemBloqueio_NaoDeveFazerNada()
    {
        var act = () => new SemBloqueioPorOrdemDeServico().AdquirirAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().CompleteWithinAsync(TimeSpan.FromSeconds(1));
    }
}
