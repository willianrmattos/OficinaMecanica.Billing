using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.API.Middleware;
using OficinaMecanica.Billing.Integration.Tests.Support;

namespace OficinaMecanica.Billing.Integration.Tests.Api;

// Casos com os números e classes de erro que o SqlClient devolveu de verdade (capturados de um SQL Server real). Aqui as
// SqlException são montadas por reflexão; CancelamentoDaRequisicaoSqlTests (Category=Infra) repete a decisão contra
// as exceções verdadeiras.
public class CancelamentoDaRequisicaoTests
{
    [Fact]
    public void OperationCanceledException_DeveSerCancelamento() =>
        CancelamentoDaRequisicao.Eh(new OperationCanceledException()).Should().BeTrue();

    [Fact]
    public void TaskCanceledException_DeveSerCancelamento() =>
        CancelamentoDaRequisicao.Eh(new TaskCanceledException()).Should().BeTrue();

    [Fact]
    public void CancelamentoDeComandoDoSqlClient_DeveSerCancelamento() =>
        CancelamentoDaRequisicao.Eh(SqlExceptionDeTeste.CancelamentoDeComando()).Should().BeTrue();

    [Fact]
    public void CancelamentoDeComandoEmbrulhadoPeloEf_DeveSerCancelamento() =>
        CancelamentoDaRequisicao.Eh(new DbUpdateException("falha ao salvar", SqlExceptionDeTeste.CancelamentoDeComando()))
            .Should().BeTrue();

    [Fact]
    public void CancelamentoEmbrulhadoEmOutraExcecao_DeveSerCancelamento() =>
        CancelamentoDaRequisicao.Eh(new InvalidOperationException("falha", new TaskCanceledException()))
            .Should().BeTrue();

    [Fact]
    public void CancelamentoCujoTextoFoiLocalizado_DeveSerCancelamentoPeloNumeroEPelaClasse()
    {
        var localizada = SqlExceptionDeTeste.Criar(
            new SqlExceptionDeTeste.ErroDeSql(0, 11, 0, "Ocorreu um erro grave no comando atual. Os resultados devem ser descartados."),
            new SqlExceptionDeTeste.ErroDeSql(0, 11, 0, "Operação cancelada pelo usuário."));

        CancelamentoDaRequisicao.Eh(localizada).Should().BeTrue();
    }

    public static IEnumerable<object[]> ErrosReaisDeSql() => new[]
    {
        new object[] { "timeout de comando (-2)", SqlExceptionDeTeste.TimeoutDeComando() },
        new object[] { "deadlock (1205)", SqlExceptionDeTeste.Deadlock() },
        new object[] { "índice único duplicado (2601)", SqlExceptionDeTeste.IndiceUnicoDuplicado() },
        new object[] { "sessão derrubada (596)", SqlExceptionDeTeste.SessaoDerrubada() },
        new object[] { "login recusado (4060/18456)", SqlExceptionDeTeste.LoginRecusado() },
    };

    [Theory]
    [MemberData(nameof(ErrosReaisDeSql))]
    public void ErroRealDeSql_NaoDeveSerCancelamento(string descricao, Exception erro)
    {
        descricao.Should().NotBeNullOrEmpty();

        CancelamentoDaRequisicao.Eh(erro).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(ErrosReaisDeSql))]
    public void ErroRealDeSqlEmbrulhadoPeloEf_NaoDeveSerCancelamento(string descricao, Exception erro)
    {
        descricao.Should().NotBeNullOrEmpty();

        CancelamentoDaRequisicao.Eh(new DbUpdateException("falha ao salvar", erro)).Should().BeFalse();
    }

    [Fact]
    public void ErroRealDeSqlEmbrulhadoPorOutraExcecao_NaoDeveSerCancelamento()
    {
        // O primeiro elo de SQL decide: um erro real que embrulha um cancelamento não vira abandono.
        var erro = new InvalidOperationException("falha", SqlExceptionDeTeste.Deadlock());

        CancelamentoDaRequisicao.Eh(erro).Should().BeFalse();
    }

    [Fact]
    public void SqlExceptionRealComCausaDeCancelamento_NaoDeveSerCancelamentoPorqueOPrimeiroElodeSqlDecide()
    {
        // A busca na cadeia termina na primeira SqlException: um erro real (deadlock) cuja exceção interna é um cancelamento
        // continua sendo erro, e a causa não pode transformá-lo em abandono.
        var causa = new OperationCanceledException();
        var deadlock = SqlExceptionDeTeste.CriarComCausa(
            causa, new SqlExceptionDeTeste.ErroDeSql(1205, 13, 51, "Transaction was deadlocked on lock resources."));
        deadlock.InnerException.Should().BeSameAs(causa, "o helper precisa montar a SqlException com a causa para o teste valer");

        CancelamentoDaRequisicao.Eh(deadlock).Should().BeFalse();
        CancelamentoDaRequisicao.Eh(new DbUpdateException("falha ao salvar", deadlock)).Should().BeFalse();
    }

    [Fact]
    public void ErroSemRelacaoComCancelamento_NaoDeveSerCancelamento()
    {
        CancelamentoDaRequisicao.Eh(new InvalidOperationException("falha real")).Should().BeFalse();
        CancelamentoDaRequisicao.Eh(new DbUpdateConcurrencyException("conflito")).Should().BeFalse();
        CancelamentoDaRequisicao.Eh(new KeyNotFoundException("nada")).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 20, 0)]
    [InlineData(0, 11, 1)]
    [InlineData(1, 11, 0)]
    [InlineData(-2, 11, 0)]
    public void ErroQueDesviaDeNumeroClasseOuEstadoDoCancelamento_NaoDeveSerCancelamento(int numero, byte classe, byte estado)
    {
        var erro = SqlExceptionDeTeste.Criar(new SqlExceptionDeTeste.ErroDeSql(numero, classe, estado, "A severe error occurred on the current command."));

        CancelamentoDaRequisicao.Eh(erro).Should().BeFalse();
    }

    [Fact]
    public void SqlExceptionComUmErroDoServidorMisturadoAoCancelamento_NaoDeveSerCancelamento()
    {
        var mista = SqlExceptionDeTeste.Criar(
            new SqlExceptionDeTeste.ErroDeSql(0, 11, 0, "Operation cancelled by user."),
            new SqlExceptionDeTeste.ErroDeSql(2601, 14, 1, "Cannot insert duplicate key row."));

        CancelamentoDaRequisicao.Eh(mista).Should().BeFalse();
    }
}
