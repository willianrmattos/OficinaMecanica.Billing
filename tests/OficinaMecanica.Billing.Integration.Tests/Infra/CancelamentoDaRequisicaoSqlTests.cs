using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.API.Middleware;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Infrastructure.Data;
using static OficinaMecanica.Billing.Integration.Tests.Support.MiddlewareDeTeste;

namespace OficinaMecanica.Billing.Integration.Tests.Infra;

// O critério de CancelamentoDaRequisicao é validado contra as exceções que o SqlClient e o EF realmente lançam num SQL
// Server real, não contra exceções montadas à mão: o cliente "sai" com um comando em voo e o middleware precisa responder
// 499 sem corpo e sem Error; os erros reais de SQL que acontecem com a requisição abortada precisam continuar como erro.
[Trait("Category", "Infra")]
[Collection(ColecaoInfra.Nome)]
public sealed class CancelamentoDaRequisicaoSqlTests : IAsyncLifetime
{
    private readonly InfraFixture _infra;
    private readonly string _banco = $"BillingCancel_{Guid.NewGuid():N}"[..24];

    public CancelamentoDaRequisicaoSqlTests(InfraFixture infra)
    {
        _infra = infra;
    }

    public async Task InitializeAsync()
    {
        await using var db = NovoDb();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await _infra.ApagarBancoAsync(_banco);
    }

    // Mesmo retry do caminho HTTP (DependencyInjection do Billing): ele não pode mudar o tipo da exceção de cancelamento.
    private BillingDbContext NovoDb(int? timeoutDeComando = null) =>
        new(new DbContextOptionsBuilder<BillingDbContext>().UseSqlServer(_infra.ConexaoSql(_banco), sql =>
        {
            sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
            if (timeoutDeComando is not null)
                sql.CommandTimeout(timeoutDeComando);
        }).Options);

    private static CancelamentoPrevio NovoCancelamento(Guid ordemDeServicoId) =>
        new(ordemDeServicoId, TipoCancelamentoPrevio.Orcamento, "motivo", DateTimeOffset.UtcNow);

    private sealed record Resultado(DefaultHttpContext Contexto, Mock<ILogger<ExceptionHandlingMiddleware>> Logger, Exception? Capturada);

    // clienteSaiNoMeio: o RequestAborted é cancelado enquanto o comando está em voo (o token vai para o EF/SqlClient, como no
    // caminho HTTP). Falso: o token já está cancelado quando o erro real de SQL acontece, e a operação roda sem token.
    private static async Task<Resultado> ExecutarNoMiddlewareAsync(Func<CancellationToken, Task> operacao, bool clienteSaiNoMeio)
    {
        using var abortada = new CancellationTokenSource();
        var logger = new Mock<ILogger<ExceptionHandlingMiddleware>>();
        var contexto = NovoContexto(abortada.Token);
        Exception? capturada = null;

        if (clienteSaiNoMeio)
            abortada.CancelAfter(TimeSpan.FromMilliseconds(1500));
        else
            abortada.Cancel();

        var middleware = new ExceptionHandlingMiddleware(
            async _ =>
            {
                try
                {
                    await operacao(clienteSaiNoMeio ? contexto.RequestAborted : CancellationToken.None);
                }
                catch (Exception ex)
                {
                    capturada = ex;
                    throw;
                }
            },
            logger.Object);

        await middleware.InvokeAsync(contexto);

        return new Resultado(contexto, logger, capturada);
    }

    private static void DeveTerSidoAbandono(Resultado r)
    {
        r.Contexto.Response.StatusCode.Should().Be(StatusCodes.Status499ClientClosedRequest);
        CorpoEscrito(r.Contexto).Should().BeEmpty();
        Registros(r.Logger).Should().NotContain(l => l.Nivel >= LogLevel.Warning);
        CancelamentoDaRequisicao.Eh(r.Capturada!).Should().BeTrue();
    }

    private static void DeveTerSidoErro(Resultado r, int status, LogLevel nivelDoLog)
    {
        r.Contexto.Response.StatusCode.Should().Be(status);
        Registros(r.Logger).Should().ContainSingle().Which.Nivel.Should().Be(nivelDoLog);
        CancelamentoDaRequisicao.Eh(r.Capturada!).Should().BeFalse();
    }

    // ---- O cliente sai com o comando em voo ----

    [Fact]
    public async Task ComandoSqlEmVooCancelado_DeveSerSqlExceptionEOMiddlewareDeveResponder499SemCorpoNemErro()
    {
        await using var db = NovoDb();

        var r = await ExecutarNoMiddlewareAsync(
            token => db.Database.ExecuteSqlRawAsync("WAITFOR DELAY '00:00:30'", token), clienteSaiNoMeio: true);

        r.Capturada.Should().BeOfType<SqlException>();
        DeveTerSidoAbandono(r);
    }

    [Fact]
    public async Task ConsultaSqlEmVooCancelada_DeveSerSqlExceptionEOMiddlewareDeveResponder499SemCorpoNemErro()
    {
        await using var db = NovoDb();

        var r = await ExecutarNoMiddlewareAsync(
            async token => await db.Database.SqlQueryRaw<int>("WAITFOR DELAY '00:00:30'; SELECT 1 AS Value").ToListAsync(token),
            clienteSaiNoMeio: true);

        r.Capturada.Should().BeOfType<SqlException>();
        DeveTerSidoAbandono(r);
    }

    [Fact]
    public async Task SaveChangesBloqueadoEmVooCancelado_DeveSerDbUpdateExceptionComSqlExceptionEOMiddlewareDeveResponder499()
    {
        // Outra conexão segura, sem confirmar, a mesma chave única: o SaveChanges espera o lock até o cliente sair.
        var ordemDeServicoId = Guid.NewGuid();
        await using var segura = new SqlConnection(_infra.ConexaoSql(_banco));
        await segura.OpenAsync();
        await using var transacao = segura.BeginTransaction();
        await using (var comando = segura.CreateCommand())
        {
            comando.Transaction = transacao;
            comando.CommandText =
                $"INSERT INTO CancelamentosPrevios (Id, OrdemDeServicoId, Tipo, Motivo, RegistradoEm) VALUES (NEWID(), '{ordemDeServicoId}', 1, 'x', SYSDATETIMEOFFSET())";
            await comando.ExecuteNonQueryAsync();
        }

        await using var db = NovoDb();
        db.CancelamentosPrevios.Add(NovoCancelamento(ordemDeServicoId));

        var r = await ExecutarNoMiddlewareAsync(token => db.SaveChangesAsync(token), clienteSaiNoMeio: true);

        r.Capturada.Should().BeOfType<DbUpdateException>().Which.InnerException.Should().BeOfType<SqlException>();
        DeveTerSidoAbandono(r);
        await transacao.RollbackAsync();
    }

    [Fact]
    public async Task TokenJaCanceladoAntesDoComando_DeveSerOceEOMiddlewareDeveResponder499()
    {
        await using var db = NovoDb();
        using var cancelado = new CancellationTokenSource();
        cancelado.Cancel();

        var r = await ExecutarNoMiddlewareAsync(
            _ => db.Database.ExecuteSqlRawAsync("SELECT 1", cancelado.Token), clienteSaiNoMeio: false);

        r.Capturada.Should().BeAssignableTo<OperationCanceledException>();
        DeveTerSidoAbandono(r);
    }

    // ---- Erros reais de SQL com a requisição já abortada: continuam sendo erro ----

    [Fact]
    public async Task TimeoutDeComandoReal_ComRequisicaoAbortada_DeveContinuarRespondendo500ComErroLogado()
    {
        await using var db = NovoDb(timeoutDeComando: 1);

        var r = await ExecutarNoMiddlewareAsync(
            token => db.Database.ExecuteSqlRawAsync("WAITFOR DELAY '00:00:10'", token), clienteSaiNoMeio: false);

        r.Capturada.Should().BeOfType<SqlException>().Which.Number.Should().Be(-2);
        DeveTerSidoErro(r, 500, LogLevel.Error);
        JsonDocument.Parse(CorpoEscrito(r.Contexto)).RootElement.GetProperty("tipo").GetString().Should().Be("Erro Interno");
    }

    [Fact]
    public async Task IndiceUnicoDuplicadoReal_ComRequisicaoAbortada_DeveContinuarRespondendo409()
    {
        var ordemDeServicoId = Guid.NewGuid();
        await using (var primeiro = NovoDb())
        {
            primeiro.CancelamentosPrevios.Add(NovoCancelamento(ordemDeServicoId));
            await primeiro.SaveChangesAsync();
        }

        await using var db = NovoDb();
        db.CancelamentosPrevios.Add(NovoCancelamento(ordemDeServicoId));

        var r = await ExecutarNoMiddlewareAsync(token => db.SaveChangesAsync(token), clienteSaiNoMeio: false);

        r.Capturada.Should().BeOfType<DbUpdateException>().Which.InnerException.Should().BeOfType<SqlException>()
            .Which.Number.Should().Be(2601);
        DeveTerSidoErro(r, 409, LogLevel.Warning);
    }

    [Fact]
    public async Task DeadlockReal_ComRequisicaoAbortada_DeveContinuarRespondendo500ComErroLogado()
    {
        await using (var preparo = new SqlConnection(_infra.ConexaoSql(_banco)))
        {
            await preparo.OpenAsync();
            await using var criar = preparo.CreateCommand();
            criar.CommandText = "CREATE TABLE dbo.Deadlock (Id INT PRIMARY KEY, V INT); INSERT INTO dbo.Deadlock VALUES (1, 0), (2, 0)";
            await criar.ExecuteNonQueryAsync();
        }

        await using var outra = new SqlConnection(_infra.ConexaoSql(_banco));
        await outra.OpenAsync();
        await using var transacaoDaOutra = outra.BeginTransaction();
        await ExecutarAsync(outra, transacaoDaOutra, "SET DEADLOCK_PRIORITY HIGH; UPDATE dbo.Deadlock SET V = 1 WHERE Id = 1");

        Task? esperaDaOutra = null;
        var r = await ExecutarNoMiddlewareAsync(async _ =>
        {
            // Esta sessão é a vítima escolhida (prioridade baixa): segura a linha 2, espera a outra pedir a mesma linha e
            // então pede a linha 1.
            await using var vitima = new SqlConnection(_infra.ConexaoSql(_banco));
            await vitima.OpenAsync();
            await using var transacaoDaVitima = vitima.BeginTransaction();
            await ExecutarAsync(vitima, transacaoDaVitima, "SET DEADLOCK_PRIORITY LOW; UPDATE dbo.Deadlock SET V = 1 WHERE Id = 2");
            esperaDaOutra = ExecutarAsync(outra, transacaoDaOutra, "UPDATE dbo.Deadlock SET V = 2 WHERE Id = 2");
            await Task.Delay(1000);
            await ExecutarAsync(vitima, transacaoDaVitima, "UPDATE dbo.Deadlock SET V = 2 WHERE Id = 1");
        }, clienteSaiNoMeio: false);

        await esperaDaOutra!;
        await transacaoDaOutra.RollbackAsync();
        r.Capturada.Should().BeOfType<SqlException>().Which.Number.Should().Be(1205);
        DeveTerSidoErro(r, 500, LogLevel.Error);
    }

    [Fact]
    public async Task SessaoDerrubadaReal_ComRequisicaoAbortada_DeveContinuarRespondendo500ComErroLogado()
    {
        var r = await ExecutarNoMiddlewareAsync(async semToken =>
        {
            await using var conexao = new SqlConnection(_infra.ConexaoSql(_banco));
            await conexao.OpenAsync();
            short sessao;
            await using (var consulta = conexao.CreateCommand())
            {
                consulta.CommandText = "SELECT @@SPID";
                sessao = Convert.ToInt16(await consulta.ExecuteScalarAsync());
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                await using var administrador = new SqlConnection(_infra.ConexaoSql("master"));
                await administrador.OpenAsync();
                await using var kill = administrador.CreateCommand();
                kill.CommandText = $"KILL {sessao}";
                await kill.ExecuteNonQueryAsync();
            });

            await using var longo = conexao.CreateCommand();
            longo.CommandText = "WAITFOR DELAY '00:00:30'";
            await longo.ExecuteNonQueryAsync();
        }, clienteSaiNoMeio: false);

        r.Capturada.Should().BeOfType<SqlException>().Which.Number.Should().Be(596);
        DeveTerSidoErro(r, 500, LogLevel.Error);
    }

    private static async Task ExecutarAsync(SqlConnection conexao, SqlTransaction transacao, string sql)
    {
        await using var comando = conexao.CreateCommand();
        comando.Transaction = transacao;
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
    }
}
