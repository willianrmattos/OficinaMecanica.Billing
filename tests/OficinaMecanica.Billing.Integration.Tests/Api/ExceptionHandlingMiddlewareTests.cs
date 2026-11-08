using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OficinaMecanica.Billing.API.Middleware;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Domain.Exceptions;
using OficinaMecanica.Billing.Integration.Tests.Support;
using static OficinaMecanica.Billing.Integration.Tests.Support.MiddlewareDeTeste;

namespace OficinaMecanica.Billing.Integration.Tests.Api;

public class ExceptionHandlingMiddlewareTests
{
    private static async Task<(int Status, JsonElement Corpo, IHeaderDictionary Cabecalhos)> ExecutarAsync(Exception? excecao)
    {
        var contexto = new DefaultHttpContext();
        contexto.Response.Body = new MemoryStream();
        var middleware = new ExceptionHandlingMiddleware(
            _ => excecao is null ? Task.CompletedTask : Task.FromException(excecao),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(contexto);

        contexto.Response.Body.Position = 0;
        var texto = await new StreamReader(contexto.Response.Body).ReadToEndAsync();
        var corpo = texto.Length == 0 ? default : JsonDocument.Parse(texto).RootElement;
        return (contexto.Response.StatusCode, corpo, contexto.Response.Headers);
    }

    [Fact]
    public async Task SemExcecao_DeveDeixarAResposta200IntactaSemCorpo()
    {
        var (status, corpo, _) = await ExecutarAsync(null);

        status.Should().Be(200);
        corpo.ValueKind.Should().Be(JsonValueKind.Undefined);
    }

    [Fact]
    public async Task DomainException_DeveResponder400ComAMensagemDoDominio()
    {
        var (status, corpo, _) = await ExecutarAsync(new DomainException("O valor do pagamento deve ser maior que zero."));

        status.Should().Be(400);
        corpo.GetProperty("tipo").GetString().Should().Be("Erro de Negócio");
        corpo.GetProperty("mensagem").GetString().Should().Be("O valor do pagamento deve ser maior que zero.");
    }

    [Fact]
    public async Task ValidationException_DeveResponder400ComOsErrosPorCampo()
    {
        var excecao = new ValidationException(new[]
        {
            new ValidationFailure("Limite", "O limite deve estar entre 1 e 1000."),
            new ValidationFailure("Pagina", "A página deve ser maior que zero."),
        });

        var (status, corpo, _) = await ExecutarAsync(excecao);

        status.Should().Be(400);
        corpo.GetProperty("tipo").GetString().Should().Be("Erro de Validação");
        corpo.GetProperty("mensagem").GetString().Should().Be("Erro de validação.");
        corpo.GetProperty("erros").EnumerateArray().Select(e => e.GetProperty("campo").GetString())
            .Should().Equal("Limite", "Pagina");
    }

    [Fact]
    public async Task KeyNotFoundException_DeveResponder404SemRepassarOTextoDaExcecao()
    {
        var (status, corpo, _) = await ExecutarAsync(new KeyNotFoundException("Pagamento 123 não encontrado."));

        status.Should().Be(404);
        corpo.GetProperty("tipo").GetString().Should().Be("Não Encontrado");
        corpo.GetProperty("mensagem").GetString().Should().Be("Recurso não encontrado.");
    }

    [Fact]
    public async Task AssinaturaWebhookInvalidaException_DeveResponder401()
    {
        var (status, corpo, _) = await ExecutarAsync(new AssinaturaWebhookInvalidaException());

        status.Should().Be(401);
        corpo.GetProperty("tipo").GetString().Should().Be("Não Autorizado");
        corpo.GetProperty("mensagem").GetString().Should().Be("Assinatura do webhook inválida.");
    }

    [Fact]
    public async Task PagamentoDoGatewayIndisponivelException_DeveResponder503ComRetryAfter()
    {
        var (status, corpo, cabecalhos) = await ExecutarAsync(new PagamentoDoGatewayIndisponivelException("999"));

        status.Should().Be(503);
        cabecalhos["Retry-After"].ToString().Should().Be("30");
        corpo.GetProperty("tipo").GetString().Should().Be("Serviço Indisponível");
        corpo.GetProperty("mensagem").GetString().Should().NotContain("999");
    }

    [Fact]
    public async Task DbUpdateConcurrencyException_DeveResponder409()
    {
        var (status, corpo, _) = await ExecutarAsync(new DbUpdateConcurrencyException("conflito"));

        status.Should().Be(409);
        corpo.GetProperty("tipo").GetString().Should().Be("Conflito");
    }

    [Fact]
    public async Task ExcecaoInesperada_DeveResponder500SemVazarDetalhesInternos()
    {
        var (status, corpo, _) = await ExecutarAsync(
            new InvalidOperationException("Server=tcp:banco;Password=segredo123 falhou"));

        status.Should().Be(500);
        corpo.GetProperty("tipo").GetString().Should().Be("Erro Interno");
        corpo.GetProperty("mensagem").GetString().Should().Be("Ocorreu um erro interno no servidor.");
        corpo.ToString().Should().NotContain("segredo123").And.NotContain("Server=");
    }

    [Fact]
    public async Task DbUpdateExceptionQueNaoEhViolacaoDeUnicidade_DeveResponder500()
    {
        var (status, _, _) = await ExecutarAsync(new DbUpdateException("falha", new InvalidOperationException("outra causa")));

        status.Should().Be(500);
    }

    [Fact]
    public async Task Resposta_DeveSerJsonEmCamelCase()
    {
        var contexto = new DefaultHttpContext();
        contexto.Response.Body = new MemoryStream();
        var middleware = new ExceptionHandlingMiddleware(
            _ => Task.FromException(new DomainException("x")), NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(contexto);

        contexto.Response.ContentType.Should().Be("application/json");
        contexto.Response.Body.Position = 0;
        (await new StreamReader(contexto.Response.Body).ReadToEndAsync()).Should().StartWith("{\"tipo\":");
    }

    // Cliente que fecha a conexão (timeout do chamador, do APIM ou do Mercado Pago): o RequestAborted dispara e quem usava
    // o token reage com OperationCanceledException (HttpClient, EF com o token já cancelado) ou, no caso de um comando SQL
    // em voo, com SqlException. Os testes abaixo fixam o que é abandono e o que continua erro.
    private static Exception Cancelamento(string tipo, CancellationToken token) => tipo == nameof(TaskCanceledException)
        ? new TaskCanceledException("A tarefa foi cancelada.", null, token)
        : new OperationCanceledException("The operation was canceled.", token);

    // Uma requisição abortada: o token já está cancelado quando a exceção sobe.
    private static async Task<(DefaultHttpContext Contexto, Mock<ILogger<ExceptionHandlingMiddleware>> Logger)> ExecutarAbortadaAsync(
        Func<Exception> excecao, Action<DefaultHttpContext>? preparar = null)
    {
        using var abortada = new CancellationTokenSource();
        var logger = new Mock<ILogger<ExceptionHandlingMiddleware>>();
        var contexto = NovoContexto(abortada.Token);
        preparar?.Invoke(contexto);
        var middleware = new ExceptionHandlingMiddleware(
            _ =>
            {
                abortada.Cancel();
                throw excecao();
            },
            logger.Object);

        await middleware.InvokeAsync(contexto);

        return (contexto, logger);
    }

    [Theory]
    [InlineData(nameof(OperationCanceledException))]
    [InlineData(nameof(TaskCanceledException))]
    public async Task ClienteQueFechouAConexao_NaoDeveEscreverCorpoNemLogarErro(string tipo)
    {
        var (contexto, logger) = await ExecutarAbortadaAsync(
            () => Cancelamento(tipo, CancellationToken.None),
            c =>
            {
                c.Request.Path = "/api/webhooks/pagamentos/987654321";
                DefinirRota(c, "/api/webhooks/pagamentos/{id}");
                c.Request.QueryString = new QueryString("?access_token=segredo-da-query");
                c.Request.Headers.Authorization = "Bearer segredo-do-cabecalho";
            });

        CorpoEscrito(contexto).Should().BeEmpty();
        contexto.Response.ContentType.Should().BeNull();
        contexto.Response.StatusCode.Should().Be(StatusCodes.Status499ClientClosedRequest);
        var registros = Registros(logger);
        registros.Should().NotContain(r => r.Nivel >= LogLevel.Warning);
        registros.Should().ContainSingle(r => r.Nivel == LogLevel.Information)
            .Which.Mensagem.Should().Contain("/api/webhooks/pagamentos/{id}")
            .And.NotContain("segredo");
        registros.Should().OnlyContain(r => r.Excecao == null);
    }

    [Fact]
    public async Task ClienteQueFechouAConexao_DeveLogarOTemplateDaRotaENuncaOCaminhoComDadoSensivel()
    {
        var (_, logger) = await ExecutarAbortadaAsync(
            () => new OperationCanceledException(),
            c =>
            {
                c.Request.Path = "/api/qualquer/12345678901";
                DefinirRota(c, "/api/qualquer/{documento}");
            });

        var mensagem = Registros(logger).Should().ContainSingle().Which.Mensagem;
        mensagem.Should().Contain("/api/qualquer/{documento}");
        mensagem.Should().NotContain("12345678901");
    }

    [Fact]
    public async Task ClienteQueFechouAConexao_SemEndpoint_DeveLogarTextoFixoSemOCaminho()
    {
        var (_, logger) = await ExecutarAbortadaAsync(
            () => new OperationCanceledException(),
            c => c.Request.Path = "/api/qualquer/12345678901");

        var mensagem = Registros(logger).Should().ContainSingle().Which.Mensagem;
        mensagem.Should().Contain("(rota desconhecida)");
        mensagem.Should().NotContain("12345678901").And.NotContain("/api/qualquer");
    }

    [Fact]
    public async Task ClienteQueFechouAConexaoComComandoSqlEmVoo_DeveResponder499SemCorpoNemErro()
    {
        var (contexto, logger) = await ExecutarAbortadaAsync(SqlExceptionDeTeste.CancelamentoDeComando);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status499ClientClosedRequest);
        CorpoEscrito(contexto).Should().BeEmpty();
        Registros(logger).Should().NotContain(r => r.Nivel >= LogLevel.Warning);
    }

    [Fact]
    public async Task ClienteQueFechouAConexaoNoSaveChanges_DeveResponder499SemCorpoNemErro()
    {
        // O EF embrulha a SqlException de cancelamento em DbUpdateException.
        var (contexto, logger) = await ExecutarAbortadaAsync(
            () => new DbUpdateException("falha ao salvar", SqlExceptionDeTeste.CancelamentoDeComando()));

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status499ClientClosedRequest);
        CorpoEscrito(contexto).Should().BeEmpty();
        Registros(logger).Should().NotContain(r => r.Nivel >= LogLevel.Warning);
    }

    [Fact]
    public async Task ClienteQueFechouAConexaoComOceEmbrulhadaPeloEf_DeveResponder499()
    {
        var (contexto, logger) = await ExecutarAbortadaAsync(
            () => new DbUpdateException("falha ao salvar", new OperationCanceledException()));

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status499ClientClosedRequest);
        Registros(logger).Should().NotContain(r => r.Nivel >= LogLevel.Warning);
    }

    [Fact]
    public async Task CancelamentoDeComandoSqlComClienteAindaConectado_DeveContinuarRespondendo500ComErroLogado()
    {
        // Sem o RequestAborted cancelado não foi o cliente que saiu (por exemplo, um token de timeout interno).
        var logger = new Mock<ILogger<ExceptionHandlingMiddleware>>();
        var contexto = NovoContexto(CancellationToken.None);
        var excecao = SqlExceptionDeTeste.CancelamentoDeComando();
        var middleware = new ExceptionHandlingMiddleware(_ => throw excecao, logger.Object);

        await middleware.InvokeAsync(contexto);

        contexto.Response.StatusCode.Should().Be(500);
        Registros(logger).Should().ContainSingle(r => r.Nivel == LogLevel.Error).Which.Excecao.Should().BeSameAs(excecao);
    }

    [Fact]
    public async Task TimeoutDeComandoSql_ComClienteQueFechouAConexao_DeveContinuarRespondendo500ComErroLogado()
    {
        var excecao = SqlExceptionDeTeste.TimeoutDeComando();

        var (contexto, logger) = await ExecutarAbortadaAsync(() => excecao);

        contexto.Response.StatusCode.Should().Be(500);
        JsonDocument.Parse(CorpoEscrito(contexto)).RootElement.GetProperty("tipo").GetString().Should().Be("Erro Interno");
        Registros(logger).Should().ContainSingle(r => r.Nivel == LogLevel.Error).Which.Excecao.Should().BeSameAs(excecao);
    }

    [Fact]
    public async Task DeadlockSql_ComClienteQueFechouAConexao_DeveContinuarRespondendo500ComErroLogado()
    {
        var excecao = new DbUpdateException("falha ao salvar", SqlExceptionDeTeste.Deadlock());

        var (contexto, logger) = await ExecutarAbortadaAsync(() => excecao);

        contexto.Response.StatusCode.Should().Be(500);
        Registros(logger).Should().ContainSingle(r => r.Nivel == LogLevel.Error).Which.Excecao.Should().BeSameAs(excecao);
    }

    [Fact]
    public async Task SessaoSqlDerrubada_ComClienteQueFechouAConexao_DeveContinuarRespondendo500ComErroLogado()
    {
        var excecao = SqlExceptionDeTeste.SessaoDerrubada();

        var (contexto, logger) = await ExecutarAbortadaAsync(() => excecao);

        contexto.Response.StatusCode.Should().Be(500);
        Registros(logger).Should().ContainSingle(r => r.Nivel == LogLevel.Error).Which.Excecao.Should().BeSameAs(excecao);
    }

    [Fact]
    public async Task ChaveDuplicadaSql_ComClienteQueFechouAConexao_DeveContinuarRespondendo409()
    {
        var (contexto, logger) = await ExecutarAbortadaAsync(
            () => new DbUpdateException("falha ao salvar", SqlExceptionDeTeste.IndiceUnicoDuplicado()));

        contexto.Response.StatusCode.Should().Be(409);
        JsonDocument.Parse(CorpoEscrito(contexto)).RootElement.GetProperty("tipo").GetString().Should().Be("Conflito");
        Registros(logger).Should().ContainSingle().Which.Nivel.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task ClienteQueFechouAConexao_DeveLogarOTraceIdDaAtividadeCorrente()
    {
        using var atividade = new Activity("teste").Start();
        using var abortada = new CancellationTokenSource();
        var logger = new Mock<ILogger<ExceptionHandlingMiddleware>>();
        var middleware = new ExceptionHandlingMiddleware(
            _ =>
            {
                abortada.Cancel();
                throw new OperationCanceledException(abortada.Token);
            },
            logger.Object);

        await middleware.InvokeAsync(NovoContexto(abortada.Token));

        Registros(logger).Should().ContainSingle().Which.Mensagem.Should().Contain(atividade.TraceId.ToString());
    }

    [Fact]
    public async Task ClienteQueFechouAConexao_SemAtividade_DeveLogarOIdDaRequisicao()
    {
        Activity.Current = null;
        using var abortada = new CancellationTokenSource();
        var logger = new Mock<ILogger<ExceptionHandlingMiddleware>>();
        var contexto = NovoContexto(abortada.Token);
        contexto.TraceIdentifier = "requisicao-123";
        var middleware = new ExceptionHandlingMiddleware(
            _ =>
            {
                abortada.Cancel();
                throw new OperationCanceledException(abortada.Token);
            },
            logger.Object);

        await middleware.InvokeAsync(contexto);

        Registros(logger).Should().ContainSingle().Which.Mensagem.Should().Contain("requisicao-123");
    }

    [Theory]
    [InlineData(nameof(OperationCanceledException))]
    [InlineData(nameof(TaskCanceledException))]
    public async Task CancelamentoComClienteAindaConectado_DeveContinuarRespondendo500ComErroLogado(string tipo)
    {
        // Timeout interno de uma dependência (gateway, banco): o cliente segue esperando e precisa receber a resposta de erro.
        var logger = new Mock<ILogger<ExceptionHandlingMiddleware>>();
        var contexto = NovoContexto(CancellationToken.None);
        var excecao = Cancelamento(tipo, CancellationToken.None);
        var middleware = new ExceptionHandlingMiddleware(_ => throw excecao, logger.Object);

        await middleware.InvokeAsync(contexto);

        contexto.Response.StatusCode.Should().Be(500);
        JsonDocument.Parse(CorpoEscrito(contexto)).RootElement.GetProperty("tipo").GetString().Should().Be("Erro Interno");
        Registros(logger).Should().ContainSingle(r => r.Nivel == LogLevel.Error)
            .Which.Excecao.Should().BeSameAs(excecao);
    }

    [Fact]
    public async Task ExcecaoQueNaoEhCancelamento_ComClienteQueFechouAConexao_DeveContinuarRespondendo500ComErroLogado()
    {
        using var abortada = new CancellationTokenSource();
        abortada.Cancel();
        var logger = new Mock<ILogger<ExceptionHandlingMiddleware>>();
        var contexto = NovoContexto(abortada.Token);
        var excecao = new InvalidOperationException("falha real");
        var middleware = new ExceptionHandlingMiddleware(_ => throw excecao, logger.Object);

        await middleware.InvokeAsync(contexto);

        contexto.Response.StatusCode.Should().Be(500);
        JsonDocument.Parse(CorpoEscrito(contexto)).RootElement.GetProperty("tipo").GetString().Should().Be("Erro Interno");
        Registros(logger).Should().ContainSingle(r => r.Nivel == LogLevel.Error)
            .Which.Excecao.Should().BeSameAs(excecao);
    }

    [Fact]
    public async Task ConflitoDeConcorrencia_ComClienteQueFechouAConexao_DeveContinuarRespondendo409()
    {
        using var abortada = new CancellationTokenSource();
        abortada.Cancel();
        var logger = new Mock<ILogger<ExceptionHandlingMiddleware>>();
        var contexto = NovoContexto(abortada.Token);
        var middleware = new ExceptionHandlingMiddleware(_ => throw new DbUpdateConcurrencyException("conflito"), logger.Object);

        await middleware.InvokeAsync(contexto);

        contexto.Response.StatusCode.Should().Be(409);
        JsonDocument.Parse(CorpoEscrito(contexto)).RootElement.GetProperty("tipo").GetString().Should().Be("Conflito");
        Registros(logger).Should().ContainSingle().Which.Nivel.Should().Be(LogLevel.Warning);
    }

    // Com a resposta já iniciada o fake lança InvalidOperationException ao mudar status ou cabeçalhos, como o Kestrel: o
    // middleware não pode tocar na resposta. Só retornar deixaria o cliente com um 200 truncado, então ele registra o erro e
    // aborta a conexão. OperationCanceledException sem o cliente ter fechado é, por exemplo, o timeout interno de uma dependência.
    private static (DefaultHttpContext Contexto, ConexaoQueRegistraAbort Conexao, Mock<ILogger<ExceptionHandlingMiddleware>> Logger, ExceptionHandlingMiddleware Middleware)
        PrepararRespostaIniciada(Func<ConexaoQueRegistraAbort, Exception> excecao, bool clienteFechou)
    {
        var conexao = new ConexaoQueRegistraAbort();
        var logger = new Mock<ILogger<ExceptionHandlingMiddleware>>();
        var contexto = NovoContexto();
        contexto.Features.Set<IHttpResponseFeature>(new RespostaJaIniciada());
        contexto.Features.Set<IHttpRequestLifetimeFeature>(conexao);
        var middleware = new ExceptionHandlingMiddleware(
            _ =>
            {
                if (clienteFechou)
                    conexao.ClienteFechou();

                throw excecao(conexao);
            },
            logger.Object);

        return (contexto, conexao, logger, middleware);
    }

    [Theory]
    [InlineData(nameof(OperationCanceledException), false)]
    [InlineData(nameof(DomainException), false)]
    [InlineData(nameof(DomainException), true)]
    [InlineData(nameof(InvalidOperationException), false)]
    [InlineData(nameof(InvalidOperationException), true)]
    public async Task ExcecaoComRespostaJaIniciada_NaoDeveTocarNaRespostaRegistrarOErroEAbortarAConexao(string tipo, bool clienteFechou)
    {
        Exception excecao = tipo switch
        {
            nameof(OperationCanceledException) => new OperationCanceledException("timeout interno da dependência"),
            nameof(DomainException) => new DomainException("regra violada no meio da resposta"),
            _ => new InvalidOperationException("falha no meio do corpo")
        };
        var (contexto, conexao, logger, middleware) = PrepararRespostaIniciada(_ => excecao, clienteFechou);

        var acao = () => middleware.InvokeAsync(contexto);

        await acao.Should().NotThrowAsync();
        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        contexto.Response.ContentType.Should().BeNull();
        CorpoEscrito(contexto).Should().BeEmpty();
        conexao.Abortes.Should().Be(1);
        contexto.RequestAborted.IsCancellationRequested.Should().BeTrue();
        var registros = Registros(logger);
        registros.Should().NotContain(r => r.Nivel == LogLevel.Information);
        registros.Should().ContainSingle(r => r.Nivel == LogLevel.Error).Which.Excecao.Should().BeSameAs(excecao);
    }

    [Fact]
    public async Task ClienteQueFechouAConexaoComRespostaJaIniciada_DeveLogarInformationSemAbortarNemMexerNaResposta()
    {
        // A conexão já está fechada: não há erro do servidor nem o que abortar.
        var (contexto, conexao, logger, middleware) = PrepararRespostaIniciada(
            c => new OperationCanceledException(c.RequestAborted), clienteFechou: true);
        contexto.Request.Path = "/api/pagamentos/12345678901";
        contexto.Request.QueryString = new QueryString("?access_token=segredo-da-query");
        DefinirRota(contexto, "api/pagamentos/{id}");

        var acao = () => middleware.InvokeAsync(contexto);

        await acao.Should().NotThrowAsync();
        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        contexto.Response.ContentType.Should().BeNull();
        CorpoEscrito(contexto).Should().BeEmpty();
        conexao.Abortes.Should().Be(0);
        var registros = Registros(logger);
        registros.Should().NotContain(r => r.Nivel >= LogLevel.Warning);
        registros.Should().ContainSingle(r => r.Nivel == LogLevel.Information)
            .Which.Mensagem.Should().Contain("api/pagamentos/{id}")
            .And.NotContain("12345678901")
            .And.NotContain("segredo");
    }

    [Fact]
    public async Task CancelamentoDeComandoSqlComRespostaJaIniciadaEClienteQueFechou_DeveLogarInformationSemAbortar()
    {
        var (contexto, conexao, logger, middleware) = PrepararRespostaIniciada(
            _ => SqlExceptionDeTeste.CancelamentoDeComando(), clienteFechou: true);

        await middleware.InvokeAsync(contexto);

        conexao.Abortes.Should().Be(0);
        var registros = Registros(logger);
        registros.Should().NotContain(r => r.Nivel >= LogLevel.Warning);
        registros.Should().ContainSingle(r => r.Nivel == LogLevel.Information);
    }

    [Fact]
    public async Task DeadlockSqlComRespostaJaIniciadaEClienteQueFechou_DeveLogarErroEAbortar()
    {
        var excecao = SqlExceptionDeTeste.Deadlock();
        var (contexto, conexao, logger, middleware) = PrepararRespostaIniciada(_ => excecao, clienteFechou: true);

        await middleware.InvokeAsync(contexto);

        conexao.Abortes.Should().Be(1);
        Registros(logger).Should().ContainSingle(r => r.Nivel == LogLevel.Error).Which.Excecao.Should().BeSameAs(excecao);
    }

    [Fact]
    public void FakeDeRespostaIniciada_DeveRecusarMudancaDeStatusECabecalhosComoOKestrel()
    {
        var contexto = NovoContexto();
        contexto.Features.Set<IHttpResponseFeature>(new RespostaJaIniciada());

        var mudarStatus = () => contexto.Response.StatusCode = 500;
        var mudarTipo = () => contexto.Response.ContentType = "application/json";

        mudarStatus.Should().Throw<InvalidOperationException>();
        mudarTipo.Should().Throw<InvalidOperationException>();
    }
}
