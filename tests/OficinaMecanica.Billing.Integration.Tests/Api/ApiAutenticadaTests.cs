using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Integration.Tests.Support;

namespace OficinaMecanica.Billing.Integration.Tests.Api;

// JWT RS256 validado pelo JWKS (servido por um Kestrel de teste que faz o papel do Seguranca) e os endpoints
// autenticados de consulta e de ação do staff.
public class ApiAutenticadaTests : IAsyncLifetime
{
    private EmissorDeTokens _emissor = null!;
    private BillingApiFactory _api = null!;
    private HttpClient _cliente = null!;

    public async Task InitializeAsync()
    {
        _emissor = await EmissorDeTokens.IniciarAsync();
        _api = new BillingApiFactory().ComJwks(_emissor);
        _cliente = _api.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _cliente.Dispose();
        await _api.DisposeAsync();
        await _emissor.DisposeAsync();
    }

    private HttpRequestMessage Requisicao(HttpMethod metodo, string url, string? token = null, HttpContent? corpo = null)
    {
        var requisicao = new HttpRequestMessage(metodo, url) { Content = corpo };
        if (token is not null)
            requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return requisicao;
    }

    // ---- Autenticacao ----

    [Theory]
    [InlineData("/api/orcamentos")]
    [InlineData("/api/orcamentos/ordem-de-servico/00000000-0000-0000-0000-000000000001")]
    [InlineData("/api/pagamentos/ordem-de-servico/00000000-0000-0000-0000-000000000001")]
    public async Task Consultas_SemToken_DevemResponder401(string url)
    {
        var resposta = await _cliente.SendAsync(Requisicao(HttpMethod.Get, url));

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Estornar_SemToken_DeveResponder401()
    {
        var resposta = await _cliente.SendAsync(Requisicao(HttpMethod.Post, $"/api/pagamentos/{Guid.NewGuid()}/estornar"));

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Consulta_TokenValidoEmitidoPeloSeguranca_DeveResponder200()
    {
        var resposta = await _cliente.SendAsync(Requisicao(HttpMethod.Get, "/api/orcamentos", _emissor.EmitirToken()));

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Consulta_TokenComLixo_DeveResponder401()
    {
        var resposta = await _cliente.SendAsync(Requisicao(HttpMethod.Get, "/api/orcamentos", "isto-nao-e-um-jwt"));

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Consulta_TokenExpirado_DeveResponder401()
    {
        var token = _emissor.EmitirToken(validade: TimeSpan.FromMinutes(-5));

        var resposta = await _cliente.SendAsync(Requisicao(HttpMethod.Get, "/api/orcamentos", token));

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Consulta_EmissorDiferente_DeveResponder401()
    {
        var token = _emissor.EmitirToken(emissor: "Outro.Emissor");

        var resposta = await _cliente.SendAsync(Requisicao(HttpMethod.Get, "/api/orcamentos", token));

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Consulta_AudienciaDiferente_DeveResponder401()
    {
        var token = _emissor.EmitirToken(audiencia: "Outra.Audiencia");

        var resposta = await _cliente.SendAsync(Requisicao(HttpMethod.Get, "/api/orcamentos", token));

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Consulta_TokenAssinadoPorOutraChaveComOMesmoKid_DeveResponder401()
    {
        using var outraChave = EmissorDeTokens.NovaChaveRsa();
        var token = _emissor.EmitirToken(chave: outraChave);

        var resposta = await _cliente.SendAsync(Requisicao(HttpMethod.Get, "/api/orcamentos", token));

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Consulta_TokenComKidDesconhecido_DeveResponder401()
    {
        var token = _emissor.EmitirToken(keyId: "kid-que-nao-existe-no-jwks");

        var resposta = await _cliente.SendAsync(Requisicao(HttpMethod.Get, "/api/orcamentos", token));

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Autorizacao por metadado: nenhum endpoint de dados fica aberto por esquecimento ----

    [Fact]
    public void TodoEndpointDeControllerExigeAutenticacaoExcetoOWebhookEAsRotasDeDev()
    {
        var fonte = _api.Services.GetRequiredService<EndpointDataSource>();

        var semAutenticacao = fonte.Endpoints
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>() is not null)
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null || !e.Metadata.OfType<IAuthorizeData>().Any())
            .Select(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()!.ControllerName)
            .Distinct()
            .ToList();

        semAutenticacao.Should().BeEquivalentTo("Webhooks", "DevPagamentos");
    }

    // ---- Consultas ----

    [Fact]
    public async Task ObterOrcamento_OrdemSemOrcamento_DeveResponder404()
    {
        var resposta = await _cliente.SendAsync(
            Requisicao(HttpMethod.Get, $"/api/orcamentos/ordem-de-servico/{Guid.NewGuid()}", _emissor.EmitirToken()));

        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ObterOrcamento_Existente_DeveDevolverOOrcamentoComOsItens()
    {
        var orcamento = new OrcamentoBuilder().CriadoEm(_api.Relogio.GetUtcNow()).Gerado();
        await _api.ComBancoAsync(async db =>
        {
            db.Orcamentos.Add(orcamento);
            await db.SaveChangesAsync();
        });

        var resposta = await _cliente.SendAsync(
            Requisicao(HttpMethod.Get, $"/api/orcamentos/ordem-de-servico/{orcamento.OrdemDeServicoId}", _emissor.EmitirToken()));

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        corpo.GetProperty("id").GetGuid().Should().Be(orcamento.Id);
        corpo.GetProperty("status").GetString().Should().Be("Gerado");
        corpo.GetProperty("valorTotal").GetDecimal().Should().Be(orcamento.ValorTotal);
        corpo.GetProperty("itens").GetArrayLength().Should().Be(orcamento.Itens.Count);
    }

    [Fact]
    public async Task ListarOrcamentos_ComPaginaEFiltroDeStatus_DeveDevolverSoOsDoStatusPedido()
    {
        var agora = _api.Relogio.GetUtcNow();
        var gerados = Enumerable.Range(0, 3).Select(_ => new OrcamentoBuilder().CriadoEm(agora).Gerado()).ToList();
        var cancelado = new OrcamentoBuilder().CriadoEm(agora).Cancelado();
        await _api.ComBancoAsync(async db =>
        {
            db.Orcamentos.AddRange(gerados);
            db.Orcamentos.Add(cancelado);
            await db.SaveChangesAsync();
        });

        var resposta = await _cliente.SendAsync(
            Requisicao(HttpMethod.Get, "/api/orcamentos?pagina=1&tamanhoPagina=2&status=Cancelado", _emissor.EmitirToken()));

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        corpo.GetProperty("total").GetInt32().Should().Be(1);
        corpo.GetProperty("itens").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(cancelado.Id);
    }

    [Theory]
    [InlineData("pagina=0")]
    [InlineData("tamanhoPagina=0")]
    [InlineData("tamanhoPagina=1000")]
    public async Task ListarOrcamentos_PaginacaoInvalida_DeveResponder400ComMensagemEmPortugues(string query)
    {
        var resposta = await _cliente.SendAsync(Requisicao(HttpMethod.Get, $"/api/orcamentos?{query}", _emissor.EmitirToken()));

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        corpo.GetProperty("tipo").GetString().Should().Be("Erro de Validação");
        corpo.GetProperty("erros").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ObterPagamento_OrdemSemPagamento_DeveResponder404()
    {
        var resposta = await _cliente.SendAsync(
            Requisicao(HttpMethod.Get, $"/api/pagamentos/ordem-de-servico/{Guid.NewGuid()}", _emissor.EmitirToken()));

        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ObterPagamento_Existente_DeveDevolverStatusLinkETentativas()
    {
        var cenario = await _api.ComBancoAsync(db => CenarioDePagamento.CriarAsync(db, _api.Gateway, _api.Relogio.GetUtcNow()));

        var resposta = await _cliente.SendAsync(
            Requisicao(HttpMethod.Get, $"/api/pagamentos/ordem-de-servico/{cenario.Pagamento.OrdemDeServicoId}", _emissor.EmitirToken()));

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        corpo.GetProperty("id").GetGuid().Should().Be(cenario.Pagamento.Id);
        corpo.GetProperty("status").GetString().Should().Be("Pendente");
        corpo.GetProperty("linkPagamento").GetString().Should().StartWith("https://fake.mercadopago.local/checkout/");
    }

    // ---- Estorno manual pelo staff ----

    [Fact]
    public async Task Estornar_PagamentoComEstornoFalho_DeveRefazerOEstornoEDevolverOPagamentoEstornado()
    {
        var cenario = await _api.ComBancoAsync(db => CenarioDePagamento.CriarAsync(db, _api.Gateway, _api.Relogio.GetUtcNow()));
        var pagamentoDoGateway = _api.Gateway.AprovarPreferencia(cenario.PreferenceId);
        await _api.ComBancoAsync(async db =>
        {
            var pagamento = await db.Pagamentos.Include(p => p.Tentativas).Include(p => p.Estorno).SingleAsync(p => p.Id == cenario.Pagamento.Id);
            pagamento.RegistrarTentativa(pagamentoDoGateway.Id, "approved", "accredited", pagamento.Valor, _api.Relogio.GetUtcNow());
            pagamento.ConfirmarAprovacao(pagamentoDoGateway.Id, pagamento.Valor, "BRL", pagamento.Id.ToString(), _api.Relogio.GetUtcNow());
            pagamento.IniciarEstorno();
            pagamento.FalharEstorno("O gateway recusou o estorno.");
            await db.SaveChangesAsync();
        });

        var resposta = await _cliente.SendAsync(Requisicao(
            HttpMethod.Post, $"/api/pagamentos/{cenario.Pagamento.Id}/estornar", _emissor.EmitirToken(),
            JsonContent.Create(new { motivo = "Autorizado pelo financeiro" })));

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        corpo.GetProperty("status").GetString().Should().Be("Estornado");
        corpo.GetProperty("estorno").GetProperty("gatewayRefundId").GetString().Should().StartWith("fake-refund-");
        var persistido = await _api.ComBancoAsync(db => db.Pagamentos.SingleAsync(p => p.Id == cenario.Pagamento.Id));
        persistido.Status.Should().Be(StatusPagamento.Estornado);
    }

    [Fact]
    public async Task Estornar_SemCorpo_DeveUsarOMotivoPadraoEFuncionar()
    {
        var cenario = await _api.ComBancoAsync(db => CenarioDePagamento.CriarAsync(db, _api.Gateway, _api.Relogio.GetUtcNow()));
        var pagamentoDoGateway = _api.Gateway.AprovarPreferencia(cenario.PreferenceId);
        await _api.ComBancoAsync(async db =>
        {
            var pagamento = await db.Pagamentos.Include(p => p.Tentativas).Include(p => p.Estorno).SingleAsync(p => p.Id == cenario.Pagamento.Id);
            pagamento.RegistrarTentativa(pagamentoDoGateway.Id, "approved", "accredited", pagamento.Valor, _api.Relogio.GetUtcNow());
            pagamento.ConfirmarAprovacao(pagamentoDoGateway.Id, pagamento.Valor, "BRL", pagamento.Id.ToString(), _api.Relogio.GetUtcNow());
            pagamento.IniciarEstorno();
            pagamento.FalharEstorno("O gateway recusou o estorno.");
            await db.SaveChangesAsync();
        });

        var resposta = await _cliente.SendAsync(
            Requisicao(HttpMethod.Post, $"/api/pagamentos/{cenario.Pagamento.Id}/estornar", _emissor.EmitirToken()));

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Estornar_PagamentoQueNaoFalhou_DeveResponder400()
    {
        var cenario = await _api.ComBancoAsync(db => CenarioDePagamento.CriarAsync(db, _api.Gateway, _api.Relogio.GetUtcNow()));

        var resposta = await _cliente.SendAsync(
            Requisicao(HttpMethod.Post, $"/api/pagamentos/{cenario.Pagamento.Id}/estornar", _emissor.EmitirToken()));

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resposta.Content.ReadAsStringAsync()).Should().Contain("estorno falho");
    }

    [Fact]
    public async Task Estornar_PagamentoInexistente_DeveResponder404()
    {
        var resposta = await _cliente.SendAsync(
            Requisicao(HttpMethod.Post, $"/api/pagamentos/{Guid.NewGuid()}/estornar", _emissor.EmitirToken()));

        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Estornar_MotivoMuitoLongo_DeveResponder400()
    {
        var resposta = await _cliente.SendAsync(Requisicao(
            HttpMethod.Post, $"/api/pagamentos/{Guid.NewGuid()}/estornar", _emissor.EmitirToken(),
            JsonContent.Create(new { motivo = new string('x', 501) })));

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
