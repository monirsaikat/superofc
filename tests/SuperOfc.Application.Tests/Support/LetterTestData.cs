using SuperOfc.Application.Letters;
using SuperOfc.Application.Setup;
using SuperOfc.Domain.Letters;

namespace SuperOfc.Application.Tests.Support;

/// <summary>Letter helpers shared by the letter tests.</summary>
public static class LetterTestData
{
    public const string Signature = "\n\nFor **{Company.Name}**\n\n\n\nAuthorised signatory";

    /// <summary>The text salary certificate that versions before 1.0.0-beta.5 created (now a Word letter).</summary>
    public const string LegacySalaryBody =
        "To whom it may concern\n\nThis is to certify that **{Employee.Name}** ({Employee.Code}) has been working with {Company.Name} as "
        + "{Employee.Designation} since {Employee.JoinDate}.\n\n{Employee.HisHer} current monthly gross salary is **{Salary.Gross}** "
        + "({Salary.GrossInWords}).\n\nThis certificate is issued at {Employee.HisHer} request for {Input.Purpose}." + Signature;

    /// <summary>The text experience certificate that versions before 1.0.0-beta.5 created (now a Word letter).</summary>
    public const string LegacyExperienceBody =
        "To whom it may concern\n\nThis is to certify that **{Employee.Name}** ({Employee.Code}) worked with {Company.Name} from "
        + "**{Employee.JoinDate}** to **{Employee.ExitDate}**, a period of {Employee.Service}. At the time of leaving, {Employee.HeShe} was "
        + "{Employee.Designation} in the {Employee.Department} department.\n\nWe wish {Employee.HimHer} success in the future." + Signature;

    /// <summary>
    /// Replaces the standard Word letter <paramref name="name"/> ("Salary certificate" or "Experience certificate") with the
    /// text certificate earlier versions provided under the same name, for tests of text-template behaviour.
    /// </summary>
    public static async Task<Guid> TextCertificateAsync(TestHost host, string name)
    {
        var seeded = (await host.SendOkAsync(new GetLetterTemplatesQuery())).Single(t => t.Name == name);
        seeded.IsWord.Should().BeTrue("the standard {0} is a Word letter", name);
        await host.SendOkAsync(new DeleteLetterTemplateCommand(seeded.Id));
        var (kind, body) = name switch
        {
            "Salary certificate" => (LetterKind.SalaryCertificate, LegacySalaryBody),
            "Experience certificate" => (LetterKind.ExperienceCertificate, LegacyExperienceBody),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Only the two former text certificates exist."),
        };
        return await host.SendOkAsync(new SaveLetterTemplateCommand(null, name, kind, name, body, "HR", true));
    }

    /// <summary>Sets who signs letters in Settings > Company (the standard Word letters print both).</summary>
    public static async Task SetSignatoryAsync(TestHost host, string? name = "Md. Hasibur Rahman", string? title = "Head of Operations")
    {
        var c = await host.SendOkAsync(new GetCompanyQuery());
        await host.SendOkAsync(new UpdateCompanyCommand(
            c.Name, c.Address, c.Phone, c.Email, c.TaxIdentifier, c.LogoPath, c.CurrencyCode, c.CurrencySymbol, c.Grouping,
            c.TimeZoneId, c.FiscalYearStartMonth, c.DateFormat, c.WorkingWeek, name, title));
    }
}
