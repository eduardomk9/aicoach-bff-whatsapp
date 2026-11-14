namespace AiCoachBff.Api.Services;

/// <summary>
/// Normalização de telefone (R20 F2): o BFF fala o wa_id da Meta — digits-only, com DDI
/// (ex.: 5541999998888). A API resolve os candidatos (mesmo número sem DDI, etc.) pelo
/// PhoneNormalizer do lado de lá; aqui o canônico é o wa_id.
/// </summary>
public static class PhoneNormalizer
{
    /// <summary>Somente dígitos do telefone, como vieram.</summary>
    public static string Canonical(string phone) => new(phone.Where(char.IsDigit).ToArray());
}
