using System.IO;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SuperOfc.Application.Common;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Letters;
using SuperOfc.Application.Organization;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Tests.Support;
using SuperOfc.Desktop.ViewModels.Letters;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Organization;

namespace SuperOfc.Desktop.Tests;

/// <summary>
/// Word letter templates through the view models (docs/specs/word-letter-templates.md): real Open XML filling, a fake Word for PDFs,
/// and the converted sample in doc-files/templates.
/// </summary>
public class WordLettersFlowTests
{
    private const string PdfFilter = "PDF (*.pdf)|*.pdf";

    /// <summary>Stands in for Microsoft Word: "converts" by copying the .docx after a PDF marker.</summary>
    private sealed class FakeWordPdfConverter : IWordPdfConverter
    {
        public static readonly byte[] Marker = "%PDF-fake\n"u8.ToArray();

        public bool IsAvailable { get; set; } = true;

        public Task ConvertAsync(IReadOnlyList<WordPdfJob> jobs, CancellationToken cancellationToken = default)
        {
            if (!IsAvailable)
            {
                throw new WordConversionException("Microsoft Word is not installed.");
            }

            foreach (var job in jobs)
            {
                File.WriteAllBytes(job.PdfPath, [.. Marker, .. File.ReadAllBytes(job.DocxPath)]);
            }

            return Task.CompletedTask;
        }
    }

    private static Task<DesktopTestHost> CreateHostAsync(bool wordInstalled)
        => DesktopTestHost.CreateAsync(completeSetup: true,
            overrides: s => s.AddSingleton<IWordPdfConverter>(new FakeWordPdfConverter { IsAvailable = wordInstalled }));

    /// <summary>Copies a converted template from doc-files/templates into the test folder.</summary>
    private static string Sample(DesktopTestHost host, string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SuperOfc.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the tests run inside the repository");
        var target = Path.Combine(host.Root, "upload", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(dir!.FullName, "doc-files", "templates", fileName), target, overwrite: true);
        return target;
    }

    /// <summary>Two Accounts Officers with a father's name who left on 9 March 2025, so the experience certificate has no blanks.</summary>
    private static async Task<(Guid Karim, Guid Rafi)> EmployeesAsync(DesktopTestHost host)
    {
        var dispatcher = host.Get<IDispatcher>();
        var officer = await dispatcher.SendAsync(new SaveMasterDataCommand(MasterDataKind.Designation, null, "Accounts Officer", "AO", "Prepare accounts", 0));
        officer.IsSuccess.Should().BeTrue(officer.ToString());

        async Task<Guid> CreateAsync(string code, string name, string father)
        {
            var input = new EmployeeInput(code, name, Gender.Male, null, null, null, null, null, null, new DateOnly(2023, 6, 1),
                null, officer.Value, null, null, PaymentMode.Cash, null, null, null, null, true, null, FatherName: father);
            var created = await dispatcher.SendAsync(new CreateEmployeeCommand(input));
            created.IsSuccess.Should().BeTrue(created.ToString());
            var exit = await dispatcher.SendAsync(new RecordEmploymentEventCommand(created.Value, EmploymentAction.Exit, new DateOnly(2025, 3, 9), "Resigned"));
            exit.IsSuccess.Should().BeTrue(exit.ToString());
            return created.Value;
        }

        return (await CreateAsync("E1", "Karim Uddin", "Abdul Karim"), await CreateAsync("E2", "Rafi Ahmed", "Nurul Ahmed"));
    }

    private static async Task<Guid> ImportAsync(DesktopTestHost host)
    {
        var result = await host.Get<IDispatcher>().SendAsync(new ImportLetterTemplateDocumentCommand(null, Sample(host, "Experience certificate.docx")));
        result.IsSuccess.Should().BeTrue(result.ToString());
        return result.Value;
    }

    private static bool IsDocx(string path) => File.ReadAllBytes(path).AsSpan().StartsWith("PK"u8);

    [Fact]
    public async Task Settings_add_edit_in_word_use_saved_changes_sample_and_remove()
    {
        await using var host = await CreateHostAsync(wordInstalled: true);
        await host.SignInAsync();
        var (karim, _) = await EmployeesAsync(host);
        var page = host.Get<LetterTemplatesSettingsViewModel>();
        page.PreviewDelay = TimeSpan.Zero;
        await page.OnNavigatedToAsync(null);
        page.IsWord.Should().BeFalse("the standard letters are text templates");

        host.Files.OpenFile(default!, default).ReturnsForAnyArgs(Sample(host, "Experience certificate.docx"));
        await page.AddWordCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().BeNull();
        host.Files.Received().OpenFile(Strings.Letters_WordFilter, Arg.Any<string?>());
        var added = page.Selected.Should().NotBeNull().And.Subject.As<LetterTemplateDto>();
        added.Name.Should().Be("Experience certificate (2)", "the standard letters already use the name");
        added.IsWord.Should().BeTrue();
        page.IsWord.Should().BeTrue();
        page.IsText.Should().BeFalse();
        page.DocumentFileName.Should().Be("Experience certificate.docx");
        page.WordFileText.Should().Contain("Experience certificate.docx");
        page.Body.Should().Contain("{Employee.FatherName}", "the body holds the document text");

        // Fields are copied for Word, not inserted into a box.
        string? copied = null;
        string? inserted = null;
        page.CopyRequested += (_, token) => copied = token;
        page.InsertRequested += (_, token) => inserted = token;
        page.InsertCommand.Execute(page.Fields.First(f => f.Key == "Employee.Name"));
        copied.Should().Be("{Employee.Name}");
        inserted.Should().BeNull();

        // The text preview still fills the document text for the sample employee.
        page.SampleEmployeeId = karim;
        await page.RefreshPreviewAsync();
        page.PreviewBody.Should().Contain("Karim Uddin").And.Contain("Abdul Karim");

        // Edit in Word exports to a stable temp file and opens it.
        page.HasEditedFile.Should().BeFalse();
        await page.EditInWordCommand.ExecuteAsync(null);
        var edited = page.EditedPath.Should().NotBeNull().And.Subject.As<string>();
        edited.Should().EndWith(".docx").And.Contain(Path.Combine("SuperOfc", "templates"));
        File.ReadAllBytes(edited).Should().Equal(File.ReadAllBytes(Sample(host, "Experience certificate.docx")));
        host.Files.Received().ShellOpen(edited);
        page.HasEditedFile.Should().BeTrue();

        // "Saving in Word": another letter's layout replaces the file, then "Use saved changes" uploads it into the same template.
        File.Copy(Sample(host, "NOC letter.docx"), edited, overwrite: true);
        await page.UseSavedChangesCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().BeNull();
        page.Selected!.Id.Should().Be(added.Id);
        page.Selected.DocumentFileName.Should().Be(Path.GetFileName(edited));
        page.Body.Should().NotContain("{Employee.FatherName}");
        page.Templates.Should().ContainSingle(t => t.Id == added.Id);
        page.HasEditedFile.Should().BeTrue("the file can be edited and used again");

        // Name and subject stay editable; the body is the document's.
        page.Name = "Experience (Word)";
        page.Subject = "Experience certificate of {Employee.Name}";
        await page.SaveCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().BeNull();
        page.Templates.Single(t => t.Id == added.Id).Should().Match<LetterTemplateDto>(t => t.Name == "Experience (Word)" && t.IsWord);

        // Replace with a file chosen on disk.
        await page.ReplaceWordCommand.ExecuteAsync(null);
        page.Selected!.DocumentFileName.Should().Be("Experience certificate.docx");
        page.Body.Should().Contain("{Employee.FatherName}");

        // Open sample: the filled .docx for the sample employee.
        host.Files.ClearReceivedCalls();
        await page.OpenSampleCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().BeNull();
        var sample = host.Files.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(host.Files.ShellOpen)).Select(c => (string)c.GetArguments()[0]!).Single();
        sample.Should().EndWith(".docx");
        IsDocx(sample).Should().BeTrue();

        // Remove the Word file after confirming: the template keeps the text.
        host.Dialogs.ConfirmAsync(default!, default!, default!, default).ReturnsForAnyArgs(true);
        await page.RemoveWordCommand.ExecuteAsync(null);
        page.IsWord.Should().BeFalse();
        page.HasEditedFile.Should().BeFalse();
        page.Selected!.Should().Match<LetterTemplateDto>(t => t.Id == added.Id && !t.IsWord && t.DocumentFileName == null);
        page.Body.Should().Contain("{Employee.FatherName}");
    }

    [Fact]
    public async Task Without_word_letters_are_saved_as_docx_one_per_person_and_reprinted_as_docx()
    {
        await using var host = await CreateHostAsync(wordInstalled: false);
        var shell = await host.SignInAsync();
        var (karim, rafi) = await EmployeesAsync(host);
        var template = await ImportAsync(host);

        var tab = host.Get<EmployeeLettersTabViewModel>();
        await tab.LoadAsync(karim);
        await tab.NewCommand.ExecuteAsync(null);
        var page = shell.CurrentPage.Should().BeOfType<LettersViewModel>().Subject;
        page.Template = page.Templates.Single(t => t.Id == template);
        page.WordInstalled.Should().BeFalse();
        page.IsWordTemplate.Should().BeTrue();
        page.ShowPdfNeedsWord.Should().BeTrue();
        page.Output.Should().Be(LetterOutput.Word, "PDFs of Word templates need Word");
        page.IssueText.Should().Be(Strings.Letters_IssueWord);
        page.PerEmployeeForced.Should().BeFalse("one person gets one file");

        // One person: one .docx at the chosen path.
        var one = Path.Combine(host.Root, "karim.docx");
        host.Files.SaveFile(default!, default!).ReturnsForAnyArgs(one);
        await page.IssueCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().BeNull();
        host.Files.Received().SaveFile(Strings.Letters_WordFilter, Arg.Is<string>(n => n.EndsWith("E1 Karim Uddin.docx", StringComparison.Ordinal)));
        IsDocx(one).Should().BeTrue();
        host.Files.Received().ShellOpen(one);
        var issued = page.Issued.Should().ContainSingle().Subject;
        issued.HasDocument.Should().BeTrue();

        // Several people: one file each in the chosen folder, the tick box forced on.
        page.SelectAllCommand.Execute(true);
        page.SelectedIds.Should().BeEquivalentTo([karim, rafi]);
        page.PerEmployeeForced.Should().BeTrue();
        page.PerEmployeeChecked.Should().BeTrue();
        page.PerEmployeeEnabled.Should().BeFalse();
        page.OneFilePerEmployee.Should().BeFalse("the forced tick is not the person's choice");
        var folder = Path.Combine(host.Root, "out");
        Directory.CreateDirectory(folder);
        host.Files.PickFolder(default!).ReturnsForAnyArgs(folder);
        await page.IssueCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().BeNull();
        Directory.GetFiles(folder).Select(Path.GetFileName).Should().BeEquivalentTo(
            [$"{page.Template!.Name} E1 Karim Uddin.docx", $"{page.Template.Name} E2 Rafi Ahmed.docx"]);
        host.Files.Received().OpenFolder(folder);
        page.Issued.Should().HaveCount(3);

        // Asking for PDF without Word is refused before anything is numbered.
        page.Output = LetterOutput.Pdf;
        host.Files.SaveFile(default!, default!).ReturnsForAnyArgs(Path.Combine(host.Root, "x.pdf"));
        page.SelectAllCommand.Execute(false);
        page.People.Single(p => p.Employee.Id == karim).IsSelected = true;
        await page.IssueCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().Contain("Microsoft Word");
        page.Issued.Should().HaveCount(3);

        // Reprint from the employee tab: only Word is offered, and the kept file comes back.
        await tab.LoadAsync(karim);
        var first = tab.Letters.Single(l => l.Number == issued.Number);
        var copy = Path.Combine(host.Root, "copy.docx");
        host.Files.ClearReceivedCalls();
        host.Files.SaveFile(default!, default!).ReturnsForAnyArgs(copy);
        await tab.ReprintCommand.ExecuteAsync(first);
        tab.ErrorMessage.Should().BeNull();
        host.Files.Received().SaveFile(Strings.Letters_WordFilter, Arg.Is<string>(n => n.EndsWith(".docx", StringComparison.Ordinal)));
        File.ReadAllBytes(copy).Should().Equal(File.ReadAllBytes(one));

        // The register reprints it too.
        var again = Path.Combine(host.Root, "again.docx");
        host.Files.SaveFile(default!, default!).ReturnsForAnyArgs(again);
        await page.ReprintCommand.ExecuteAsync(page.Issued.Single(l => l.Id == issued.Id));
        File.ReadAllBytes(again).Should().Equal(File.ReadAllBytes(one));
    }

    [Fact]
    public async Task With_word_installed_letters_default_to_pdf_and_reprints_offer_both()
    {
        await using var host = await CreateHostAsync(wordInstalled: true);
        var shell = await host.SignInAsync();
        var (karim, _) = await EmployeesAsync(host);
        var template = await ImportAsync(host);

        var page = host.Get<LettersViewModel>();
        await page.OnNavigatedToAsync(new LettersTarget([karim], template));
        page.Template!.Id.Should().Be(template);
        page.WordInstalled.Should().BeTrue();
        page.ShowPdfNeedsWord.Should().BeFalse();
        page.Output.Should().Be(LetterOutput.Pdf);

        // Open sample: the filled .docx for the first ticked person.
        await page.OpenSampleCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().BeNull();
        var sample = host.Files.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(host.Files.ShellOpen)).Select(c => (string)c.GetArguments()[0]!).Single();
        IsDocx(sample).Should().BeTrue();
        page.Issued.Should().BeEmpty("a sample is not a letter");

        var pdf = Path.Combine(host.Root, "karim.pdf");
        host.Files.SaveFile(default!, default!).ReturnsForAnyArgs(pdf);
        await page.IssueCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().BeNull();
        host.Files.Received().SaveFile(PdfFilter, Arg.Is<string>(n => n.EndsWith(".pdf", StringComparison.Ordinal)));
        File.ReadAllBytes(pdf).AsSpan().StartsWith(FakeWordPdfConverter.Marker).Should().BeTrue();

        // A text template ignores the Word choice and stays PDF.
        page.Output = LetterOutput.Word;
        page.Template = page.Templates.First(t => !t.IsWord);
        page.Output.Should().Be(LetterOutput.Pdf);
        page.IsWordTemplate.Should().BeFalse();
        page.SelectAllCommand.Execute(true);
        page.PerEmployeeForced.Should().BeFalse("text letters can share one PDF");

        host.Files.ClearReceivedCalls();
        host.Files.SaveFile(default!, default!).ReturnsForAnyArgs(Path.Combine(host.Root, "copy.pdf"));
        await page.ReprintCommand.ExecuteAsync(page.Issued.Single());
        page.ErrorMessage.Should().BeNull();
        host.Files.Received().SaveFile(Strings.Letters_PdfOrWordFilter, Arg.Is<string>(n => n.EndsWith(".pdf", StringComparison.Ordinal)));
        File.ReadAllBytes(Path.Combine(host.Root, "copy.pdf")).AsSpan().StartsWith(FakeWordPdfConverter.Marker).Should().BeTrue();
        shell.Should().NotBeNull();
    }
}
