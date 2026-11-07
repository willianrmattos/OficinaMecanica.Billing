using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OficinaMecanica.Billing.API.Middleware;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Domain.Exceptions;

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
}
