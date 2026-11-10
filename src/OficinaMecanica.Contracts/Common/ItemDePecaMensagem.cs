namespace OficinaMecanica.Contracts.Common;

public sealed record ItemDePecaMensagem(Guid PecaId, string Nome, decimal PrecoUnitario, int Quantidade);
