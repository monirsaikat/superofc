using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Organization;

namespace SuperOfc.Application.Abstractions;

public partial interface IAppDbContext
{
    DbSet<Department> Departments { get; }

    DbSet<Designation> Designations { get; }

    DbSet<Location> Locations { get; }

    DbSet<Grade> Grades { get; }

    DbSet<Holiday> Holidays { get; }

    DbSet<Employee> Employees { get; }

    DbSet<EmploymentEvent> EmploymentEvents { get; }

    DbSet<EmployeeDocument> EmployeeDocuments { get; }

    /// <summary>Adds an entity of any mapped type (used by generic handlers).</summary>
    EntityEntry Add(object entity);
}

/// <summary>Reads and writes simple spreadsheets (Excel or CSV) for import and export.</summary>
public interface ISpreadsheetService
{
    /// <summary>Reads the first sheet; the first row is the header. Keys are header texts (case-insensitive).</summary>
    IReadOnlyList<IReadOnlyDictionary<string, string>> Read(string path);

    /// <summary>Writes one or more sheets. Values may be string, number, DateOnly, DateTime or bool.</summary>
    void Write(string path, IEnumerable<SheetData> sheets);
}

public sealed record SheetData(string Name, IReadOnlyList<string> Headers, IEnumerable<IReadOnlyList<object?>> Rows);
