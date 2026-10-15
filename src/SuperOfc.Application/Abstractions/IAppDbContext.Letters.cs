using Microsoft.EntityFrameworkCore;
using SuperOfc.Domain.Letters;

namespace SuperOfc.Application.Abstractions;

public partial interface IAppDbContext
{
    DbSet<LetterTemplate> LetterTemplates { get; }

    DbSet<IssuedLetter> IssuedLetters { get; }

    /// <summary>Filled Word files of letters issued from Word templates; load only for reprints.</summary>
    DbSet<IssuedLetterDocument> IssuedLetterDocuments { get; }
}
