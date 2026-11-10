using Bogus;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Domain.Entities;

namespace OficinaMecanica.Billing.Application.Tests.Builders;

public class PagamentoGatewayDtoBuilder
{
    private static readonly Faker Faker = new("pt_BR");

    private string _id = Faker.Random.Long(1_000_000_000, 9_999_999_999).ToString();
    private string _status = "approved";
    private string? _statusDetalhe = "accredited";
    private decimal _valor = Math.Round(Faker.Random.Decimal(50m, 2000m), 2);
    private string _moeda = "BRL";
    private string? _externalReference = Guid.NewGuid().ToString();

    public PagamentoGatewayDtoBuilder DoPagamento(Pagamento pagamento)
    {
        _valor = pagamento.Valor;
        _externalReference = pagamento.Id.ToString();
        return this;
    }

    public PagamentoGatewayDtoBuilder ComId(string id) { _id = id; return this; }
    public PagamentoGatewayDtoBuilder ComStatus(string status, string? statusDetalhe = null) { _status = status; _statusDetalhe = statusDetalhe; return this; }
    public PagamentoGatewayDtoBuilder ComValor(decimal valor) { _valor = valor; return this; }
    public PagamentoGatewayDtoBuilder ComMoeda(string moeda) { _moeda = moeda; return this; }
    public PagamentoGatewayDtoBuilder ComReferenciaExterna(string? externalReference) { _externalReference = externalReference; return this; }

    public PagamentoGatewayDto Build() => new(_id, _status, _statusDetalhe, _valor, _moeda, _externalReference);
}
