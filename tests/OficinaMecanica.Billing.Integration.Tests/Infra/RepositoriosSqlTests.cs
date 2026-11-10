using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Infrastructure.Data;
using OficinaMecanica.Billing.Infrastructure.Repositories;

namespace OficinaMecanica.Billing.Integration.Tests.Infra;

// Esquema (migration), restrições e repositórios sobre um SQL Server real: é aqui que se prova que o modelo do EF, o
// banco criado pela migration e os índices únicos que sustentam a idempotência dizem a mesma coisa.
[Trait("Category", "Infra")]
[Collection(ColecaoInfra.Nome)]
public sealed class RepositoriosSqlTests : IAsyncLifetime
{
    private readonly InfraFixture _infra;
    private readonly string _banco = $"BillingRepo_{Guid.NewGuid():N}"[..24];

    public RepositoriosSqlTests(InfraFixture infra)
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

    private async Task SalvarAsync(params object[] entidades)
    {
        await using var db = NovoContexto();
        db.AddRange(entidades);
        await db.SaveChangesAsync();
    }

    private async Task<List<string>> ConsultarTextosAsync(string sql)
    {
        await using var conexao = new SqlConnection(_infra.ConexaoSql(_banco));
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        await using var leitor = await comando.ExecuteReaderAsync();
        var resultado = new List<string>();
        while (await leitor.ReadAsync())
            resultado.Add(leitor.GetValue(0)?.ToString() ?? string.Empty);
        return resultado;
    }

    // ---- Esquema ----

    [Fact]
    public async Task Migration_DeveCriarAsTabelasDoBillingEDoMassTransit()
    {
        var tabelas = await ConsultarTextosAsync("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'");

        tabelas.Should().Contain(new[]
        {
            "Orcamentos", "ItensOrcamento", "Pagamentos", "TentativasPagamento", "Estornos", "WebhooksRecebidos",
            "CancelamentosPrevios", "InboxState", "OutboxMessage", "OutboxState",
        });
    }

    [Fact]
    public async Task Migration_DeveCriarOsIndicesUnicosQueSustentamAIdempotencia()
    {
        var unicos = await ConsultarTextosAsync("SELECT name FROM sys.indexes WHERE is_unique = 1 AND name LIKE 'IX_%'");

        unicos.Should().Contain(new[]
        {
            "IX_Orcamentos_OrdemDeServicoId", "IX_Pagamentos_OrdemDeServicoId", "IX_TentativasPagamento_GatewayPaymentId",
            "IX_WebhooksRecebidos_RequestId", "IX_CancelamentosPrevios_OrdemDeServicoId_Tipo", "IX_Estornos_PagamentoId",
        });
    }

    [Fact]
    public async Task Migration_ValoresMonetariosDevemSerDecimal18Com2Casas()
    {
        var colunas = await ConsultarTextosAsync(
            "SELECT TABLE_NAME + '.' + COLUMN_NAME + ':' + CAST(NUMERIC_PRECISION AS varchar) + ',' + CAST(NUMERIC_SCALE AS varchar) " +
            "FROM INFORMATION_SCHEMA.COLUMNS WHERE DATA_TYPE = 'decimal'");

        colunas.Should().BeEquivalentTo(new[]
        {
            "Orcamentos.ValorTotal:18,2", "ItensOrcamento.PrecoUnitario:18,2", "Pagamentos.Valor:18,2", "TentativasPagamento.ValorGateway:18,2",
        });
    }

    [Fact]
    public async Task Modelo_NaoDeveTerMudancasPendentesEmRelacaoAMigration()
    {
        await using var db = NovoContexto();

        db.Database.HasPendingModelChanges().Should().BeFalse("a migration inicial precisa refletir o modelo atual");
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    // ---- Ida e volta dos agregados ----

    [Fact]
    public async Task Pagamento_ComTentativasEEstorno_DevePersistirECarregarTudoComOsMesmosValores()
    {
        var pagamento = new PagamentoBuilder().ComValor(1234.56m).ComGatewayPaymentId("700100").AprovadoComAprovacaoDuplicada("700200");
        pagamento.IniciarEstorno();
        pagamento.FalharEstorno("O gateway recusou o estorno.");
        pagamento.RegistrarEstornoDeAprovacaoDuplicada("700200", "refund-dup-9");
        await SalvarAsync(pagamento);

        await using var db = NovoContexto();
        var carregado = await new PagamentoRepository(db).ObterPorIdAsync(pagamento.Id);

        carregado.Should().NotBeNull();
        carregado!.Valor.Should().Be(1234.56m);
        carregado.Status.Should().Be(StatusPagamento.EstornoFalhou);
        carregado.GatewayPaymentIdAprovado.Should().Be("700100");
        carregado.Tentativas.Select(t => t.GatewayPaymentId).Should().BeEquivalentTo("700100", "700200");
        carregado.Tentativas.Single(t => t.GatewayPaymentId == "700200").GatewayRefundId.Should().Be("refund-dup-9");
        carregado.Estorno.Should().NotBeNull();
        carregado.Estorno!.Status.Should().Be(StatusEstorno.Falhou);
        carregado.Estorno.Tentativas.Should().Be(1);
        carregado.Estorno.Erro.Should().Be("O gateway recusou o estorno.");
        carregado.Estorno.AbandonadoEm.Should().BeNull();
    }

    [Fact]
    public async Task Orcamento_ComItens_DevePersistirECarregarPelaOrdemDeServicoEPeloId()
    {
        var orcamento = new OrcamentoBuilder().Gerado();
        await SalvarAsync(orcamento);

        await using var db = NovoContexto();
        var repositorio = new OrcamentoRepository(db);
        var pelaOrdem = await repositorio.ObterPorOrdemDeServicoIdAsync(orcamento.OrdemDeServicoId);
        var peloId = await repositorio.ObterPorIdAsync(orcamento.Id);

        pelaOrdem!.Itens.Should().HaveCount(orcamento.Itens.Count);
        pelaOrdem.ValorTotal.Should().Be(orcamento.ValorTotal);
        pelaOrdem.Itens.Sum(i => i.Subtotal).Should().Be(orcamento.ValorTotal);
        peloId!.Id.Should().Be(orcamento.Id);
        (await repositorio.ObterPorIdAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task FilhoNovoDeAgregadoJaRastreado_DeveSerInseridoEmVezDeAtualizado()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        await SalvarAsync(pagamento);

        await using (var db = NovoContexto())
        {
            var carregado = await new PagamentoRepository(db).ObterPorIdAsync(pagamento.Id);
            carregado!.RegistrarTentativa("800100", "rejected", "cc_rejected_other_reason", carregado.Valor, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await using var conferencia = NovoContexto();
        (await new PagamentoRepository(conferencia).ObterPorIdAsync(pagamento.Id))!.Tentativas.Should().ContainSingle();
    }

    [Fact]
    public async Task Paginacao_DeveOrdenarDoMaisRecenteParaOMaisAntigoEFiltrarPorStatus()
    {
        var baseTempo = DateTimeOffset.UtcNow.AddHours(-1);
        var orcamentos = Enumerable.Range(0, 5)
            .Select(i => new OrcamentoBuilder().CriadoEm(baseTempo.AddMinutes(i)).ValidoAte(baseTempo.AddDays(3)).Gerado())
            .ToList();
        var cancelado = new OrcamentoBuilder().CriadoEm(baseTempo.AddMinutes(10)).ValidoAte(baseTempo.AddDays(3)).Cancelado();
        await SalvarAsync(orcamentos.Cast<object>().Append(cancelado).ToArray());

        await using var db = NovoContexto();
        var repositorio = new OrcamentoRepository(db);
        var segundaPagina = (await repositorio.ListarAsync(2, 2, StatusOrcamento.Gerado)).ToList();
        var soCancelados = (await repositorio.ListarAsync(1, 10, StatusOrcamento.Cancelado)).ToList();

        segundaPagina.Select(o => o.Id).Should().Equal(orcamentos[2].Id, orcamentos[1].Id);
        soCancelados.Should().ContainSingle().Which.Id.Should().Be(cancelado.Id);
        (await repositorio.ContarAsync()).Should().Be(6);
        (await repositorio.ContarAsync(StatusOrcamento.Gerado)).Should().Be(5);
    }

    // ---- Restricoes de unicidade ----

    private static void DeveSerViolacaoDeUnicidade(Exception? excecao) =>
        (excecao as DbUpdateException)?.InnerException.Should().BeOfType<SqlException>().Which.Number.Should().BeOneOf(2601, 2627);

    [Fact]
    public async Task Orcamento_SegundoParaAMesmaOrdemDeServico_DeveViolarOIndiceUnico()
    {
        var ordem = Guid.NewGuid();
        await SalvarAsync(new OrcamentoBuilder().DaOrdemDeServico(ordem).Gerado());

        var act = () => SalvarAsync(new OrcamentoBuilder().DaOrdemDeServico(ordem).Gerado());

        DeveSerViolacaoDeUnicidade((await act.Should().ThrowAsync<DbUpdateException>()).Which);
    }

    [Fact]
    public async Task Pagamento_SegundoParaAMesmaOrdemDeServico_DeveViolarOIndiceUnico()
    {
        var ordem = Guid.NewGuid();
        await SalvarAsync(new PagamentoBuilder().DaOrdemDeServico(ordem).ComPreferencia());

        var act = () => SalvarAsync(new PagamentoBuilder().DaOrdemDeServico(ordem).ComPreferencia());

        DeveSerViolacaoDeUnicidade((await act.Should().ThrowAsync<DbUpdateException>()).Which);
    }

    [Fact]
    public async Task TentativaPagamento_MesmoPaymentIdEmDoisPagamentos_DeveViolarOIndiceUnico()
    {
        await SalvarAsync(new PagamentoBuilder().ComGatewayPaymentId("900100").Aprovado());

        var act = () => SalvarAsync(new PagamentoBuilder().ComGatewayPaymentId("900100").Aprovado());

        DeveSerViolacaoDeUnicidade((await act.Should().ThrowAsync<DbUpdateException>()).Which);
    }

    [Fact]
    public async Task WebhookRecebido_MesmoRequestId_DeveViolarOIndiceUnico()
    {
        await SalvarAsync(new WebhookRecebidoBuilder().ComRequestId("req-unico").Recebido());

        var act = () => SalvarAsync(new WebhookRecebidoBuilder().ComRequestId("req-unico").Recebido());

        DeveSerViolacaoDeUnicidade((await act.Should().ThrowAsync<DbUpdateException>()).Which);
    }

    [Fact]
    public async Task CancelamentoPrevio_MesmaOrdemETipo_DeveViolarOIndiceUnicoMasTiposDiferentesConvivem()
    {
        var ordem = Guid.NewGuid();
        await SalvarAsync(new CancelamentoPrevioBuilder().DaOrdemDeServico(ordem).DoTipo(TipoCancelamentoPrevio.Orcamento).Build());
        await SalvarAsync(new CancelamentoPrevioBuilder().DaOrdemDeServico(ordem).DoTipo(TipoCancelamentoPrevio.Pagamento).Build());

        var act = () => SalvarAsync(new CancelamentoPrevioBuilder().DaOrdemDeServico(ordem).DoTipo(TipoCancelamentoPrevio.Orcamento).Build());

        DeveSerViolacaoDeUnicidade((await act.Should().ThrowAsync<DbUpdateException>()).Which);
        await using var db = NovoContexto();
        var repositorio = new CancelamentoPrevioRepository(db);
        (await repositorio.ObterAsync(ordem, TipoCancelamentoPrevio.Orcamento)).Should().NotBeNull();
        (await repositorio.ObterAsync(ordem, TipoCancelamentoPrevio.Pagamento)).Should().NotBeNull();
        (await repositorio.ObterAsync(Guid.NewGuid(), TipoCancelamentoPrevio.Pagamento)).Should().BeNull();
    }

    [Fact]
    public async Task WebhookRecebido_DeveSerEncontradoPeloRequestId()
    {
        var webhook = new WebhookRecebidoBuilder().ComRequestId("req-busca").Recebido();
        await SalvarAsync(webhook);

        await using var db = NovoContexto();
        var repositorio = new WebhookRecebidoRepository(db);

        (await repositorio.ObterPorRequestIdAsync("req-busca"))!.Id.Should().Be(webhook.Id);
        (await repositorio.ObterPorRequestIdAsync("req-outro")).Should().BeNull();
    }

    [Fact]
    public async Task Estorno_SegundoParaOMesmoPagamento_DeveViolarOIndiceUnicoDaChaveEstrangeira()
    {
        var pagamento = new PagamentoBuilder().Estornando();
        await SalvarAsync(pagamento);

        var act = async () =>
        {
            await using var db = NovoContexto();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Estornos (Id, Tentativas, Status, PagamentoId) VALUES (NEWID(), 0, 1, {0})", pagamento.Id);
        };

        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().BeOneOf(2601, 2627);
    }

    // ---- Concorrencia otimista ----

    [Fact]
    public async Task Pagamento_DuasAlteracoesConcorrentes_ASegundaDeveFalharPeloTokenDeVersao()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        await SalvarAsync(pagamento);

        await using var primeiro = NovoContexto();
        await using var segundo = NovoContexto();
        var a = await new PagamentoRepository(primeiro).ObterPorIdAsync(pagamento.Id);
        var b = await new PagamentoRepository(segundo).ObterPorIdAsync(pagamento.Id);
        a!.Cancelar("Cancelado por A");
        b!.Cancelar("Cancelado por B");

        await primeiro.SaveChangesAsync();
        var act = () => segundo.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        await using var conferencia = NovoContexto();
        var final = await new PagamentoRepository(conferencia).ObterPorIdAsync(pagamento.Id);
        final!.MotivoCancelamento.Should().Be("Cancelado por A");
        final.Version.Should().Be(pagamento.Version + 1);
    }

    [Fact]
    public async Task Orcamento_DuasAlteracoesConcorrentes_ASegundaDeveFalharPeloTokenDeVersao()
    {
        var orcamento = new OrcamentoBuilder().Gerado();
        await SalvarAsync(orcamento);

        await using var primeiro = NovoContexto();
        await using var segundo = NovoContexto();
        var a = await new OrcamentoRepository(primeiro).ObterPorIdAsync(orcamento.Id);
        var b = await new OrcamentoRepository(segundo).ObterPorIdAsync(orcamento.Id);
        a!.Cancelar("A");
        b!.Cancelar("B");

        await primeiro.SaveChangesAsync();
        var act = () => segundo.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task ExcluirPagamento_DeveRemoverTentativasEEstornoEmCascata()
    {
        var pagamento = new PagamentoBuilder().ComGatewayPaymentId("950100").Estornando();
        await SalvarAsync(pagamento);

        await using (var db = NovoContexto())
        {
            var carregado = await new PagamentoRepository(db).ObterPorIdAsync(pagamento.Id);
            db.Pagamentos.Remove(carregado!);
            await db.SaveChangesAsync();
        }

        await using var conferencia = NovoContexto();
        (await conferencia.TentativasPagamento.CountAsync()).Should().Be(0);
        (await conferencia.Estornos.CountAsync()).Should().Be(0);
    }

    // ---- Consulta da reconciliacao ----

    [Fact]
    public async Task ListarIdsParaReconciliacao_DeveConcordarComOsPredicadosDoDominioEmTodosOsEstados()
    {
        var pagamentos = new List<Pagamento>
        {
            new PagamentoBuilder().ComPreferencia(),
            new PagamentoBuilder().Aprovado(),
            new PagamentoBuilder().Cancelado(),
            new PagamentoBuilder().Expirado(),
            new PagamentoBuilder().AprovadoAposCancelamento(),
            new PagamentoBuilder().AprovadoAposExpiracao(),
            new PagamentoBuilder().Estornando(),
            new PagamentoBuilder().ComEstornoFalho(),
            new PagamentoBuilder().Estornado(),
            new PagamentoBuilder().ComGatewayPaymentId("960100").AprovadoComAprovacaoDuplicada("960101"),
            new PagamentoBuilder().ComGatewayPaymentId("960200").AprovadoComAprovacaoDuplicada("960201"),
        };
        // A segunda duplicidade já foi devolvida; a terceira só tem tentativa rejeitada além da aprovada.
        pagamentos[10].RegistrarEstornoDeAprovacaoDuplicada("960201", "refund-dup-ok");
        // Estorno abandonado sai da lista; abandonado com cobranca em duplicidade pendente continua nela.
        var abandonado = new PagamentoBuilder().ComEstornoFalho();
        abandonado.AbandonarEstorno(DateTimeOffset.UtcNow);
        pagamentos.Add(abandonado);
        var abandonadoComDuplicidade = new PagamentoBuilder().ComGatewayPaymentId("960400").AprovadoComAprovacaoDuplicada("960401");
        abandonadoComDuplicidade.IniciarEstorno();
        abandonadoComDuplicidade.FalharEstorno("falha");
        abandonadoComDuplicidade.AbandonarEstorno(DateTimeOffset.UtcNow);
        pagamentos.Add(abandonadoComDuplicidade);
        var comRejeitada = new PagamentoBuilder().Aprovado();
        comRejeitada.RegistrarTentativa("960300", "rejected", "cc_rejected_other_reason", comRejeitada.Valor, DateTimeOffset.UtcNow);
        pagamentos.Add(comRejeitada);
        await SalvarAsync(pagamentos.Cast<object>().ToArray());

        await using var db = NovoContexto();
        var ids = await new PagamentoRepository(db).ListarIdsParaReconciliacaoAsync(100);

        var esperados = pagamentos
            .Where(p => (p.EstornoPendente && !p.EstornoAbandonado) || p.AprovacoesDuplicadasSemEstorno().Count > 0)
            .Select(p => p.Id);
        ids.Should().BeEquivalentTo(esperados);
        ids.Should().HaveCount(6);
    }

    [Fact]
    public async Task ListarIdsParaReconciliacao_DeveRespeitarOLimiteEPriorizarQuemTemMenosTentativasDeEstorno()
    {
        var falhouVezes = new PagamentoBuilder().Estornando();
        for (var i = 0; i < 3; i++)
        {
            falhouVezes.FalharEstorno("falha");
            falhouVezes.IniciarEstorno();
        }
        falhouVezes.FalharEstorno("falha");
        var semTentativas = new PagamentoBuilder().AprovadoAposCancelamento();
        var umaFalha = new PagamentoBuilder().ComEstornoFalho();
        await SalvarAsync(falhouVezes, semTentativas, umaFalha);

        await using var db = NovoContexto();
        var repositorio = new PagamentoRepository(db);

        (await repositorio.ListarIdsParaReconciliacaoAsync(2)).Should().Equal(semTentativas.Id, umaFalha.Id);
        (await repositorio.ListarIdsParaReconciliacaoAsync(1)).Should().Equal(semTentativas.Id);
        (await repositorio.ListarIdsParaReconciliacaoAsync(10)).Should().Equal(semTentativas.Id, umaFalha.Id, falhouVezes.Id);
    }
}
