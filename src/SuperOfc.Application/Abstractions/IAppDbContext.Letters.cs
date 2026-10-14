using Microsoft.EntityFrameworkCore;
using SuperOfc.Domain.Letters;

namespace SuperOfc.Application.Abstractions;

public partial interface IAppDbContext
{
    DbSet<LetterTemplate> LetterTemplates { get; }

    DbSet<IssuedLetter> IssuedLetters { get; }
}
